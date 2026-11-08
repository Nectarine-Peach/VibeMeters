using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal interface ICodexAppServerUsage
{
    Task<UsageSnapshot?> FetchAsync(bool refreshToken, string? expectedAccountId, CancellationToken cancellationToken);
}

/// <summary>Queries account and quota through the official CLI without requesting auth tokens or a session.</summary>
internal sealed class CodexAppServerUsage(string configDirectory) : ICodexAppServerUsage
{
    public async Task<UsageSnapshot?> FetchAsync(bool refreshToken, string? expectedAccountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = OfficialCliProcess.CreateStartInfo("codex", "-s", "read-only", "-a", "never", "app-server");
        if (start is null) return null;
        start.Environment["CODEX_HOME"] = Path.GetFullPath(configDirectory);
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = Encoding.UTF8;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var process = new Process { StartInfo = start };
        var started = false;
        Task? errorDrain = null;
        try
        {
            if (!(started = process.Start())) return null;
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            return await ReadUsageAsync(process.StandardInput, process.StandardOutput, refreshToken, expectedAccountId,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally
        {
            if (started)
            {
                // Let app-server exit on EOF before terminating a stuck process. The
                // official CLI owns any managed token rotation and its persistence.
                try { process.StandardInput.Close(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException
                    or System.ComponentModel.Win32Exception) { OfficialCliProcess.Stop(process); }
                deadline.Cancel();
                if (errorDrain is not null)
                {
                    try { await errorDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }
    }

    internal static async Task<UsageSnapshot?> ReadUsageAsync(TextWriter input, TextReader output, bool refreshToken,
        string? expectedAccountId, CancellationToken cancellationToken)
    {
        var rpc = new AppServerRpc(input, output);
        var version = typeof(CodexAppServerUsage).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var initialized = await rpc.RequestAsync(1, "initialize", new
        {
            clientInfo = new { name = "vibemeters", title = "VibeMeters", version }
        }, cancellationToken).ConfigureAwait(false);
        if (!Object(initialized, "result", out _)) return null;
        await rpc.SendAsync(new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
        var accountResponse = await rpc.RequestAsync(2, "account/read", new { refreshToken }, cancellationToken).ConfigureAwait(false);
        if (Object(accountResponse, "error", out var accountError)) return ParseError(accountError);
        if (!Object(accountResponse, "result", out var accountResult) || !accountResult.TryGetProperty("account", out var account))
            return null;
        if (account.ValueKind == JsonValueKind.Null)
            return Failure(UsageStatus.NotConnected, "Codex CLI에서 ChatGPT 계정으로 로그인해 주세요.");
        if (String(account, "type") != "chatgpt")
            return Failure(UsageStatus.Unavailable, "Codex에서 ChatGPT 구독 계정의 사용량을 확인할 수 없습니다.");

        var quotaResponse = await rpc.RequestAsync(3, "account/rateLimits/read", new { excludeResetCreditDetails = true },
            cancellationToken).ConfigureAwait(false);
        if (Object(quotaResponse, "error", out var quotaError)) return ParseError(quotaError);
        if (!Object(quotaResponse, "result", out var quota)) return null;
        var accountId = String(quota, "accountId");
        if (!string.IsNullOrWhiteSpace(expectedAccountId) && !string.IsNullOrWhiteSpace(accountId)
            && !string.Equals(accountId, expectedAccountId, StringComparison.Ordinal))
            return Failure(UsageStatus.Unavailable, "Codex 조회 중 계정이 변경되었습니다. 현재 계정으로 다시 확인합니다.");
        return ParseQuota(quota, String(account, "planType"));
    }

    private static UsageSnapshot? ParseQuota(JsonElement quota, string? plan)
    {
        if (!(Object(quota, "rateLimitsByLimitId", out var buckets) && Object(buckets, "codex", out var bucket))
            && !Object(quota, "rateLimits", out bucket)) return null;

        Object(bucket, "primary", out var primary);
        Object(bucket, "secondary", out var secondary);
        Object(bucket, "individualLimit", out var individual);
        Object(bucket, "credits", out var credits);
        // Normalize app-server's wire names to the existing Codex quota parser.
        var normalized = JsonSerializer.SerializeToElement(new
        {
            plan_type = String(bucket, "planType") ?? plan,
            rate_limit = new { primary_window = MapWindow(primary), secondary_window = MapWindow(secondary) },
            credits = new
            {
                has_credits = Value(credits, "hasCredits"),
                unlimited = Value(credits, "unlimited")
            },
            spend_control = new
            {
                individual_limit = new { remaining_percent = Value(individual, "remainingPercent"), reset_at = Value(individual, "resetsAt") }
            }
        });
        return UsageParser.Parse(UsageProviderKind.Codex, normalized, DateTimeOffset.UtcNow);
    }

    private static object MapWindow(JsonElement window)
    {
        var minutes = Value(window, "windowDurationMins");
        double? seconds = minutes is { ValueKind: JsonValueKind.Number } number
            && number.TryGetDouble(out var value) && value is > 0 and <= 525600 ? value * 60 : null;
        return new
        {
            used_percent = Value(window, "usedPercent"),
            limit_window_seconds = seconds,
            reset_at = Value(window, "resetsAt")
        };
    }

    private static UsageSnapshot ParseError(JsonElement error)
    {
        // The server can wrap a backend error in its message. Use it only to
        // classify known conditions and keep the raw RPC details out of the UI.
        var message = String(error, "message") ?? "";
        if (message.Contains("429 Too Many Requests", StringComparison.OrdinalIgnoreCase))
            return Failure(UsageStatus.RateLimited, "Codex 사용량 조회 요청이 제한되었습니다. 잠시 후 다시 확인합니다.");
        if (message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication required to read rate limits", StringComparison.OrdinalIgnoreCase))
            return Failure(UsageStatus.AuthenticationRequired, "Codex CLI에서 ChatGPT 계정으로 다시 로그인해 주세요.");
        return Failure(UsageStatus.Unavailable, "Codex CLI에서 사용량을 확인할 수 없습니다. 잠시 후 다시 확인합니다.");
    }

    private static UsageSnapshot Failure(UsageStatus status, string message) =>
        new(UsageProviderKind.Codex, status, [], DateTimeOffset.UtcNow, Message: message);

    private static JsonElement? Value(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : null;

    private static string? String(JsonElement root, string key) =>
        Value(root, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Object(JsonElement root, string key, out JsonElement result)
    {
        result = Value(root, key) ?? default;
        return result.ValueKind == JsonValueKind.Object;
    }

    private sealed class AppServerRpc(TextWriter input, TextReader output)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position, _length, _total, _messages;

        internal async Task SendAsync<T>(T message, CancellationToken cancellationToken)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task<JsonElement> RequestAsync(int id, string method, object parameters, CancellationToken cancellationToken)
        {
            await SendAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            while (++_messages <= 128)
            {
                using var json = JsonDocument.Parse(await ReadLineAsync(cancellationToken).ConfigureAwait(false));
                var message = json.RootElement;
                if (message.ValueKind != JsonValueKind.Object) throw new IOException("Invalid app-server message.");
                if (!message.TryGetProperty("id", out var responseId)) continue; // Unsolicited notifications.
                if (message.TryGetProperty("method", out _))
                    throw new IOException("Unexpected request from app-server.");
                if (responseId.ValueKind == JsonValueKind.Number && responseId.TryGetInt32(out var number) && number == id)
                    return message.Clone();
            }
            throw new IOException("Too many app-server messages.");
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await output.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0)
                    {
                        if (line.Length != 0) return line.ToString();
                        throw new IOException("App-server stream closed.");
                    }
                }
                var character = _buffer[_position++];
                if (++_total > 1024 * 1024 || line.Length > 128 * 1024) throw new IOException("App-server output exceeds limit.");
                if (character == '\n') return line.ToString();
                if (character != '\r') line.Append(character);
            }
        }
    }
}

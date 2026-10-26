using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>Official ACP initialize + billing only. No session, prompt, authenticate, or token handling.</summary>
internal static class GrokCliBridge
{
    internal static async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var start = OfficialCliProcess.CreateStartInfo("grok", "agent", "--no-leader", "stdio");
        if (start is null) return Failure(UsageStatus.NotConnected, "공식 Grok Build CLI를 설치한 뒤 로그인해 주세요.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        Task? errorDrain = null;
        try
        {
            if (!process.Start()) return Failure(UsageStatus.Unavailable, "Grok Build 조회를 시작할 수 없습니다.");
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var reader = new BoundedLines(process.StandardOutput);
            await SendAsync(process.StandardInput, InitializeRequest(), deadline.Token).ConfigureAwait(false);
            var initialized = await ReadResponseAsync(reader, 1, deadline.Token).ConfigureAwait(false);
            if (Object(initialized, "error", out var initError)) return ParseError(initError);
            if (!Object(initialized, "result", out var initialization)
                || !initialization.TryGetProperty("protocolVersion", out var protocol)
                || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != 1)
                return Failure(UsageStatus.Unavailable, "Grok Build가 지원되는 조회 프로토콜을 제공하지 않습니다.");
            await SendAsync(process.StandardInput, BillingRequest(), deadline.Token).ConfigureAwait(false);
            var response = await ReadResponseAsync(reader, 2, deadline.Token).ConfigureAwait(false);
            if (Object(response, "error", out var error)) return ParseError(error);
            return Object(response, "result", out var result) ? Parse(result)
                : Failure(UsageStatus.Unavailable, "Grok Build 사용량 응답 형식을 확인할 수 없습니다.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(UsageStatus.Unavailable, "Grok Build 사용량 조회 시간이 초과되었습니다. 잠시 후 새로고침해 주세요."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Failure(UsageStatus.Unavailable, "Grok Build 사용량을 가져올 수 없습니다. 공식 CLI 상태를 확인해 주세요."); }
        finally
        {
            OfficialCliProcess.Stop(process);
            if (errorDrain is not null)
                try { await errorDrain.ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static string InitializeRequest() => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id = 1, method = "initialize", @params = new
        {
            protocolVersion = 1, clientCapabilities = new { }, clientInfo = new { name = "VibeMeters", version = ClientVersion },
            _meta = new { startupHints = new { nonInteractive = true }, clientType = "VibeMeters", clientVersion = ClientVersion }
        }
    });
    private static string ClientVersion => typeof(GrokCliBridge).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private static string BillingRequest() => "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"_x.ai/billing\",\"params\":{}}";

    private static async Task SendAsync(StreamWriter writer, string value, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResponseAsync(BoundedLines reader, int expectedId, CancellationToken cancellationToken)
    {
        for (var count = 0; count < 128; count++)
        {
            var line = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new IOException("CLI stream closed.");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (Text(root, "jsonrpc") != "2.0") throw new IOException("Invalid RPC envelope.");
            if (root.TryGetProperty("id", out var id))
            {
                if (Text(root, "method") is not null) throw new IOException("Unexpected interactive RPC request.");
                if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == expectedId) return root.Clone();
            }
        }
        throw new IOException("Too many RPC messages.");
    }

    private static UsageSnapshot Parse(JsonElement root)
    {
        if (!Object(root, "config", out var config) && Object(root, "result", out var nested)) root = nested;
        if (!Object(root, "config", out config) || !config.TryGetProperty("creditUsagePercent", out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var used)
            || !double.IsFinite(used) || used < 0 || used > 100)
            return Failure(UsageStatus.Unavailable, "Grok Build에서 지원되는 구독 사용 비율을 제공하지 않습니다.");
        var unified = config.TryGetProperty("isUnifiedBillingUser", out var flag) && flag.ValueKind == JsonValueKind.True;
        var period = "한도";
        DateTimeOffset? reset = null, beginning = null;
        if (Object(config, "currentPeriod", out var current))
        {
            period = Text(current, "type")?.ToUpperInvariant() switch
            {
                "USAGE_PERIOD_TYPE_WEEKLY" or "WEEKLY" => "주간",
                "USAGE_PERIOD_TYPE_MONTHLY" or "MONTHLY" => "월간",
                "USAGE_PERIOD_TYPE_DAILY" or "DAILY" => "일간", _ => "한도"
            };
            reset = Timestamp(Text(current, "end")); beginning = Timestamp(Text(current, "start"));
        }
        else if (Object(config, "monthlyLimit", out var monthly) && monthly.TryGetProperty("val", out var cents)
            && cents.ValueKind == JsonValueKind.Number && cents.TryGetInt64(out _))
        {
            period = "월간"; reset = Timestamp(Text(config, "billingPeriodEnd")); beginning = Timestamp(Text(config, "billingPeriodStart"));
        }
        TimeSpan? duration = beginning is { } from && reset is { } to && to > from ? to - from : null;
        var name = (unified ? "공유 " : "Build ") + period;
        var message = unified ? "공식 Grok Build가 제공한 X 구독 공유 한도입니다."
            : "공식 Grok Build가 제공한 Build 전용 한도입니다. Grok 웹 전체 한도와 구분합니다.";
        message += " Grok Bot 한도·API 비용·추가 사용 금액은 포함하지 않습니다.";
        if (reset is null) message += " 초기화 시각이 제공되지 않았습니다.";
        return new(UsageProviderKind.Grok, UsageStatus.Ready, [new UsageWindow(name, used, reset, duration)],
            DateTimeOffset.UtcNow, unified ? "X 구독 공유 · Grok Build" : "Grok Build", message);
    }

    private static UsageSnapshot ParseError(JsonElement error)
    {
        // SDK details and OAuth URLs never leave this transient classifier.
        var text = ((Text(error, "message") ?? "") + " " + (Text(error, "data") ?? "")).ToLowerInvariant();
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var errorCode)
            && errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var numericCode) ? numericCode : 0;
        if (text.Contains("429", StringComparison.Ordinal) || text.Contains("rate limit", StringComparison.Ordinal))
            return Failure(UsageStatus.RateLimited, "Grok Build 조회 요청이 제한되었습니다. 잠시 후 자동으로 다시 확인합니다.");
        if (code == -32000 || text.Contains("authentication", StringComparison.Ordinal) || text.Contains("auth required", StringComparison.Ordinal)
            || text.Contains("unauthorized", StringComparison.Ordinal) || text.Contains("401", StringComparison.Ordinal)
            || text.Contains("not logged", StringComparison.Ordinal) || text.Contains("not authenticated", StringComparison.Ordinal)
            || text.Contains("authrequired", StringComparison.Ordinal) || text.Contains("auth_required", StringComparison.Ordinal))
            return Failure(UsageStatus.AuthenticationRequired, "Grok Build 로그인이 필요합니다. 설정의 Grok 로그인 또는 공식 grok login --oauth를 완료해 주세요.");
        if (text.Contains("403", StringComparison.Ordinal) || text.Contains("forbidden", StringComparison.Ordinal))
            return Failure(UsageStatus.Unavailable, "Grok Build 계정의 구독 권한과 CLI 사용 권한을 확인해 주세요.");
        return Failure(UsageStatus.Unavailable, "Grok Build 사용량을 조회할 수 없습니다. 공식 CLI 상태를 확인해 주세요.");
    }

    internal static UsageSnapshot Failure(UsageStatus status, string message) =>
        new(UsageProviderKind.Grok, status, [], DateTimeOffset.UtcNow, "Grok Build", message);
    private static DateTimeOffset? Timestamp(string? text) => text is not null && text.Contains('T')
        && (text.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(text, "[+-]\\d{2}:\\d{2}$"))
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
    private static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Object(JsonElement root, string key, out JsonElement value)
    { value = default; return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object; }

    private sealed class BoundedLines(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position, _length, _total;
        internal async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false); _position = 0;
                    if (_length == 0) return line.Length == 0 ? null : line.ToString();
                }
                var character = _buffer[_position++];
                if (++_total > 1024 * 1024 || line.Length > 65536) throw new IOException("CLI response exceeds limit.");
                if (character == '\n') return line.ToString();
                if (character != '\r') line.Append(character);
            }
        }
    }
}

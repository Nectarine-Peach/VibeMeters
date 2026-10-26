using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

/// <summary>Runs the official read-only /usage command; never reads or copies keyring credentials.</summary>
internal static class AntigravityUsageBridge
{
    internal const string Source = "Antigravity";
    private static readonly Version MinimumVersion = new(1, 4, 7);

    internal static LoginResult StartLogin()
    {
        var executable = OfficialCliProcess.FindExecutable("agy");
        if (executable is null) return new(false, "개인 계정은 Antigravity CLI 설치 및 로그인이 필요합니다. 설정 안내를 확인해 주세요.");
        try
        {
            // Only the user's explicit Login action opens an interactive official client.
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
            return new(true, "Antigravity 창에서 로그인 또는 계정 전환을 완료한 뒤 새로고침해 주세요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(false, "Antigravity를 열 수 없습니다. 공식 CLI에서 로그인한 뒤 새로고침해 주세요."); }
    }

    internal static async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var versionStart = OfficialCliProcess.CreateStartInfo("agy", "--version");
            if (versionStart is null) return Failure(UsageStatus.Unavailable,
                "Gemini CLI 개인 계정 지원이 종료되었습니다. Antigravity CLI를 설치하고 로그인해 주세요.");
            var version = await RunAsync(versionStart, deadline.Token).ConfigureAwait(false);
            // Old clients can send /usage to a model as a prompt. Never invoke print mode on them.
            if (version.ExitCode != 0 || !IsSupportedVersion(version.Output))
                return Failure(UsageStatus.Unavailable, "Antigravity CLI 1.4.7 이상이 필요합니다. 공식 CLI를 업데이트해 주세요.");
            var start = OfficialCliProcess.CreateStartInfo("agy", "-p", "/usage", "--output-format", "json", "--print-timeout", "30s")!;
            // No controlling terminal: official headless authentication fails fast instead of opening login.
            var result = await RunAsync(start, deadline.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) return FailureFromDiagnostic(result.Output + "\n" + result.Error);
            using var json = JsonDocument.Parse(result.Output);
            return Parse(json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(UsageStatus.Unavailable, "Antigravity 사용량 조회 시간이 초과되었습니다. 잠시 후 새로고침해 주세요."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { return Failure(UsageStatus.Unavailable, "Antigravity 사용량을 가져올 수 없습니다. 공식 CLI 상태를 확인해 주세요."); }
    }

    private static bool IsSupportedVersion(string text) =>
        Version.TryParse(text.Trim().TrimStart('v'), out var version) && version >= MinimumVersion;

    private static UsageSnapshot Parse(JsonElement root)
    {
        if (Text(root, "status") != "SUCCESS")
            return FailureFromDiagnostic(root.ValueKind == JsonValueKind.Object ? root.GetRawText() : "");
        // The command envelope proves this was /usage, not an agent-generated answer.
        if (!Object(root, "command", out var command)
            || Text(command, "name") is not ("usage" or "quota") || !Object(command, "data", out var data)
            || !root.TryGetProperty("num_turns", out var turns) || turns.ValueKind != JsonValueKind.Number || !turns.TryGetInt32(out var count) || count != 0
            || !Object(root, "usage", out var usage) || !usage.TryGetProperty("total_tokens", out var tokens)
            || tokens.ValueKind != JsonValueKind.Number || !tokens.TryGetInt64(out var total) || total != 0
            || !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return Failure(UsageStatus.Unavailable, "Antigravity가 올바른 사용량 명령 응답을 제공하지 않았습니다.");

        var windows = new List<UsageWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = Text(group, "name");
            if (groupName is null || !groupName.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase)
                || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                var id = Text(bucket, "id");
                var window = Text(bucket, "window");
                if (id is null || !id.StartsWith("gemini-", StringComparison.Ordinal) || !seen.Add(id)
                    || window is not ("5h" or "weekly") || !bucket.TryGetProperty("remaining_fraction", out var value)
                    || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var remaining)
                    || !double.IsFinite(remaining) || remaining < 0 || remaining > 1) continue;
                var period = window == "5h" ? "5시간" : "주간";
                var label = groupName.Equals("Gemini Models", StringComparison.OrdinalIgnoreCase) ? period
                    : groupName[7..].Trim() + " " + period;
                DateTimeOffset? resetsAt = DateTimeOffset.TryParse(Text(bucket, "reset_time"), out var reset) ? reset : null;
                windows.Add(new(label, 100 - remaining * 100, resetsAt,
                    window == "5h" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7)));
            }
        }
        return windows.Count == 0 ? Failure(UsageStatus.Unavailable, "Antigravity 계정에서 Gemini 한도를 제공하지 않습니다.")
            : new(UsageProviderKind.Gemini, UsageStatus.Ready,
                windows.OrderBy(w => w.Name == "5시간" ? 0 : w.Name == "주간" ? 1 : 2).ToArray(),
                DateTimeOffset.UtcNow, Source, "Antigravity의 Gemini 공유 한도입니다. Gemini 웹·앱 사용량과는 별개입니다.");
    }

    private static UsageSnapshot FailureFromDiagnostic(string diagnostic)
    {
        // Raw output is transient and never reaches UI, logs, or disk.
        var text = diagnostic.ToLowerInvariant();
        if (text.Contains("429", StringComparison.Ordinal) || text.Contains("rate limit", StringComparison.Ordinal))
            return Failure(UsageStatus.RateLimited, "Antigravity 조회 요청이 제한되었습니다. 잠시 후 자동으로 재시도합니다.");
        if (text.Contains("authentication required", StringComparison.Ordinal) || text.Contains("not logged in", StringComparison.Ordinal)
            || text.Contains("sign in", StringComparison.Ordinal) || text.Contains("oauth", StringComparison.Ordinal)
            || text.Contains("401", StringComparison.Ordinal))
            return Failure(UsageStatus.AuthenticationRequired, "Antigravity 로그인이 필요합니다. 공식 CLI에서 로그인한 뒤 새로고침해 주세요.");
        return Failure(UsageStatus.Unavailable, "Antigravity 사용량을 조회할 수 없습니다. 공식 CLI의 /usage를 확인해 주세요.");
    }

    private static UsageSnapshot Failure(UsageStatus status, string message) =>
        new(UsageProviderKind.Gemini, status, [], DateTimeOffset.UtcNow, Source, message);

    private static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Object(JsonElement root, string key, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("CLI did not start.");
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally { OfficialCliProcess.Stop(process); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 1024 * 1024) throw new IOException("CLI response exceeds limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

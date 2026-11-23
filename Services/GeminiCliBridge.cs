using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal sealed class GeminiCliBridge(GeminiProfile profile)
{
    private volatile bool _usesAntigravity;
    internal void ResetRoute() => _usesAntigravity = false;
    private bool UseAntigravity => profile == GeminiProfile.Antigravity || _usesAntigravity;

    public async Task<LoginResult> LoginAsync(CancellationToken cancellationToken)
    {
        if (UseAntigravity) return AntigravityUsageBridge.StartLogin();
        var (runtime, script) = ResolveRuntime();
        var node = FindNode(runtime);
        if (node is null || !File.Exists(script))
            return new(false, "Gemini 로그인 런타임을 찾을 수 없습니다. 앱을 다시 설치해 주세요.");
        try
        {
            var start = CreateStartInfo(node, script, runtime);
            start.ArgumentList.Add("--login");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new(false, "Gemini 로그인을 시작할 수 없습니다.");
            try
            {
                process.StandardInput.Close();
                var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                using var json = JsonDocument.Parse(await output.ConfigureAwait(false));
                return process.ExitCode == 0 && Text(json.RootElement, "status") == "Authenticated"
                    ? new(true, "로그인했습니다. 사용량을 다시 확인합니다.")
                    : new(false, "Gemini 로그인을 완료하지 못했습니다.");
            }
            finally { OfficialCliProcess.Stop(process); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "로그인 대기 시간이 지났습니다. 다시 로그인해 주세요."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(false, "Gemini 로그인을 시작할 수 없습니다. 잠시 후 다시 시도해 주세요."); }
    }

    public async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (UseAntigravity) return await AntigravityUsageBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
        var (runtime, script) = ResolveRuntime();
        var node = FindNode(runtime);
        if (node is null || !File.Exists(script))
            return Error(UsageStatus.Unavailable, "Gemini 조회 런타임을 찾을 수 없습니다. 앱을 다시 설치해 주세요.");
        var start = CreateStartInfo(node, script, runtime);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Error(UsageStatus.Unavailable, "Gemini 조회를 시작할 수 없습니다.");
            process.StandardInput.Close();
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return Error(UsageStatus.Unavailable, "Gemini 조회가 완료되지 않았습니다. 잠시 후 다시 확인합니다.");
            using var json = JsonDocument.Parse(output);
            if (profile == GeminiProfile.Default && Text(json.RootElement, "reason") == "UNSUPPORTED_CLIENT")
            {
                _usesAntigravity = true;
                return await AntigravityUsageBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            return Parse(json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Error(UsageStatus.Unavailable, "Gemini 연결 시간이 초과되었습니다. 잠시 후 다시 확인합니다."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { return Error(UsageStatus.Unavailable, "Gemini 사용량을 가져올 수 없습니다. 잠시 후 다시 확인합니다."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static (string Runtime, string Script) ResolveRuntime()
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "gemini-quota");
        var script = Path.Combine(runtime, "quota.mjs");
        if (!File.Exists(script) && FindSourceRuntime() is { } source)
        { runtime = source; script = Path.Combine(source, "quota.mjs"); }
        return (runtime, script);
    }

    internal ProcessStartInfo CreateStartInfo(string node, string script, string runtime)
    {
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = runtime
        };
        start.ArgumentList.Add(script);
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        start.Environment.Remove("CODE_ASSIST_ENDPOINT");
        start.Environment["NO_COLOR"] = "1";
        if (profile is GeminiProfile.CodeAssist1 or GeminiProfile.CodeAssist2)
        {
            start.Environment["GEMINI_CLI_HOME"] = HomeDirectory;
            start.Environment["GEMINI_FORCE_ENCRYPTED_FILE_STORAGE"] = "true";
            start.Environment["GEMINI_FORCE_FILE_STORAGE"] = "true";
            start.Environment.Remove("GOOGLE_APPLICATION_CREDENTIALS");
            start.Environment.Remove("GOOGLE_CLOUD_ACCESS_TOKEN");
        }
        return start;
    }

    internal string HomeDirectory => profile is GeminiProfile.CodeAssist1 or GeminiProfile.CodeAssist2
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeMeters", "Gemini", profile.ToString())
        : Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static UsageSnapshot Parse(JsonElement root)
    {
        if (Text(root, "status") != "Ready")
            return Text(root, "reason") switch
            {
                "AUTH_REQUIRED" => Error(UsageStatus.AuthenticationRequired, "Gemini 로그인이 필요합니다. 로그인 후 자동으로 갱신됩니다."),
                "RATE_LIMITED" => Error(UsageStatus.RateLimited, "Gemini 요청 제한 · 잠시 후 자동으로 다시 확인합니다.")
                    with { RetryAfter = ReadRetryAfter(root) },
                "PROJECT_REQUIRED" => Error(UsageStatus.Unavailable, "Gemini 계정에 Google Cloud 프로젝트 설정이 필요합니다."),
                "ACCESS_DENIED" => Error(UsageStatus.Unavailable, "조회 권한이 없습니다. 회사 계정의 서비스 권한·SSO 및 네트워크 정책을 확인해 주세요."),
                "UNSUPPORTED_CLIENT" => Error(UsageStatus.Unavailable, "Gemini CLI 개인 계정 지원이 종료되었습니다. Antigravity에서 사용량을 확인해 주세요."),
                "RUNTIME_REQUIRED" => Error(UsageStatus.Unavailable, "Gemini 조회 런타임이 누락되었습니다. 앱을 다시 설치해 주세요."),
                "NO_QUOTA" => Error(UsageStatus.Unavailable, "Gemini 계정에서 남은 한도 정보를 제공하지 않습니다."),
                _ => Error(UsageStatus.Unavailable, "Gemini 서버에 연결할 수 없습니다. 잠시 후 다시 확인합니다.")
            };
        var windows = new List<UsageWindow>();
        if (root.TryGetProperty("windows", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                var name = Text(entry, "name");
                if (string.IsNullOrWhiteSpace(name) || !entry.TryGetProperty("remainingPercent", out var value)
                    || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var remaining)
                    || !double.IsFinite(remaining) || remaining < 0 || remaining > 100) continue;
                DateTimeOffset? reset = DateTimeOffset.TryParse(Text(entry, "resetsAt"), out var date) ? date : null;
                windows.Add(new UsageWindow(name, 100 - remaining, reset));
            }
        return windows.Count == 0 ? Error(UsageStatus.Unavailable, "Gemini 계정에서 남은 한도 정보를 제공하지 않습니다.")
            : new UsageSnapshot(UsageProviderKind.Gemini, UsageStatus.Ready, windows, DateTimeOffset.UtcNow, Text(root, "plan"));
    }

    private static UsageSnapshot Error(UsageStatus status, string message) =>
        new(UsageProviderKind.Gemini, status, [], DateTimeOffset.UtcNow, Message: message);

    private static DateTimeOffset? ReadRetryAfter(JsonElement root)
    {
        if (!root.TryGetProperty("retryAfterUnixMs", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var milliseconds) || milliseconds < 0
            || milliseconds > DateTimeOffset.MaxValue.AddDays(-1).ToUnixTimeMilliseconds()) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 1024 * 1024) throw new IOException("Bridge response is too large.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string? FindNode(string runtime)
    {
        var candidates = new List<string>
        {
            Path.Combine(runtime, "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")
        };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory)) candidates.Add(Path.Combine(directory, "node.exe"));
        }
        foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string? FindSourceRuntime()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "runtime", "gemini-quota");
            if (File.Exists(Path.Combine(candidate, "quota.mjs"))) return candidate;
        }
        return null;
    }
}

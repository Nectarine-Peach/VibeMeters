using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal static class GeminiCliBridge
{
    private static volatile bool _usesAntigravity;
    internal static void ResetRoute() => _usesAntigravity = false;

    public static LoginResult StartLogin()
    {
        if (_usesAntigravity) return AntigravityUsageBridge.StartLogin();
        var (runtime, script) = ResolveRuntime();
        var node = FindNode(runtime);
        if (node is null || !File.Exists(script))
            return new(false, "Gemini 로그인 런타임을 찾을 수 없습니다. 앱을 다시 설치해 주세요.");
        try
        {
            var start = CreateStartInfo(node, script, runtime);
            start.ArgumentList.Add("--login");
            // The adapter suppresses all SDK logs, and only a small sanitized JSON result is emitted.
            // No pipes remain open in this detached user-initiated browser login flow.
            start.RedirectStandardInput = false;
            start.RedirectStandardOutput = false;
            start.RedirectStandardError = false;
            using var process = Process.Start(start);
            return process is null ? new(false, "Gemini 로그인을 시작할 수 없습니다.")
                : new(true, "브라우저에서 Gemini 로그인을 완료하면 사용량이 자동으로 갱신됩니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(false, "Gemini 로그인을 시작할 수 없습니다. 잠시 후 다시 시도해 주세요."); }
    }

    public static async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (_usesAntigravity) return await AntigravityUsageBridge.ReadAsync(cancellationToken).ConfigureAwait(false);
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
            if (Text(json.RootElement, "reason") == "UNSUPPORTED_CLIENT")
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

    private static ProcessStartInfo CreateStartInfo(string node, string script, string runtime)
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
        return start;
    }

    private static UsageSnapshot Parse(JsonElement root)
    {
        if (Text(root, "status") != "Ready")
            return Text(root, "reason") switch
            {
                "AUTH_REQUIRED" => Error(UsageStatus.AuthenticationRequired, "Gemini 로그인이 필요합니다. 로그인 후 자동으로 갱신됩니다."),
                "RATE_LIMITED" => Error(UsageStatus.RateLimited, "Gemini 요청 제한 · 잠시 후 자동으로 다시 확인합니다."),
                "PROJECT_REQUIRED" => Error(UsageStatus.Unavailable, "Gemini 계정에 Google Cloud 프로젝트 설정이 필요합니다."),
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

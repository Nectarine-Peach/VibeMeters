using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

internal sealed record CliInstallResult(bool Success, string Message);
internal sealed record CliCommandResult(int ExitCode, string Output);
internal sealed record CliInstallerRuntime(
    Func<UsageProviderKind, string?> Find,
    Func<ProcessStartInfo, CancellationToken, Task<CliCommandResult>> Run);
internal sealed record CliInstallation(string? Executable, string? Version)
{
    internal string Description => Executable is null ? "설치되지 않음" : Version is null ? "설치됨 · 버전 확인 실패" : "설치됨 · " + Version;
}

internal interface ICliMaintenance
{
    Task<CliInstallation> InspectAsync(UsageProviderKind kind, CancellationToken token);
    Task<CliInstallResult> InstallAsync(UsageProviderKind kind, bool update, CancellationToken token, IProgress<string> progress);
}

internal sealed class CliMaintenance : ICliMaintenance
{
    public Task<CliInstallation> InspectAsync(UsageProviderKind kind, CancellationToken token) => OfficialCliInstaller.InspectAsync(kind, token);
    public Task<CliInstallResult> InstallAsync(UsageProviderKind kind, bool update, CancellationToken token, IProgress<string> progress) =>
        OfficialCliInstaller.InstallAsync(kind, token, progress, update);
}

/// <summary>Explicit, per-user CLI maintenance. Never logs installer output or starts login.</summary>
internal static class OfficialCliInstaller
{
    private static readonly ConcurrentDictionary<UsageProviderKind, SemaphoreSlim> Gates = new();
    private static readonly CliInstallerRuntime Runtime = new(Find, RunAsync);
    internal static string Name(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => "Codex CLI", UsageProviderKind.Claude => "Claude Code",
        UsageProviderKind.Gemini => "Antigravity CLI", _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    internal static string Command(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => "codex", UsageProviderKind.Claude => "claude",
        UsageProviderKind.Gemini => "agy", _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    internal static string? Find(UsageProviderKind kind) => OfficialCliProcess.FindExecutable(Command(kind));
    internal static string Source(UsageProviderKind kind) => kind switch
    {
        UsageProviderKind.Codex => "https://chatgpt.com/codex/install.ps1",
        UsageProviderKind.Claude => "https://claude.ai/install.ps1",
        UsageProviderKind.Gemini => "https://antigravity.google/cli/install.cmd",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static async Task<CliInstallation> InspectAsync(UsageProviderKind kind, CancellationToken token, CliInstallerRuntime? runtime = null)
    {
        runtime ??= Runtime;
        var path = runtime.Find(kind);
        return path is null ? new(null, null) : new(path, await ReadVersionAsync(path, token, runtime).ConfigureAwait(false));
    }

    internal static string? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"^\s*(?:(?:codex-cli|codex|agy|antigravity(?: cli)?)\s+)?v?(?<version>\d{1,4}\.\d{1,4}\.\d{1,6}(?:[-+][a-zA-Z0-9.\-]{1,64})?)(?:\s+\((?:claude code|antigravity(?: cli)?)\))?\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static async Task<string?> ReadVersionAsync(string executable, CancellationToken token, CliInstallerRuntime runtime)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var start = OfficialCliProcess.CreateStartInfoForExecutable(executable, "--version");
            start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            start.Environment["DISABLE_AUTOUPDATER"] = "1";
            var result = await runtime.Run(start, deadline.Token).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParseVersion(result.Output) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException
            or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { return null; }
    }

    internal static ProcessStartInfo CreateStartInfo(UsageProviderKind kind, string? existing, string? npm = null, string? winget = null)
    {
        if (existing is not null)
        {
            string normalized = existing.Replace('/', '\\');
            if (normalized.Contains("\\Microsoft\\WinGet\\", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
            {
                winget ??= OfficialCliProcess.FindExecutable("winget");
                if (winget is null) throw new InvalidOperationException("WinGet을 찾지 못했습니다. 앱 설치 관리자에서 WinGet을 복구해 주세요.");
                string id = kind switch { UsageProviderKind.Codex => "OpenAI.Codex", UsageProviderKind.Claude => "Anthropic.ClaudeCode", _ => "Google.AntigravityCLI" };
                return OfficialCliProcess.CreateStartInfoForExecutable(winget, "upgrade", "--id", id, "--exact", "--silent",
                    "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity");
            }
            if (Path.GetExtension(existing).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                string package = kind switch { UsageProviderKind.Codex => "@openai/codex", UsageProviderKind.Claude => "@anthropic-ai/claude-code", _ => "" };
                var prefix = Path.GetDirectoryName(existing)!;
                if (package.Length == 0 || !File.Exists(Path.Combine(prefix, "node_modules", package.Replace('/', Path.DirectorySeparatorChar), "package.json")))
                    throw new InvalidOperationException("기존 CLI 설치 방식을 확인하지 못했습니다. 해당 CLI의 공식 업데이트 방법을 사용해 주세요.");
                npm ??= OfficialCliProcess.FindExecutable("npm");
                if (npm is null) throw new InvalidOperationException("npm을 찾지 못했습니다. 기존 Node.js 설치를 확인해 주세요.");
                return OfficialCliProcess.CreateStartInfoForExecutable(npm, "install", "--global", "--prefix", prefix, package + "@latest", "--no-fund", "--no-audit");
            }
            if (kind is UsageProviderKind.Claude or UsageProviderKind.Gemini)
                return OfficialCliProcess.CreateStartInfoForExecutable(existing, "update");
        }

        var script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; $global:LASTEXITCODE=0; try { ";
        if (kind == UsageProviderKind.Gemini)
        {
            // The official CMD installer owns its archive checks and user PATH update.
            script += "$file=Join-Path ([IO.Path]::GetTempPath()) ('vibemeters-agy-'+[Guid]::NewGuid().ToString('N')+'.cmd'); " +
                "try { Invoke-WebRequest -UseBasicParsing -Uri '" + Source(kind) + "' -OutFile $file; & $file; $code=$LASTEXITCODE } " +
                "finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }; if ($code -ne 0) {exit $code}; ";
        }
        else script += "$payload=(Invoke-WebRequest -UseBasicParsing -Uri '" + Source(kind) + "').Content; " +
            "if ($payload -is [byte[]]) {$payload=[Text.Encoding]::UTF8.GetString($payload)}; & ([scriptblock]::Create([string]$payload)); ";
        script += "if ($LASTEXITCODE -ne 0) {exit $LASTEXITCODE}; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment.Remove("NODE_OPTIONS"); start.Environment.Remove("NODE_PATH"); start.Environment["NO_COLOR"] = "1";
        if (kind == UsageProviderKind.Codex)
        {
            start.Environment["CODEX_NON_INTERACTIVE"] = "1";
            start.Environment["CODEX_INSTALL_DIR"] = existing is not null ? Path.GetDirectoryName(existing)! :
                Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin");
        }
        return start;
    }

    internal static async Task<CliInstallResult> InstallAsync(UsageProviderKind kind, CancellationToken token, IProgress<string> progress, bool update = false, CliInstallerRuntime? runtime = null)
    {
        runtime ??= Runtime;
        var gate = Gates.GetOrAdd(kind, _ => new SemaphoreSlim(1, 1));
        bool acquired = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            acquired = await gate.WaitAsync(0, token).ConfigureAwait(false);
            if (!acquired) return new(false, "이 CLI의 설치 또는 업데이트가 이미 진행 중입니다.");
            var existing = runtime.Find(kind);
            if (!update && existing is not null)
            {
                var current = await InspectAsync(kind, deadline.Token, runtime).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return current.Version is not null ? new(true, "이미 설치된 CLI를 사용합니다.")
                    : new(false, "작업이 종료됐지만 CLI 실행과 버전을 확인하지 못했습니다. 설치 정책과 PATH를 확인한 뒤 다시 시도해 주세요.");
            }
            progress.Report(existing is null ? "공식 CLI를 설치하고 있습니다." : "기존 CLI를 업데이트하고 있습니다.");
            var start = CreateStartInfo(kind, existing);
            var result = await runtime.Run(start, deadline.Token).ConfigureAwait(false);
            // WinGet reports no applicable upgrade as a distinct success-equivalent code.
            bool noUpgrade = result.ExitCode == unchecked((int)0x8A15002B) && start.ArgumentList.Contains("upgrade");
            if (result.ExitCode != 0 && !noUpgrade) return new(false, Failure(result.Output));
            var installed = await InspectAsync(kind, deadline.Token, runtime).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (installed.Executable is null || installed.Version is null)
                return new(false, "작업이 종료됐지만 CLI 실행과 버전을 확인하지 못했습니다. 설치 정책과 PATH를 확인한 뒤 다시 시도해 주세요.");
            if (existing is not null && !string.Equals(existing, installed.Executable, StringComparison.OrdinalIgnoreCase))
                return new(false, "업데이트 후 실행 경로가 달라졌습니다. 중복 CLI 설치와 PATH를 확인해 주세요.");
            return new(true, (noUpgrade ? "적용할 업데이트 없음 · " : update ? "업데이트 명령 완료 · " : "설치 완료 · ") + installed.Version);
        }
        catch (OperationCanceledException)
        { return new(false, token.IsCancellationRequested ? "작업을 취소했습니다. 일부 설치 파일이 남을 수 있습니다." : "작업 대기 시간(10분)이 지났습니다. 네트워크와 설치 정책을 확인해 주세요."); }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(false, "CLI 작업을 실행하지 못했습니다. 설치 권한과 Windows 또는 회사 정책을 확인해 주세요."); }
        finally { if (acquired) gate.Release(); }
    }

    private static string Failure(string output)
    {
        var text = output.ToLowerInvariant();
        if (text.Contains("older standalone") || text.Contains("replace the existing"))
            return "기존 CLI 설치 형식을 자동으로 바꾸지 않았습니다. 해당 CLI의 공식 수동 업데이트 안내를 확인해 주세요.";
        if (text.Contains("certificate") || text.Contains("trust relationship") || text.Contains("ssl/tls"))
            return "공식 설치 서버의 인증서를 확인하지 못했습니다. 회사 CA·프록시 설정을 확인하세요. TLS 검증은 해제하지 않습니다.";
        if (text.Contains("access is denied") || text.Contains("unauthorized") || text.Contains("execution policy") || text.Contains("cannot be loaded"))
            return "현재 사용자 설치가 Windows 또는 회사 정책에 의해 차단됐습니다. 관리자에게 공식 CLI 설치를 요청하세요.";
        return "CLI 작업이 실패했습니다. 네트워크와 기존 CLI 설치 방식을 확인한 뒤 다시 시도해 주세요.";
    }

    private static async Task<CliCommandResult> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("CLI 작업을 시작하지 못했습니다.");
            process.StandardInput.Close();
            var stdout = DrainAsync(process.StandardOutput, token);
            var stderr = DrainAsync(process.StandardError, token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        }
        finally { OfficialCliProcess.Stop(process); }
    }

    private static async Task<string> DrainAsync(StreamReader stream, CancellationToken token)
    {
        var retained = new StringBuilder(); var buffer = new char[1024]; int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (retained.Length < 16384) retained.Append(buffer, 0, Math.Min(read, 16384 - retained.Length));
        return retained.ToString();
    }
}

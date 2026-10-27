using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeters.Services;

public enum ClaudeRefreshOutcome { Completed, CliMissing, CliOutdated, Failed }

public interface IClaudeCredentialRefresher
{
    Task<ClaudeRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lets the official CLI own refresh-token rotation, locking and credential storage.</summary>
public sealed class ClaudeCredentialRefresher(string configDirectory) : IClaudeCredentialRefresher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Version MinimumVersion = new(2, 1, 301);

    public async Task<ClaudeRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Gate.Wait(0)) return ClaudeRefreshOutcome.Failed;
        // This task retains the gate until the CLI exits, even when the UI stops waiting.
        // Never kill a command while it may be saving a rotated refresh token.
        var pending = RefreshAndReleaseAsync();
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return ClaudeRefreshOutcome.Failed; }
    }

    private async Task<ClaudeRefreshOutcome> RefreshAndReleaseAsync()
    {
        try
        {
            var version = await RunAsync(["--version"], captureVersion: true).ConfigureAwait(false);
            if (version is null) return ClaudeRefreshOutcome.CliMissing;
            if (version.Value.ExitCode != 0 || !IsSupportedVersion(version.Value.Output)) return ClaudeRefreshOutcome.CliOutdated;
            // 2.1.301 fixed short commands exiting during OAuth refresh. There is no
            // separate public refresh command; status initializes the CLI's own auth.
            var status = await RunAsync(["auth", "status"], captureVersion: false).ConfigureAwait(false);
            return status?.ExitCode == 0 ? ClaudeRefreshOutcome.Completed : ClaudeRefreshOutcome.Failed;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException or ArgumentException)
        { return ClaudeRefreshOutcome.Failed; }
        finally { Gate.Release(); }
    }

    public static bool IsSupportedVersion(string output)
    {
        var match = Regex.Match(output.Trim(), @"^(\d+\.\d+\.\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) && version >= MinimumVersion;
    }

    private async Task<(int ExitCode, string Output)?> RunAsync(string[] arguments, bool captureVersion)
    {
        var start = CreateStartInfo(configDirectory, arguments);
        if (start is null) return null;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return null;
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, captureVersion);
        var error = DrainAsync(process.StandardError, capture: false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }

    internal static ProcessStartInfo? CreateStartInfo(string directory, params string[] arguments)
    {
        var start = OfficialCliProcess.CreateStartInfo("claude", arguments);
        if (start is null) return null;
        ConfigureEnvironment(start, directory);
        return start;
    }

    internal static void ConfigureEnvironment(ProcessStartInfo start, string directory)
    {
        start.Environment["CLAUDE_CONFIG_DIR"] = directory;
        // Always refresh the same file-backed subscription used by CredentialReader.
        // Environment credentials/gateways must not redirect this status check.
        foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
            "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            start.Environment.Remove(key);
    }

    private static async Task<string> DrainAsync(StreamReader stream, bool capture)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (capture && result.Length < 512) result.Append(buffer, 0, Math.Min(count, 512 - result.Length));
        // auth status output may contain account details: drain, never retain or log it.
        return result.ToString();
    }
}

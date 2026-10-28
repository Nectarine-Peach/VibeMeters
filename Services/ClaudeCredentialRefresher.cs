using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeters.Services;

public enum ClaudeRefreshOutcome { Completed, CliMissing, CliOutdated, Failed, Pending, TimedOut, AuthenticationRejected }

public sealed class ClaudeCliResult(int exitCode, string output = "", string error = "")
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
    public string Error { get; } = error;
    public override string ToString() => "[Claude CLI result]";
}

public interface IClaudeCommandRunner
{
    Task<ClaudeCliResult?> RunAsync(string[] arguments);
}

public interface IClaudeCredentialRefresher
{
    Task<ClaudeRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lets the official CLI own refresh-token rotation, locking and credential storage.</summary>
public sealed class ClaudeCredentialRefresher : IClaudeCredentialRefresher
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Task<ClaudeRefreshOutcome>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Version MinimumVersion = new(2, 1, 301);
    private readonly string _configDirectory;
    private readonly IClaudeCommandRunner _runner;
    private readonly IClaudeRecoveryClock _clock;
    private readonly IClaudeAuthDiagnostics? _diagnostics;

    public ClaudeCredentialRefresher(string configDirectory, IClaudeCommandRunner? runner = null,
        IClaudeRecoveryClock? clock = null, IClaudeAuthDiagnostics? diagnostics = null)
    {
        _configDirectory = Path.GetFullPath(configDirectory);
        _runner = runner ?? new OfficialClaudeCommandRunner(_configDirectory);
        _clock = clock ?? ClaudeRecoveryClock.Instance;
        _diagnostics = diagnostics ?? (runner is null ? new ClaudeAuthDiagnostics() : null);
    }

    public async Task<ClaudeRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ClaudeRefreshOutcome> pending;
        bool shared;
        lock (Sync)
        {
            shared = Pending.TryGetValue(_configDirectory, out pending!) && !pending.IsCompleted;
            if (!shared) Pending[_configDirectory] = pending = RefreshCoreAsync();
        }
        if (shared) _diagnostics?.Write(ClaudeAuthEvent.CliShared, ClaudeRefreshOutcome.Pending);
        // Cancellation/timeout stops waiting, never a CLI which may be saving rotated credentials.
        try { return await _clock.WaitAsync(pending, TimeSpan.FromSeconds(shared ? 2 : 12), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var outcome = shared ? ClaudeRefreshOutcome.Pending : ClaudeRefreshOutcome.TimedOut;
            _diagnostics?.Write(ClaudeAuthEvent.CliTimedOut, outcome);
            return outcome;
        }
    }

    private async Task<ClaudeRefreshOutcome> RefreshCoreAsync()
    {
        try
        {
            var version = await _runner.RunAsync(["--version"]).ConfigureAwait(false);
            if (version is null) return ClaudeRefreshOutcome.CliMissing;
            if (version.ExitCode != 0 || !IsSupportedVersion(version.Output)) return ClaudeRefreshOutcome.CliOutdated;
            // 2.1.301 fixed short commands exiting during OAuth refresh. There is no
            // separate public refresh command; status initializes the CLI's own auth.
            var status = await _runner.RunAsync(["auth", "status"]).ConfigureAwait(false);
            var outcome = status is null ? ClaudeRefreshOutcome.CliMissing
                : IsExplicitRejection(status.Error) ? ClaudeRefreshOutcome.AuthenticationRejected
                : status.ExitCode == 0 ? ClaudeRefreshOutcome.Completed : ClaudeRefreshOutcome.Failed;
            _diagnostics?.Write(ClaudeAuthEvent.CliCompleted, outcome, status?.ExitCode);
            return outcome;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException or ArgumentException)
        { return ClaudeRefreshOutcome.Failed; }
    }

    public static bool IsSupportedVersion(string output)
    {
        var match = Regex.Match(output.Trim(), @"^(\d+\.\d+\.\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) && version >= MinimumVersion;
    }

    public static bool IsExplicitRejection(string error)
    {
        // Only an entire machine-readable OAuth error is decisive. An exit code,
        // expired access-token message or arbitrary text containing invalid_grant is not.
        try
        {
            using var json = JsonDocument.Parse(error);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var value)
                && value.ValueKind == JsonValueKind.String && value.GetString() == "invalid_grant";
        }
        catch (JsonException) { return false; }
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

    private sealed class OfficialClaudeCommandRunner(string directory) : IClaudeCommandRunner
    {
        public async Task<ClaudeCliResult?> RunAsync(string[] arguments)
        {
            var start = CreateStartInfo(directory, arguments);
            if (start is null) return null;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return null;
            process.StandardInput.Close();
            var output = DrainAsync(process.StandardOutput);
            var error = DrainAsync(process.StandardError);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }

    private static async Task<string> DrainAsync(StreamReader stream)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        // Bounded transient data only: results are never logged, serialized or shown.
        return result.ToString();
    }
}

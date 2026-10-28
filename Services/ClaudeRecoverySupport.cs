using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeters.Services;

public interface IClaudeRecoveryClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    Task<T> WaitAsync<T>(Task<T> pending, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ClaudeRecoveryClock : IClaudeRecoveryClock
{
    public static readonly ClaudeRecoveryClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    public Task<T> WaitAsync<T>(Task<T> pending, TimeSpan timeout, CancellationToken cancellationToken) => pending.WaitAsync(timeout, cancellationToken);
}

public enum ClaudeAuthEvent { RecoveryStarted, CliCompleted, CliTimedOut, CliShared, FileObserved, RecoveryDeferred, RecoverySucceeded, PreflightReady, PreflightLimited, BrowserLoginRequested }

public interface IClaudeAuthDiagnostics
{
    void Write(ClaudeAuthEvent name, ClaudeRefreshOutcome? outcome = null, int? exitCode = null, bool? credentialChanged = null, bool? futureExpiry = null);
}

/// <summary>Only closed enums, numbers and booleans enter this bounded local diagnostic log.</summary>
public sealed class ClaudeAuthDiagnostics(string? directory = null) : IClaudeAuthDiagnostics
{
    private static readonly object Sync = new();
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeMeters");
    public void Write(ClaudeAuthEvent name, ClaudeRefreshOutcome? outcome = null, int? exitCode = null, bool? credentialChanged = null, bool? futureExpiry = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "claude-auth-diagnostics.jsonl");
                var archive = path + ".1";
                var cutoff = DateTime.UtcNow.AddDays(-7);
                if (File.Exists(archive) && File.GetLastWriteTimeUtc(archive) < cutoff) File.Delete(archive);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                if (File.Exists(path) && new FileInfo(path).Length >= 64 * 1024) File.Move(path, archive, true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, reason = name.ToString(), outcome = outcome?.ToString(), exitCode, credentialChanged, futureExpiry }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}

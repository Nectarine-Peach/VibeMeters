using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using VibeMeters.Models;

namespace VibeMeters.Services;

public sealed record LoginResult(bool Success, string Message);

/// <summary>Opens the installed provider's official interactive login, only on explicit user action.</summary>
public static class LoginService
{
    public static LoginResult StartLogin(UsageProviderKind provider)
    {
        var name = provider switch { UsageProviderKind.Codex => "codex", UsageProviderKind.Claude => "claude", _ => "gemini" };
        var executable = FindExecutable(name);
        if (executable is null)
            return new(false, provider switch
            {
                UsageProviderKind.Codex => "Codex CLI를 설치한 뒤 다시 로그인해 주세요.",
                UsageProviderKind.Claude => "Claude Code를 설치한 뒤 다시 로그인해 주세요.",
                _ => "Gemini CLI를 설치한 뒤 다시 열어 주세요."
            });
        try
        {
            // Absolute trusted shell path and encoded PowerShell command avoid cmd.exe's
            // metacharacter expansion in npm shim paths. No token is passed to the process.
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = provider switch
            {
                UsageProviderKind.Codex => "login",
                UsageProviderKind.Claude => "auth login --claudeai",
                // Official interactive startup dispatches this local slash command after auth.
                _ => "--prompt-interactive '/stats'"
            };
            var script = "& '" + executable.Replace("'", "''", StringComparison.Ordinal) + "' " + arguments;
            var start = new ProcessStartInfo(shell)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            start.Arguments = "-NoLogo -NoProfile -NoExit -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(start);
            return process is null ? new(false, "로그인 창을 열 수 없습니다.")
                : new(true, provider == UsageProviderKind.Gemini
                    ? "공식 Gemini 사용량 창을 열었습니다. 필요하면 /auth로 로그인해 주세요."
                    : "공식 로그인 창을 열었습니다. 완료하면 사용량이 자동으로 갱신됩니다.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, "로그인 창을 열 수 없습니다. 해당 CLI에서 직접 로그인해 주세요.");
        }
    }

    private static string? FindExecutable(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = new List<string>
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
        };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var entry in directories)
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var extension in new[] { ".exe", ".cmd" })
            {
                try
                {
                    var path = Path.GetFullPath(Path.Combine(directory, name + extension));
                    if (File.Exists(path)) return path;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return null;
    }
}

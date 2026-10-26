using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VibeMeters.Services;

/// <summary>Hidden official CLI launch, including npm shims, with no shell window.</summary>
internal static class OfficialCliProcess
{
    internal static ProcessStartInfo? CreateStartInfo(string name, params string[] arguments)
    {
        var executable = FindExecutable(name);
        if (executable is null) return null;

        ProcessStartInfo start;
        if (Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        else
        {
            // The absolute Windows PowerShell path and literal arguments prevent shell
            // metacharacter expansion in npm shim paths. No terminal window is allocated.
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = "& " + Quote(executable);
            foreach (var argument in arguments) script += " " + Quote(argument);
            script += "; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 }";
            start = new ProcessStartInfo(shell);
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.Environment["NO_COLOR"] = "1";
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        return start;
    }

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException) { }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string? FindExecutable(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = new List<string>
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
        };
        if (name == "codex" && Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR") is { Length: > 0 } customInstall)
            directories.Insert(0, customInstall);
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        // An installer updates User PATH after this process started. Re-read it without
        // changing the user's environment or requiring a VibeMeters restart.
        directories.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(Path.PathSeparator));
        directories.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(Path.PathSeparator));
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

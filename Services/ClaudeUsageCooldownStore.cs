using System.Text.Json;

namespace VibeMeters.Services;

// Keep the endpoint's protection through restarts without storing credentials or quota values.
internal sealed class ClaudeUsageCooldownStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "claude-usage-cooldown.json");

    internal sealed record State(DateTimeOffset RetryAt, int ConsecutiveLimits);

    internal State? Load()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 1024) return null;
            var state = JsonSerializer.Deserialize<State>(stream);
            return state is { ConsecutiveLimits: >= 1 and <= 6 } && state.RetryAt > DateTimeOffset.MinValue ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Save(DateTimeOffset retryAt, int consecutiveLimits)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temp, JsonSerializer.Serialize(new State(retryAt, consecutiveLimits)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Delete(temp); }
    }

    internal void Clear() => Delete(_path);

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

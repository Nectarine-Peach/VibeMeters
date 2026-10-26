using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

// Never serialized, logged, refreshed, or persisted by VibeMeters.
public sealed class UsageCredential
{
    public required string AccessToken { get; init; }
    public string? AccountId { get; init; }
    public string? Plan { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public override string ToString() => "[usage credential]";
}

public interface ICredentialReader
{
    Task<UsageCredential?> ReadAsync(UsageProviderKind provider, CancellationToken cancellationToken = default);
}

public sealed class CredentialReader : ICredentialReader
{
    private readonly string _codexHome;
    private readonly string _claudeHome;
    private readonly string _geminiHome;
    private readonly string? _grokHome;

    public CredentialReader(string? codexHome = null, string? claudeHome = null, string? grokHome = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _codexHome = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        _claudeHome = claudeHome ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        _geminiHome = Path.Combine(home, ".gemini");
        _grokHome = grokHome;
    }

    public async Task<UsageCredential?> ReadAsync(UsageProviderKind provider, CancellationToken cancellationToken = default)
    {
        // Gemini is opened in its official CLI; VibeMeters does not harvest its OAuth token.
        if (provider is UsageProviderKind.Gemini or UsageProviderKind.Grok) return null;
        var path = GetCredentialPath(provider);
        if (!File.Exists(path)) return null;
        // Shared reading avoids interfering with the CLI's atomic token updates.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Credential file is too large.");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (provider == UsageProviderKind.Codex)
        {
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;
            var token = GetString(tokens, "access_token");
            return string.IsNullOrWhiteSpace(token) ? null : new UsageCredential { AccessToken = token, AccountId = GetString(tokens, "account_id") };
        }
        if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;
        var access = GetString(oauth, "accessToken");
        DateTimeOffset? expiry = null;
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var milliseconds))
        {
            try { expiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { }
        }
        return string.IsNullOrWhiteSpace(access) ? null : new UsageCredential { AccessToken = access, Plan = GetString(oauth, "subscriptionType"), ExpiresAt = expiry };
    }

    public string GetCredentialPath(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Codex => Path.Combine(_codexHome, "auth.json"),
        UsageProviderKind.Claude => Path.Combine(_claudeHome, ".credentials.json"),
            UsageProviderKind.Gemini => Path.Combine(_geminiHome, "oauth_creds.json"),
        UsageProviderKind.Grok => _grokHome is null ? GrokAuthPath.Resolve() : Path.Combine(_grokHome, "auth.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string? GetString(JsonElement parent, string key) => parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

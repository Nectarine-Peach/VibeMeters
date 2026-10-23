using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeMeters.Models;

namespace VibeMeters.Services;

public interface IUsageProvider
{
    UsageProviderKind Kind { get; }
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default);
}

public static class UsageProviderFactory
{
    public static IReadOnlyList<IUsageProvider> CreateDefault()
    {
        var credentials = new CredentialReader();
        return [new UsageProvider(UsageProviderKind.Codex, credentials), new UsageProvider(UsageProviderKind.Claude, credentials), new GeminiUsageProvider()];
    }
}

public sealed class UsageProvider : IUsageProvider
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(8)
    }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly ICredentialReader _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAttempt;
    private UsageSnapshot? _last;
    private int _failures;
    private string? _credentialFingerprint;

    public UsageProviderKind Kind { get; }

    public UsageProvider(UsageProviderKind kind, ICredentialReader credentials, HttpClient? httpClient = null)
    {
        if (kind == UsageProviderKind.Gemini)
            throw new ArgumentException("Gemini uses its official CLI through GeminiUsageProvider.", nameof(kind));
        Kind = kind;
        _credentials = credentials;
        _http = httpClient ?? SharedClient;
    }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            UsageCredential? credential;
            try { credential = await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { return Save(UsageStatus.NotConnected, "로그인 정보를 읽을 수 없습니다. 해당 CLI에서 다시 로그인해 주세요."); }
            if (credential is null) return Save(UsageStatus.NotConnected, Kind == UsageProviderKind.Codex ? "Codex CLI에서 ChatGPT 계정으로 로그인해 주세요." : "Claude Code에서 구독 계정으로 로그인해 주세요.");
            // Credential updates must bypass an earlier failure's backoff after official CLI login.
            // Retain only a one-way digest, never a second copy of the access token.
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(credential.AccessToken + "\n" + credential.AccountId)));
            if (!string.Equals(fingerprint, _credentialFingerprint, StringComparison.Ordinal))
            {
                _credentialFingerprint = fingerprint;
                _failures = 0;
                _nextAttempt = default;
                _last = null;
            }
            if (_last is not null && now < _nextAttempt) return _last;
            if (credential.ExpiresAt is { } expiry && expiry <= now)
                return Save(UsageStatus.AuthenticationRequired, "로그인이 만료되었습니다. 해당 CLI를 실행하여 다시 로그인해 주세요.");

            var endpoint = Kind == UsageProviderKind.Codex ? "https://chatgpt.com/backend-api/wham/usage" : "https://api.anthropic.com/api/oauth/usage";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("VibeMeters/1.0");
            if (Kind == UsageProviderKind.Codex && !string.IsNullOrWhiteSpace(credential.AccountId))
                request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
            if (Kind == UsageProviderKind.Claude) request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            try
            {
                // ResponseContentRead applies HttpClient's timeout and size cap to the entire body.
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Save(UsageStatus.AuthenticationRequired, "인증이 만료되었거나 조회 권한이 없습니다. CLI에서 다시 로그인해 주세요.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _failures = Math.Min(_failures + 1, 6);
                    var retry = response.Headers.RetryAfter?.Date ?? now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60 * Math.Pow(2, _failures - 1)));
                    _nextAttempt = retry > now.AddSeconds(60) ? retry : now.AddSeconds(60);
                    return Save(UsageStatus.RateLimited, "서버 요청 제한 · 잠시 후 자동으로 다시 확인합니다.", retryAfter: _nextAttempt);
                }
                if (!response.IsSuccessStatusCode) return Failure("서버에 연결할 수 없습니다. 잠시 후 다시 확인합니다.");
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                var parsed = UsageParser.Parse(Kind, json.RootElement, now, credential.Plan);
                _failures = 0;
                _nextAttempt = now.AddSeconds(15);
                _last = parsed;
                return parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Failure("연결 시간이 초과되었습니다. 잠시 후 다시 확인합니다."); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or ArgumentException or InvalidOperationException)
            { return Failure("사용량을 가져올 수 없습니다. 네트워크 또는 응답 형식을 확인해 주세요."); }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        { return Save(UsageStatus.AuthenticationRequired, "로그인 정보 형식이 올바르지 않습니다. CLI에서 다시 로그인해 주세요."); }
        finally { _gate.Release(); }
    }

    private UsageSnapshot Failure(string message)
    {
        _failures = Math.Min(_failures + 1, 6);
        _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 30 * Math.Pow(2, _failures - 1)));
        return Save(UsageStatus.Unavailable, message, retryAfter: _nextAttempt);
    }

    private UsageSnapshot Save(UsageStatus status, string message, DateTimeOffset? retryAfter = null)
    {
        if (_last?.Windows.Count > 0) message = "이전 조회값 표시 중 · " + message;
        _last = new(Kind, status, _last?.Windows ?? [], DateTimeOffset.UtcNow, _last?.Plan, message, retryAfter);
        return _last;
    }
}

public static class UsageParser
{
    public static UsageSnapshot Parse(UsageProviderKind kind, JsonElement root, DateTimeOffset now, string? plan = null)
    {
        var windows = new List<UsageWindow>();
        if (kind == UsageProviderKind.Gemini)
            return new(kind, UsageStatus.Unavailable, windows, now, plan, "공식 Gemini CLI의 /stats에서 사용량을 확인하세요.");
        if (root.ValueKind != JsonValueKind.Object)
            return new(kind, UsageStatus.Unavailable, windows, now, plan, "서버가 올바른 사용량 정보를 제공하지 않았습니다.");
        if (kind == UsageProviderKind.Codex)
        {
            plan = Text(root, "plan_type") ?? plan;
            if (Object(root, "rate_limit", out var limits) || Object(root, "rate_limits", out limits))
            {
                AddCodex(limits, "primary_window", "5시간", windows, now);
                AddCodex(limits, "secondary_window", "주간", windows, now);
            }
        }
        else
        {
            AddClaude(root, "five_hour", "5시간", TimeSpan.FromHours(5), windows);
            AddClaude(root, "seven_day", "주간", TimeSpan.FromDays(7), windows);
            foreach (var property in root.EnumerateObject())
                if (property.Name.StartsWith("seven_day_", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Object)
                    AddClaude(root, property.Name, property.Name[10..].Replace('_', ' ') + " 주간", TimeSpan.FromDays(7), windows);
        }
        return new(kind, windows.Count > 0 ? UsageStatus.Ready : UsageStatus.Unavailable, windows, now, plan,
            windows.Count == 0 ? "계정에서 사용량 창을 제공하지 않습니다. 구독 상태를 확인해 주세요." : null);
    }

    private static void AddCodex(JsonElement root, string key, string fallback, List<UsageWindow> windows, DateTimeOffset now)
    {
        if (!Object(root, key, out var window) || !Number(window, "used_percent", out var used)) return;
        TimeSpan? duration = Number(window, "limit_window_seconds", out var seconds) && seconds > 0 && seconds <= 31536000 ? TimeSpan.FromSeconds(seconds) : null;
        var label = duration is { } d ? d.TotalDays >= 6 ? "주간" : d.TotalHours == 5 ? "5시간" : d.TotalHours >= 1 ? $"{d.TotalHours:0.#}시간" : $"{d.TotalMinutes:0.#}분" : fallback;
        var reset = Reset(window, "reset_at");
        if (reset is null && Number(window, "reset_after_seconds", out var after) && after >= 0 && after < 31536000) reset = now.AddSeconds(after);
        windows.Add(new(label, Math.Clamp(used, 0, 100), reset, duration));
    }

    private static void AddClaude(JsonElement root, string key, string label, TimeSpan duration, List<UsageWindow> windows)
    {
        if (!Object(root, key, out var window) || !Number(window, "utilization", out var used)) return;
        windows.Add(new(label, Math.Clamp(used, 0, 100), Reset(window, "resets_at"), duration));
    }

    private static DateTimeOffset? Reset(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var date)) return date;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        { try { return DateTimeOffset.FromUnixTimeSeconds(epoch); } catch (ArgumentOutOfRangeException) { } }
        return null;
    }
    private static bool Object(JsonElement root, string key, out JsonElement value) { value = default; return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object; }
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Number(JsonElement root, string key, out double number) { number = 0; return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number); }
}

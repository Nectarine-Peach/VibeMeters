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
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default, UsageFetchOptions options = default);
}

public static class UsageProviderFactory
{
    public static IReadOnlyList<IUsageProvider> CreateDefault()
    {
        var credentials = new CredentialReader();
        return [new UsageProvider(UsageProviderKind.Codex, credentials), new UsageProvider(UsageProviderKind.Claude, credentials), new GeminiUsageProvider(), new GrokUsageProvider()];
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
    private readonly IClaudeCredentialRefresher? _claudeRefresh;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAttempt;
    private UsageSnapshot? _last;
    private int _failures;
    private int _rateLimitFailures;
    private string? _credentialFingerprint;
    private string? _accountId;
    private int _loginCompleted;

    public UsageProviderKind Kind { get; }

    public UsageProvider(UsageProviderKind kind, ICredentialReader credentials, HttpClient? httpClient = null,
        IClaudeCredentialRefresher? claudeRefresher = null)
    {
        if (kind is not (UsageProviderKind.Codex or UsageProviderKind.Claude))
            throw new ArgumentException("This HTTP provider supports Codex and Claude only.", nameof(kind));
        Kind = kind;
        _credentials = credentials;
        _http = httpClient ?? SharedClient;
        _claudeRefresh = kind == UsageProviderKind.Claude ? claudeRefresher ?? (credentials is CredentialReader reader
            ? new ClaudeCredentialRefresher(Path.GetDirectoryName(reader.GetCredentialPath(kind))!) : null) : null;
    }

    // Explicit login can change Claude identity without a stable account ID.
    public void NotifyLoginCompleted() => Interlocked.Exchange(ref _loginCompleted, 1);

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default, UsageFetchOptions options = default)
    {
        var requestedAt = options.RequestedAt ?? DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (Interlocked.Exchange(ref _loginCompleted, 0) != 0)
            {
                var rateLimited = _last?.Status == UsageStatus.RateLimited && now < _nextAttempt;
                // Drop old account values immediately, including during a server cooldown.
                _last = rateLimited ? new(Kind, UsageStatus.RateLimited, [], now,
                    Message: "계정이 변경되었습니다. 기존 요청 제한 대기 후 사용량을 확인합니다.", RetryAfter: _nextAttempt) : null;
                _credentialFingerprint = null;
                _accountId = null;
                _failures = 0;
                if (!rateLimited) { _nextAttempt = default; _rateLimitFailures = 0; }
            }
            // Server cooldown applies to all refresh triggers, including automatic CLI
            // token renewal and explicit refresh. Claude does not supply a stable account ID.
            if (_last?.Status == UsageStatus.RateLimited && now < _nextAttempt) return RefreshPolicy.Reuse(_last, _nextAttempt);
            UsageCredential? credential;
            try { credential = await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { return Save(UsageStatus.NotConnected, "로그인 정보를 읽을 수 없습니다. 해당 CLI에서 다시 로그인해 주세요."); }
            if (credential is null) return Save(UsageStatus.NotConnected, Kind == UsageProviderKind.Codex ? "Codex CLI에서 ChatGPT 계정으로 로그인해 주세요." : "Claude Code에서 구독 계정으로 로그인해 주세요.");
            // A fresh login can recover authentication errors immediately. Routine token
            // renewal must not bypass the normal polling floor or network/server backoff.
            // Retain only a one-way digest, never a second copy of the access token.
            var fingerprint = Fingerprint(credential);
            if (!string.Equals(fingerprint, _credentialFingerprint, StringComparison.Ordinal))
            {
                var firstCredential = _credentialFingerprint is null;
                var accountChanged = !string.IsNullOrWhiteSpace(_accountId)
                    && !string.IsNullOrWhiteSpace(credential.AccountId)
                    && !string.Equals(_accountId, credential.AccountId, StringComparison.Ordinal);
                _credentialFingerprint = fingerprint;
                _accountId = credential.AccountId;
                if (firstCredential || accountChanged || _last?.Status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected)
                {
                    _failures = 0;
                    if (firstCredential || accountChanged) _rateLimitFailures = 0;
                    _nextAttempt = default;
                    _last = null;
                }
            }
            if (RefreshPolicy.CanReuse(_last, _nextAttempt, now, options, requestedAt)) return RefreshPolicy.Reuse(_last!, _nextAttempt);
            var refreshed = false;
            if (credential.ExpiresAt is { } expiry && expiry <= now)
            {
                if (Kind != UsageProviderKind.Claude)
                    return Save(UsageStatus.AuthenticationRequired, "인증 갱신이 필요합니다. 설정에서 다시 로그인해 주세요.");
                var recovery = await RefreshClaudeAsync(credential, cancellationToken).ConfigureAwait(false);
                if (recovery.Credential is null) return ClaudeAuthenticationRequired(recovery.Outcome);
                credential = recovery.Credential;
                _credentialFingerprint = Fingerprint(credential);
                refreshed = true;
            }

            for (;;)
            {
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
                    {
                        if (Kind == UsageProviderKind.Claude && response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                        {
                            var recovery = await RefreshClaudeAsync(credential, cancellationToken).ConfigureAwait(false);
                            if (recovery.Credential is null) return ClaudeAuthenticationRequired(recovery.Outcome);
                            credential = recovery.Credential;
                            _credentialFingerprint = Fingerprint(credential);
                            refreshed = true;
                            continue; // At most one retry, using only the newly read token.
                        }
                        _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
                        return Save(UsageStatus.AuthenticationRequired, response.StatusCode == HttpStatusCode.Unauthorized
                            ? "인증이 만료되었습니다. 설정에서 다시 로그인해 주세요."
                            : "조회 권한이 없습니다. 회사 계정의 서비스 권한·SSO 및 네트워크 정책을 확인해 주세요.");
                    }
                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                        return Failure("회사 프록시 인증이 필요합니다. Windows 프록시 및 네트워크 로그인을 확인해 주세요.");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _rateLimitFailures = Math.Min(_rateLimitFailures + 1, 6);
                        var receivedAt = DateTimeOffset.UtcNow;
                        // Conservative app policy; this is not a claimed published provider limit.
                        var minutes = Kind == UsageProviderKind.Claude
                            ? new[] { 5, 10, 20, 30, 60, 60 }[_rateLimitFailures - 1]
                            : Math.Min(60, Math.Pow(2, _rateLimitFailures - 1));
                        _nextAttempt = receivedAt.AddMinutes(minutes);
                        var retryHeader = response.Headers.RetryAfter;
                        var serverRetry = retryHeader?.Date ?? (retryHeader?.Delta is { } delta ? receivedAt.Add(delta) : receivedAt);
                        if (serverRetry > _nextAttempt) _nextAttempt = serverRetry;
                        var previous = _last?.Windows.Count > 0 ? " · 이전 값 표시" : "";
                        var retryTime = _nextAttempt.ToLocalTime();
                        var timeLabel = retryTime.Date == DateTimeOffset.Now.Date ? retryTime.ToString("HH:mm") : retryTime.ToString("MM/dd HH:mm");
                        return Save(UsageStatus.RateLimited, $"조회 일시 제한{previous} · {timeLabel} 이후 재시도", retryAfter: _nextAttempt);
                    }
                    if (!response.IsSuccessStatusCode) return Failure("서버에 연결할 수 없습니다. 잠시 후 다시 확인합니다.");
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var observedAt = DateTimeOffset.UtcNow;
                    var parsed = UsageParser.Parse(Kind, json.RootElement, observedAt, credential.Plan);
                    if (parsed.Status != UsageStatus.Ready)
                        return Failure(parsed.Message ?? "사용량 정보를 확인할 수 없습니다. 잠시 후 다시 확인합니다.");
                    _failures = 0;
                    _rateLimitFailures = 0;
                    _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Kind == UsageProviderKind.Claude ? 120 : 15);
                    _credentialFingerprint = Fingerprint(credential);
                    _last = parsed with { ObservedAt = observedAt, NextQueryAt = _nextAttempt };
                    return _last;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return Failure("연결 시간이 초과되었습니다. 잠시 후 다시 확인합니다."); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or ArgumentException or InvalidOperationException)
                { return Failure("사용량을 가져올 수 없습니다. 네트워크 또는 응답 형식을 확인해 주세요."); }
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        { return Save(UsageStatus.AuthenticationRequired, "로그인 정보 형식이 올바르지 않습니다. CLI에서 다시 로그인해 주세요."); }
        finally { _gate.Release(); }
    }

    private static string Fingerprint(UsageCredential credential) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(credential.AccessToken + "\n" + credential.AccountId)));

    private async Task<(UsageCredential? Credential, ClaudeRefreshOutcome Outcome)> RefreshClaudeAsync(
        UsageCredential rejected, CancellationToken cancellationToken)
    {
        if (_last is not null) _last = _last with { Windows = [], ObservedAt = null };
        try
        {
            // Another CLI/login may already have replaced the rejected token. Read it
            // again rather than writing an old account/token over the current file.
            var current = await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false);
            if (IsReplacement(current)) return (current, ClaudeRefreshOutcome.Completed);
            var outcome = _claudeRefresh is null ? ClaudeRefreshOutcome.CliMissing
                : await _claudeRefresh.RefreshAsync(cancellationToken).ConfigureAwait(false);
            current = await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false);
            return (IsReplacement(current) ? current : null, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (null, ClaudeRefreshOutcome.Failed); }

        bool IsReplacement(UsageCredential? value) => value is not null
            && value.ExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow
            && !string.Equals(value.AccessToken, rejected.AccessToken, StringComparison.Ordinal);
    }

    private UsageSnapshot ClaudeAuthenticationRequired(ClaudeRefreshOutcome outcome)
    {
        _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
        return Save(UsageStatus.AuthenticationRequired, outcome switch
        {
            ClaudeRefreshOutcome.CliMissing => "자동 인증 갱신에 Claude Code가 필요합니다. 설정에서 다시 로그인해 주세요.",
            ClaudeRefreshOutcome.CliOutdated => "자동 인증 갱신에는 Claude Code 2.1.301 이상이 필요합니다. CLI를 업데이트하거나 다시 로그인해 주세요.",
            _ => "자동 인증 갱신에 실패했습니다. 설정에서 Claude에 다시 로그인해 주세요."
        });
    }

    private UsageSnapshot Failure(string message)
    {
        _failures = Math.Min(_failures + 1, 6);
        var floor = Kind == UsageProviderKind.Claude ? 120 : 30;
        _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(1800, floor * Math.Pow(2, _failures - 1)));
        return Save(UsageStatus.Unavailable, message, retryAfter: _nextAttempt);
    }

    private UsageSnapshot Save(UsageStatus status, string message, DateTimeOffset? retryAfter = null)
    {
        var clear = Kind == UsageProviderKind.Claude && status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected;
        if (!clear && status != UsageStatus.RateLimited && _last?.Windows.Count > 0) message = "이전 조회값 표시 중 · " + message;
        _last = new(Kind, status, clear ? [] : _last?.Windows ?? [], DateTimeOffset.UtcNow, _last?.Plan, message, retryAfter,
            ObservedAt: clear ? null : _last?.ObservedAt,
            NextQueryAt: _nextAttempt > DateTimeOffset.UtcNow && _nextAttempt != DateTimeOffset.MaxValue ? _nextAttempt : null,
            WaitReason: RefreshPolicy.Reason(status));
        return _last;
    }
}

public static class UsageParser
{
    public static UsageSnapshot Parse(UsageProviderKind kind, JsonElement root, DateTimeOffset now, string? plan = null)
    {
        var windows = new List<UsageWindow>();
        string? message = null;
        var connectedWithoutQuota = false;
        if (kind == UsageProviderKind.Gemini)
            return new(kind, UsageStatus.Unavailable, windows, now, plan, "공식 Gemini CLI의 /stats에서 사용량을 확인하세요.");
        if (kind == UsageProviderKind.Grok)
            return new(kind, UsageStatus.Unavailable, windows, now, plan, "공식 Grok Build CLI에서 로그인한 뒤 사용량을 확인해 주세요.");
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
            if (AddCodexIndividualLimit(root, windows, now))
                message = "계정에 적용된 개인 사용 한도입니다. 초기화 시점은 서버가 제공한 값입니다.";
            // Metered accounts can return null quota windows and no numeric balance.
            // Treat the reported credit state as connected without inventing a percentage.
            if (windows.Count == 0 && Object(root, "credits", out var credits)
                && (Boolean(credits, "has_credits") is not null || Boolean(credits, "unlimited") is not null))
            {
                connectedWithoutQuota = true;
                message = Boolean(credits, "unlimited") == true ? "연결됨 · 계정이 무제한 크레딧 사용을 보고했습니다."
                    : Boolean(credits, "overage_limit_reached") == true ? "연결됨 · 계정의 추가 사용 한도에 도달했습니다."
                    : Boolean(credits, "has_credits") == true ? "연결됨 · 크레딧 사용 가능. 계정에서 사용량 비율을 제공하지 않습니다."
                    : "연결됨 · 계정에서 사용량 비율을 제공하지 않습니다. 크레딧 설정을 확인해 주세요.";
            }
        }
        else
        {
            AddClaude(root, "five_hour", "5시간", TimeSpan.FromHours(5), windows);
            AddClaude(root, "seven_day", "주간", TimeSpan.FromDays(7), windows);
            foreach (var property in root.EnumerateObject())
                if (property.Name.StartsWith("seven_day_", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Object)
                    AddClaude(root, property.Name, property.Name[10..].Replace('_', ' ') + " 주간", TimeSpan.FromDays(7), windows);
            if (Object(root, "extra_usage", out var extra) && Boolean(extra, "is_enabled") == true)
            {
                if (Number(extra, "utilization", out var used))
                {
                    windows.Add(new("월 추가 사용", Math.Clamp(used, 0, 100), Reset(extra, "resets_at")));
                    message = "월 추가 사용은 계정에서 제공한 추가 사용 예산 비율이며, 5시간·주간 구독 한도와 다릅니다.";
                }
                else if (windows.Count == 0)
                {
                    connectedWithoutQuota = true;
                    message = "연결됨 · 추가 사용이 활성화되어 있으나 계정에서 사용량 비율을 제공하지 않습니다.";
                }
            }
        }
        return new(kind, windows.Count > 0 || connectedWithoutQuota ? UsageStatus.Ready : UsageStatus.Unavailable, windows, now, plan,
            message ?? (windows.Count == 0 ? "계정에서 사용량 창을 제공하지 않습니다. 구독 상태를 확인해 주세요." : null));
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

    private static bool AddCodexIndividualLimit(JsonElement root, List<UsageWindow> windows, DateTimeOffset now)
    {
        if (!Object(root, "spend_control", out var spend) || !Object(spend, "individual_limit", out var limit)) return false;
        double used;
        // Match the official client: remaining_percent is authoritative. Do not
        // derive percentages from string-valued credit amounts or assume a period.
        if (Number(limit, "remaining_percent", out var remaining)) used = 100 - Math.Clamp(remaining, 0, 100);
        else if (!Number(limit, "used_percent", out used)) return false;
        var reset = Reset(limit, "reset_at");
        if (reset is null && Number(limit, "reset_after_seconds", out var after) && after >= 0 && after < 31536000)
            reset = now.AddSeconds(after);
        windows.Add(new("개인 한도", Math.Clamp(used, 0, 100), reset));
        return true;
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
    private static bool? Boolean(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static bool Object(JsonElement root, string key, out JsonElement value) { value = default; return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object; }
    private static string? Text(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Number(JsonElement root, string key, out double number) { number = 0; return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number); }
}

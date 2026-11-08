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
    private static readonly TimeSpan ClaudeRenewalWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClaudeStaleWindow = TimeSpan.FromMinutes(30);
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(8)
    }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly ICredentialReader _credentials;
    private readonly ICodexAppServerUsage? _codexAppServer;
    private readonly IClaudeCredentialRefresher? _claudeRefresh;
    private readonly IClaudeRecoveryClock _clock;
    private readonly IClaudeAuthDiagnostics? _diagnostics;
    private readonly ClaudeUsageCooldownStore? _claudeCooldownStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAttempt;
    private UsageSnapshot? _last;
    private int _failures;
    private int _rateLimitFailures;
    private string? _credentialFingerprint;
    private string? _claudeProactiveFingerprint;
    private string? _claudeObservedFingerprint;
    private string? _claudeRenewedFingerprint;
    private string? _accountId;
    private bool _codexUsedCliWithoutFile;
    private int _loginCompleted;
    private int _loginGeneration;
    public event Action<UsageSnapshot>? AuthenticationProgress;

    public UsageProviderKind Kind { get; }

    public UsageProvider(UsageProviderKind kind, ICredentialReader credentials, HttpClient? httpClient = null,
        IClaudeCredentialRefresher? claudeRefresher = null, IClaudeRecoveryClock? clock = null,
        IClaudeAuthDiagnostics? diagnostics = null, string? claudeCooldownDirectory = null)
        : this(kind, credentials, null, httpClient, claudeRefresher, clock, diagnostics, claudeCooldownDirectory) { }

    internal UsageProvider(UsageProviderKind kind, ICredentialReader credentials, ICodexAppServerUsage? codexAppServer,
        HttpClient? httpClient, IClaudeCredentialRefresher? claudeRefresher = null, IClaudeRecoveryClock? clock = null,
        IClaudeAuthDiagnostics? diagnostics = null, string? claudeCooldownDirectory = null)
    {
        if (kind is not (UsageProviderKind.Codex or UsageProviderKind.Claude))
            throw new ArgumentException("This HTTP provider supports Codex and Claude only.", nameof(kind));
        Kind = kind;
        _credentials = credentials;
        _http = httpClient ?? SharedClient;
        _codexAppServer = kind == UsageProviderKind.Codex ? codexAppServer ?? (httpClient is null && credentials is CredentialReader codexReader
            ? new CodexAppServerUsage(Path.GetDirectoryName(codexReader.GetCredentialPath(kind))!) : null) : null;
        _clock = clock ?? ClaudeRecoveryClock.Instance;
        _diagnostics = diagnostics ?? (kind == UsageProviderKind.Claude && credentials is CredentialReader ? new ClaudeAuthDiagnostics() : null);
        _claudeRefresh = kind == UsageProviderKind.Claude ? claudeRefresher ?? (credentials is CredentialReader reader
            ? new ClaudeCredentialRefresher(Path.GetDirectoryName(reader.GetCredentialPath(kind))!, clock: _clock, diagnostics: _diagnostics) : null) : null;
        if (kind == UsageProviderKind.Claude)
        {
            if (claudeCooldownDirectory is null && credentials is CredentialReader && httpClient is null)
                claudeCooldownDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeMeters");
            if (claudeCooldownDirectory is not null) _claudeCooldownStore = new(claudeCooldownDirectory);
            if (_claudeCooldownStore?.Load() is { } saved)
            {
                _rateLimitFailures = saved.ConsecutiveLimits;
                if (saved.RetryAt > _clock.UtcNow)
                {
                    _nextAttempt = saved.RetryAt;
                    SaveRateLimited();
                }
            }
        }
    }

    // Explicit login can change Claude identity without a stable account ID.
    public void NotifyLoginCompleted() { Interlocked.Increment(ref _loginGeneration); Interlocked.Exchange(ref _loginCompleted, 1); }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default, UsageFetchOptions options = default)
    {
        var requestedAt = options.RequestedAt ?? _clock.UtcNow;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _loginGeneration);
            var recoveringBeforeFetch = _last?.Status is UsageStatus.AuthenticationRequired or UsageStatus.AuthenticationRetry or UsageStatus.AuthenticationRecovering;
            var now = _clock.UtcNow;
            if (Interlocked.Exchange(ref _loginCompleted, 0) != 0)
            {
                var rateLimited = _last?.Status == UsageStatus.RateLimited && now < _nextAttempt;
                // Drop old account values immediately, including during a server cooldown.
                _last = rateLimited ? new(Kind, UsageStatus.RateLimited, [], now,
                    Message: "계정이 변경되었습니다. 기존 요청 제한 대기 후 사용량을 확인합니다.", RetryAfter: _nextAttempt) : null;
                _credentialFingerprint = null;
                _claudeProactiveFingerprint = null;
                _claudeObservedFingerprint = null;
                _claudeRenewedFingerprint = null;
                _accountId = null;
                _codexUsedCliWithoutFile = false;
                _failures = 0;
                if (!rateLimited)
                {
                    _nextAttempt = default;
                    if (Kind != UsageProviderKind.Claude) _rateLimitFailures = 0;
                }
            }
            // Server cooldown applies to all refresh triggers, including automatic CLI
            // token renewal and explicit refresh. Claude does not supply a stable account ID.
            var inCooldown = _last?.Status == UsageStatus.RateLimited && now < _nextAttempt;
            if (inCooldown && (Kind != UsageProviderKind.Claude || _last!.Windows.Count == 0))
                return RefreshPolicy.Reuse(_last!, _nextAttempt);
            UsageCredential? credential;
            var credentialReadFailed = false;
            try { credential = await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                if (inCooldown) return ReuseClaudeCooldown(now, null);
                if (Kind == UsageProviderKind.Claude)
                    return ClaudeRecoveryDeferred(ClaudeRefreshOutcome.Failed, "로그인 파일을 잠시 읽지 못했습니다. 기존 인증을 다시 확인합니다.");
                credential = null;
                credentialReadFailed = true;
            }
            // A local credential check can hide stale values after logout or account switch
            // without sending any requests during the server's cooldown.
            if (inCooldown) return ReuseClaudeCooldown(now, credential);
            if (credential is null)
            {
                if (Kind == UsageProviderKind.Claude)
                    return Save(UsageStatus.NotConnected, "Claude Code에서 구독 계정으로 로그인해 주세요.");
                if (_codexUsedCliWithoutFile && RefreshPolicy.CanReuse(_last, _nextAttempt, now, options, requestedAt))
                    return RefreshPolicy.Reuse(_last!, _nextAttempt);
                var fromCli = await TryCodexAppServerAsync(null, generation, cancellationToken).ConfigureAwait(false);
                if (fromCli is not null) return fromCli;
                if (_codexAppServer is not null) _last = null;
                return Save(UsageStatus.NotConnected, credentialReadFailed
                    ? "로그인 정보를 읽을 수 없습니다. 해당 CLI에서 다시 로그인해 주세요."
                    : "Codex CLI에서 ChatGPT 계정으로 로그인해 주세요.");
            }
            _codexUsedCliWithoutFile = false;
            // A fresh login can recover authentication errors immediately. Routine token
            // renewal must not bypass the normal polling floor or network/server backoff.
            // Retain only a one-way digest, never a second copy of the access token.
            var fingerprint = Fingerprint(credential);
            if (Kind == UsageProviderKind.Claude && _last?.Status == UsageStatus.NotConnected)
            { _last = null; _nextAttempt = default; }
            if (!string.Equals(fingerprint, _credentialFingerprint, StringComparison.Ordinal))
            {
                var firstCredential = _credentialFingerprint is null;
                var accountChanged = !string.IsNullOrWhiteSpace(_accountId)
                    && !string.IsNullOrWhiteSpace(credential.AccountId)
                    && !string.Equals(_accountId, credential.AccountId, StringComparison.Ordinal);
                _credentialFingerprint = fingerprint;
                _accountId = credential.AccountId;
                if (firstCredential || accountChanged || _last?.WaitReason == UsageWaitReason.AuthenticationBackoff || _last?.Status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected or UsageStatus.AuthenticationRetry)
                {
                    _failures = 0;
                    if (Kind != UsageProviderKind.Claude && (firstCredential || accountChanged)) _rateLimitFailures = 0;
                    _nextAttempt = default;
                    _last = null;
                }
            }
            var retryRecovery = Kind == UsageProviderKind.Claude && options.RecoverAuthentication
                && (_last?.Status is UsageStatus.AuthenticationRetry or UsageStatus.AuthenticationRequired || _last?.WaitReason == UsageWaitReason.AuthenticationBackoff);
            if (!retryRecovery && RefreshPolicy.CanReuse(_last, _nextAttempt, now, options, requestedAt))
            {
                if (Kind == UsageProviderKind.Claude && _last!.Status == UsageStatus.Unavailable)
                    PruneClaudeQuota(now, CacheFingerprint(credential, now));
                return RefreshPolicy.Reuse(_last!, _nextAttempt);
            }
            var refreshed = false;
            ClaudeRefreshOutcome? proactiveOutcome = null;
            if (credential.ExpiresAt is { } expiry)
            {
                var remaining = expiry - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero && Kind != UsageProviderKind.Claude)
                    return Save(UsageStatus.AuthenticationRequired, "인증 갱신이 필요합니다. 설정에서 다시 로그인해 주세요.");
                if (Kind == UsageProviderKind.Claude && remaining <= ClaudeRenewalWindow)
                {
                    var proactive = remaining > TimeSpan.Zero;
                    if (!proactive || _claudeProactiveFingerprint != fingerprint)
                    {
                        if (proactive) _claudeProactiveFingerprint = fingerprint;
                        var recovery = await RefreshClaudeAsync(credential, cancellationToken, proactive).ConfigureAwait(false);
                        if (recovery.Credential is null) return ClaudeRecoveryDeferred(recovery.Outcome);
                        refreshed = recovery.Credential.AccessToken != credential.AccessToken;
                        if (refreshed)
                        {
                            _last = null; // The replacement could also be a different account.
                            _claudeRenewedFingerprint = Fingerprint(recovery.Credential);
                        }
                        else proactiveOutcome = recovery.Outcome;
                        credential = recovery.Credential;
                        _credentialFingerprint = Fingerprint(credential);
                    }
                }
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
                        if (Kind == UsageProviderKind.Claude && response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            // The usage endpoint alone cannot tell us whether the refresh credential is revoked.
                            if (refreshed) return ClaudeRecoveryDeferred(ClaudeRefreshOutcome.Failed);
                            if (proactiveOutcome is { } outcome) return ClaudeRecoveryDeferred(outcome);
                            var recovery = await RefreshClaudeAsync(credential, cancellationToken).ConfigureAwait(false);
                            if (recovery.Credential is null) return ClaudeRecoveryDeferred(recovery.Outcome);
                            credential = recovery.Credential;
                            _credentialFingerprint = Fingerprint(credential);
                            _claudeRenewedFingerprint = _credentialFingerprint;
                            refreshed = true;
                            continue; // At most one retry, using only the newly read token.
                        }
                        if (Kind == UsageProviderKind.Codex && response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            var fromCli = await TryCodexAppServerAsync(credential, generation, cancellationToken).ConfigureAwait(false);
                            if (fromCli is not null) return fromCli;
                        }
                        _nextAttempt = _clock.UtcNow.AddMinutes(5);
                        if (Kind == UsageProviderKind.Claude && response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            if (_last is not null) _last = _last with { Windows = [], ObservedAt = null };
                            return _last = Save(UsageStatus.Unavailable, "조회 권한이 없습니다. 회사 계정의 서비스 권한·SSO 및 네트워크 정책을 확인해 주세요.") with { WaitReason = UsageWaitReason.AuthenticationBackoff };
                        }
                        return Save(UsageStatus.AuthenticationRequired, response.StatusCode == HttpStatusCode.Unauthorized
                            ? "인증이 만료되었습니다. 설정에서 다시 로그인해 주세요."
                            : "조회 권한이 없습니다. 회사 계정의 서비스 권한·SSO 및 네트워크 정책을 확인해 주세요.");
                    }
                    if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                        return Failure("회사 프록시 인증이 필요합니다. Windows 프록시 및 네트워크 로그인을 확인해 주세요.");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var receivedAt = _clock.UtcNow;
                        var retryHeader = response.Headers.RetryAfter;
                        // Commit the deadline before re-reading credentials. Even if the caller
                        // cancels that read, the next fetch still honors this server response.
                        RecordRateLimit(receivedAt, retryHeader?.Date ?? (retryHeader?.Delta is { } delta ? receivedAt.Add(delta) : null));
                        if (Kind == UsageProviderKind.Claude && _last!.Windows.Count > 0)
                            PruneClaudeQuota(receivedAt, CacheFingerprint(await ReadClaudeSafelyAsync(cancellationToken).ConfigureAwait(false), receivedAt));
                        return _last!;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        const string message = "서버에 연결할 수 없습니다. 잠시 후 다시 확인합니다.";
                        return (int)response.StatusCode >= 500
                            ? await TransientFailureAsync(message, cancellationToken).ConfigureAwait(false) : Failure(message);
                    }
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var observedAt = _clock.UtcNow;
                    var parsed = UsageParser.Parse(Kind, json.RootElement, observedAt, credential.Plan);
                    if (parsed.Status != UsageStatus.Ready)
                        return Failure(parsed.Message ?? "사용량 정보를 확인할 수 없습니다. 잠시 후 다시 확인합니다.");
                    if (Kind == UsageProviderKind.Claude)
                    {
                        var current = await ReadClaudeSafelyAsync(cancellationToken).ConfigureAwait(false);
                        if (generation != Volatile.Read(ref _loginGeneration) || current is null || Fingerprint(current) != Fingerprint(credential))
                            return ClaudeRecoveryDeferred(ClaudeRefreshOutcome.Pending, "조회 중 로그인이 변경되었습니다. 현재 인증을 다시 확인합니다.");
                        var renewalObserved = _claudeRenewedFingerprint == Fingerprint(credential);
                        if (renewalObserved || options.RecoverAuthentication || recoveringBeforeFetch)
                            _diagnostics?.Write(ClaudeAuthEvent.RecoverySucceeded, credentialChanged: renewalObserved, futureExpiry: credential.ExpiresAt > _clock.UtcNow);
                        _claudeRenewedFingerprint = null;
                    }
                    _failures = 0;
                    if (_rateLimitFailures > 0) _claudeCooldownStore?.Clear();
                    _rateLimitFailures = 0;
                    _nextAttempt = _clock.UtcNow.AddSeconds(Kind == UsageProviderKind.Claude ? RefreshPolicy.ClaudeSuccessCacheSeconds : 15);
                    _credentialFingerprint = Fingerprint(credential);
                    if (Kind == UsageProviderKind.Claude) _claudeObservedFingerprint = _credentialFingerprint;
                    _last = parsed with { ObservedAt = observedAt, NextQueryAt = _nextAttempt };
                    return _last;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return await TransientFailureAsync("연결 시간이 초과되었습니다. 잠시 후 다시 확인합니다.", cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or ArgumentException or InvalidOperationException)
                {
                    const string message = "사용량을 가져올 수 없습니다. 네트워크 또는 응답 형식을 확인해 주세요.";
                    return ex is HttpRequestException or IOException
                        ? await TransientFailureAsync(message, cancellationToken).ConfigureAwait(false) : Failure(message);
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        { return Save(UsageStatus.AuthenticationRequired, "로그인 정보 형식이 올바르지 않습니다. CLI에서 다시 로그인해 주세요."); }
        finally { _gate.Release(); }
    }

    private static string Fingerprint(UsageCredential credential) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(credential.AccessToken + "\n" + credential.AccountId)));

    private async Task<UsageSnapshot?> TryCodexAppServerAsync(UsageCredential? rejected, int generation, CancellationToken cancellationToken)
    {
        if (_codexAppServer is null) return null;
        var result = await _codexAppServer.FetchAsync(rejected is not null, rejected?.AccountId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        if (result?.Status == UsageStatus.RateLimited)
        {
            _last = null;
            return RecordRateLimit(now, result.RetryAfter);
        }
        if (result is null) return null;
        if (generation != Volatile.Read(ref _loginGeneration))
        {
            _last = null;
            return new(Kind, UsageStatus.Unavailable, [], now, Message: "조회 중 로그인이 변경되었습니다. 현재 인증을 다시 확인합니다.");
        }

        _codexUsedCliWithoutFile = rejected is null;
        if (result.Status != UsageStatus.Ready)
        {
            _last = null;
            var message = result.Message ?? "Codex CLI에서 사용량을 확인할 수 없습니다. 잠시 후 다시 확인합니다.";
            if (result.Status == UsageStatus.Unavailable) return Failure(message);
            _nextAttempt = now.AddMinutes(5);
            return Save(result.Status, message);
        }
        _failures = _rateLimitFailures = 0;
        _nextAttempt = now.AddSeconds(15);
        return _last = result with { ObservedAt = result.ObservedAt ?? result.CheckedAt, NextQueryAt = _nextAttempt };
    }

    private static string? CacheFingerprint(UsageCredential? credential, DateTimeOffset now)
        => credential is not null && (credential.ExpiresAt is not { } expiry || expiry > now) ? Fingerprint(credential) : null;

    private UsageSnapshot ReuseClaudeCooldown(DateTimeOffset now, UsageCredential? credential)
    {
        PruneClaudeQuota(now, CacheFingerprint(credential, now));
        return RefreshPolicy.Reuse(_last!, _nextAttempt);
    }

    private void PruneClaudeQuota(DateTimeOffset now, string? fingerprint)
    {
        if (_last is not { Windows.Count: > 0 } last) return;
        var sameCredential = fingerprint is not null && fingerprint == _claudeObservedFingerprint;
        if (!sameCredential || last.ObservedAt is not { } observed || now < observed || now - observed >= ClaudeStaleWindow)
        {
            _last = last with { Windows = [], ObservedAt = null };
            return;
        }

        var windows = last.Windows.Where(window => window.ResetsAt is null || window.ResetsAt > now).ToArray();
        if (windows.Length != last.Windows.Count)
            _last = last with { Windows = windows, ObservedAt = windows.Length == 0 ? null : observed };
    }

    private async Task<(UsageCredential? Credential, ClaudeRefreshOutcome Outcome)> RefreshClaudeAsync(
        UsageCredential previous, CancellationToken cancellationToken, bool proactive = false)
    {
        if (!proactive)
        {
            _nextAttempt = default;
            _last = new(Kind, UsageStatus.AuthenticationRecovering, [], _clock.UtcNow, _last?.Plan,
                Message: "인증 갱신을 확인하고 있습니다. 잠시 기다려 주세요.", WaitReason: UsageWaitReason.AuthenticationRecovery);
            AuthenticationProgress?.Invoke(_last);
        }
        _diagnostics?.Write(proactive ? ClaudeAuthEvent.ProactiveRenewalStarted : ClaudeAuthEvent.RecoveryStarted);
        try
        {
            // Another CLI/login may already have replaced the rejected token. Read it
            // again rather than writing an old account/token over the current file.
            var current = await ReadClaudeSafelyAsync(cancellationToken).ConfigureAwait(false);
            if (IsReplacement(current)) return (current, ClaudeRefreshOutcome.Completed);
            var outcome = _claudeRefresh is null ? ClaudeRefreshOutcome.CliMissing
                : await _claudeRefresh.RefreshAsync(cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt <= 16; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = await ReadClaudeSafelyAsync(cancellationToken).ConfigureAwait(false);
                if (IsReplacement(current))
                {
                    _diagnostics?.Write(ClaudeAuthEvent.FileObserved, outcome, credentialChanged: true, futureExpiry: true);
                    return (current, outcome);
                }
                // A still-valid credential can serve this query while the CLI saves a renewal.
                if (proactive && current?.ExpiresAt > _clock.UtcNow && current.AccessToken == previous.AccessToken)
                    return (current, outcome);
                if (attempt == 16 || outcome is ClaudeRefreshOutcome.CliMissing or ClaudeRefreshOutcome.CliOutdated or ClaudeRefreshOutcome.AuthenticationRejected) break;
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            _diagnostics?.Write(ClaudeAuthEvent.FileObserved, outcome, credentialChanged: current is not null && current.AccessToken != previous.AccessToken,
                futureExpiry: current?.ExpiresAt > _clock.UtcNow);
            return (null, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (null, ClaudeRefreshOutcome.Failed); }

        bool IsReplacement(UsageCredential? value) => value is not null
            && value.ExpiresAt is { } expiry && expiry > _clock.UtcNow
            && !string.Equals(value.AccessToken, previous.AccessToken, StringComparison.Ordinal);
    }

    private async Task<UsageCredential?> ReadClaudeSafelyAsync(CancellationToken cancellationToken)
    {
        try { return await _credentials.ReadAsync(Kind, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private UsageSnapshot ClaudeRecoveryDeferred(ClaudeRefreshOutcome outcome, string? message = null)
    {
        var rejected = outcome == ClaudeRefreshOutcome.AuthenticationRejected;
        _nextAttempt = _clock.UtcNow.AddSeconds(rejected ? 300 : 30);
        _diagnostics?.Write(ClaudeAuthEvent.RecoveryDeferred, outcome);
        var snapshot = Save(rejected ? UsageStatus.AuthenticationRequired : UsageStatus.AuthenticationRetry, message ?? (outcome switch
        {
            ClaudeRefreshOutcome.AuthenticationRejected => "저장된 인증 갱신이 거부되었습니다. Claude에 다시 로그인해 주세요.",
            ClaudeRefreshOutcome.CliMissing => "자동 복구에 Claude Code가 필요합니다. 다시 확인하거나 로그인할 수 있습니다.",
            ClaudeRefreshOutcome.CliOutdated => "자동 복구에는 Claude Code 2.1.317 이상이 필요합니다. Claude Code를 업데이트해 주세요.",
            ClaudeRefreshOutcome.Pending or ClaudeRefreshOutcome.TimedOut => "다른 인증 갱신이나 저장을 기다리고 있습니다. 잠시 후 다시 확인합니다.",
            _ => "인증 복구를 아직 확인하지 못했습니다. 잠시 후 다시 확인하거나 로그인을 선택할 수 있습니다."
        }));
        return _last = snapshot with { AuthenticationWorkPending = outcome is ClaudeRefreshOutcome.Pending or ClaudeRefreshOutcome.TimedOut };
    }

    private async Task<UsageSnapshot> TransientFailureAsync(string message, CancellationToken cancellationToken)
    {
        var current = Kind == UsageProviderKind.Claude && _last?.Windows.Count > 0
            ? await ReadClaudeSafelyAsync(cancellationToken).ConfigureAwait(false) : null;
        return Failure(message, current);
    }

    private UsageSnapshot Failure(string message, UsageCredential? transientCredential = null)
    {
        _failures = Math.Min(_failures + 1, 6);
        var floor = Kind == UsageProviderKind.Claude ? 120 : 30;
        var now = _clock.UtcNow;
        _nextAttempt = now.AddSeconds(Math.Min(1800, floor * Math.Pow(2, _failures - 1)));
        if (Kind == UsageProviderKind.Claude) PruneClaudeQuota(now, CacheFingerprint(transientCredential, now));
        return Save(UsageStatus.Unavailable, message, retryAfter: _nextAttempt);
    }

    private UsageSnapshot SaveRateLimited()
    {
        var previous = Kind != UsageProviderKind.Claude && _last?.Windows.Count > 0 ? " · 이전 값 표시" : "";
        var retryTime = _nextAttempt.ToLocalTime();
        var timeLabel = retryTime.Date == _clock.UtcNow.ToLocalTime().Date ? retryTime.ToString("HH:mm") : retryTime.ToString("MM/dd HH:mm");
        return Save(UsageStatus.RateLimited, $"조회 일시 제한{previous} · {timeLabel} 이후 재시도", retryAfter: _nextAttempt);
    }

    private UsageSnapshot RecordRateLimit(DateTimeOffset receivedAt, DateTimeOffset? serverRetry)
    {
        _rateLimitFailures = Math.Min(_rateLimitFailures + 1, 6);
        var minutes = Kind == UsageProviderKind.Claude
            ? new[] { 5, 10, 20, 30, 60, 60 }[_rateLimitFailures - 1]
            : Math.Min(60, Math.Pow(2, _rateLimitFailures - 1));
        _nextAttempt = receivedAt.AddMinutes(minutes);
        if (serverRetry > _nextAttempt) _nextAttempt = serverRetry.Value;
        _claudeCooldownStore?.Save(_nextAttempt, _rateLimitFailures);
        return SaveRateLimited();
    }

    private UsageSnapshot Save(UsageStatus status, string message, DateTimeOffset? retryAfter = null)
    {
        var clear = Kind == UsageProviderKind.Claude && status is UsageStatus.AuthenticationRequired or UsageStatus.NotConnected or UsageStatus.AuthenticationRetry or UsageStatus.AuthenticationRecovering;
        if (Kind != UsageProviderKind.Claude && status != UsageStatus.RateLimited && _last?.Windows.Count > 0) message = "이전 조회값 표시 중 · " + message;
        _last = new(Kind, status, clear ? [] : _last?.Windows ?? [], _clock.UtcNow, _last?.Plan, message, retryAfter,
            ObservedAt: clear ? null : _last?.ObservedAt,
            NextQueryAt: _nextAttempt > _clock.UtcNow && _nextAttempt != DateTimeOffset.MaxValue ? _nextAttempt : null,
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
        if (!Object(root, key, out var window) || !Number(window, "utilization", out var used) || used is < 0 or > 100) return;
        windows.Add(new(label, used, Reset(window, "resets_at"), duration));
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

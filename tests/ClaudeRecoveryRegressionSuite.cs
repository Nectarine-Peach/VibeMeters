using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using VibeMeters.Models;
using VibeMeters.Services;

internal sealed class FakeClaudeClock : IClaudeRecoveryClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    public int Delays { get; private set; }
    public Action<int>? OnDelay { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
    public Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); UtcNow += delay; Delays++; OnDelay?.Invoke(Delays);
        token.ThrowIfCancellationRequested(); return Task.CompletedTask;
    }
    public async Task<T> WaitAsync<T>(Task<T> pending, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!pending.IsCompleted) { UtcNow += timeout; OnWait?.Invoke(timeout); }
        token.ThrowIfCancellationRequested();
        if (!pending.IsCompleted) throw new TimeoutException();
        return await pending;
    }
}

internal static class ClaudeRecoveryRegressionSuite
{
    internal static async Task<(int Passed, int Failed)> RunAsync()
    {
        var passed = 0; var failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine("PASS Claude recovery " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL Claude recovery " + name + ": " + ex.GetBaseException().Message); }
        }
        void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        UsageCredential Expired(FakeClaudeClock c) => new() { AccessToken = "old-fixture", ExpiresAt = c.UtcNow.AddHours(-4) };
        UsageCredential Fresh(FakeClaudeClock c) => new() { AccessToken = "fresh-fixture", ExpiresAt = c.UtcNow.AddHours(8) };
        var readyBody = """{"five_hour":{"utilization":17}}""";

        await Check("a scheduled lookup renews a near-expiry token without interrupting usage display", async () =>
        {
            var clock = new FakeClaudeClock();
            var credentials = new MutableCredentials(new UsageCredential { AccessToken = "old-fixture", ExpiresAt = clock.UtcNow.AddMinutes(4) });
            var refresh = new StubClaudeRefresher(_ =>
            {
                credentials.Current = Fresh(clock);
                return Task.FromResult(ClaudeRefreshOutcome.Completed);
            });
            var tokens = new List<string?>();
            var handler = new StubHandler(request => { tokens.Add(request.Headers.Authorization?.Parameter); return Json(readyBody); });
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresh, clock);
            var progress = new List<UsageStatus>();
            provider.AuthenticationProgress += snapshot => progress.Add(snapshot.Status);

            Require((await provider.FetchAsync()).Status == UsageStatus.Ready && refresh.Calls == 0);
            clock.UtcNow += TimeSpan.FromSeconds(150);
            var renewed = await provider.FetchAsync();

            Require(renewed.Status == UsageStatus.Ready && renewed.Windows.Count == 1);
            Require(refresh.Calls == 1 && tokens.SequenceEqual(new[] { "old-fixture", "fresh-fixture" }));
            Require(progress.Count == 0);
        });
        await Check("inconclusive early renewal keeps the valid token and permits recovery after expiry", async () =>
        {
            var clock = new FakeClaudeClock();
            var expiry = clock.UtcNow.AddSeconds(90);
            var credentials = new MutableCredentials(new UsageCredential { AccessToken = "old-fixture", ExpiresAt = expiry });
            var refresh = new StubClaudeRefresher(_ =>
            {
                if (clock.UtcNow >= expiry) credentials.Current = Fresh(clock);
                return Task.FromResult(ClaudeRefreshOutcome.Completed);
            });
            var tokens = new List<string?>();
            var handler = new StubHandler(request => { tokens.Add(request.Headers.Authorization?.Parameter); return Json(readyBody); });
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresh, clock);
            var progress = new List<UsageStatus>();
            provider.AuthenticationProgress += snapshot => progress.Add(snapshot.Status);

            Require((await provider.FetchAsync()).Status == UsageStatus.Ready);
            clock.UtcNow += TimeSpan.FromSeconds(1);
            var manual = await provider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow, RecoverAuthentication: true));
            Require(manual.Status == UsageStatus.Ready && manual.Windows.Count == 1);
            Require(refresh.Calls == 1 && clock.Delays == 0 && progress.Count == 0);
            Require(tokens.SequenceEqual(new[] { "old-fixture", "old-fixture" }));

            clock.UtcNow = expiry.AddSeconds(1);
            var recovered = await provider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow));
            Require(recovered.Status == UsageStatus.Ready && refresh.Calls == 2 && tokens.Last() == "fresh-fixture");
        });
        await Check("near expiry cannot trigger renewal or usage while a server cooldown is active", async () =>
        {
            var clock = new FakeClaudeClock();
            var credentials = new MutableCredentials(new UsageCredential { AccessToken = "old-fixture", ExpiresAt = clock.UtcNow.AddMinutes(3) });
            var refresh = new StubClaudeRefresher(_ =>
            {
                credentials.Current = Fresh(clock);
                return Task.FromResult(ClaudeRefreshOutcome.Completed);
            });
            var calls = 0;
            var handler = new StubHandler(request =>
            {
                if (++calls > 1)
                {
                    Require(request.Headers.Authorization?.Parameter == "fresh-fixture");
                    return Json(readyBody);
                }
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromMinutes(8));
                return response;
            });
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresh, clock);

            var limited = await provider.FetchAsync();
            Require(limited.Status == UsageStatus.RateLimited && limited.RetryAfter == clock.UtcNow.AddMinutes(8));
            clock.UtcNow += TimeSpan.FromMinutes(2);
            var duringCooldown = await provider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow, RecoverAuthentication: true));
            Require(duringCooldown.Status == UsageStatus.RateLimited && duringCooldown.RetryAfter == limited.RetryAfter);
            Require(refresh.Calls == 0 && handler.Calls == 1);

            clock.UtcNow = limited.RetryAfter!.Value;
            Require((await provider.FetchAsync()).Status == UsageStatus.Ready && refresh.Calls == 1 && handler.Calls == 2);
        });
        await Check("a saved 429 deadline survives a new provider and clears after a successful quota lookup", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "VibeMeters-claude-cooldown-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FakeClaudeClock();
                var credentials = new MutableCredentials(Fresh(clock));
                var calls = 0;
                var handler = new StubHandler(_ => ++calls == 1 ? RateLimitedFor(TimeSpan.FromMinutes(45)) : Json(readyBody));
                using var client = new HttpClient(handler);
                var provider = new UsageProvider(UsageProviderKind.Claude, credentials, client, clock: clock, claudeCooldownDirectory: directory);

                var limited = await provider.FetchAsync();
                Require(limited.Status == UsageStatus.RateLimited && limited.RetryAfter == clock.UtcNow.AddMinutes(45));
                var persisted = Directory.GetFiles(directory);
                Require(persisted.Length == 1);
                using (var document = JsonDocument.Parse(File.ReadAllText(persisted[0])))
                {
                    Require(document.RootElement.EnumerateObject().Count() == 2);
                    Require(document.RootElement.GetProperty("RetryAt").GetDateTimeOffset() == limited.RetryAfter);
                    Require(document.RootElement.GetProperty("ConsecutiveLimits").GetInt32() == 1);
                }

                var reopened = new UsageProvider(UsageProviderKind.Claude, credentials, client, clock: clock, claudeCooldownDirectory: directory);
                var manual = await reopened.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow, RecoverAuthentication: true));
                Require(manual.Status == UsageStatus.RateLimited && manual.RetryAfter == limited.RetryAfter && handler.Calls == 1);
                Require(manual.Windows.Count == 0 && manual.ObservedAt is null);

                clock.UtcNow = limited.RetryAfter!.Value;
                reopened.NotifyLoginCompleted();
                Require((await reopened.FetchAsync()).Status == UsageStatus.Ready && handler.Calls == 2);
                Require(Directory.GetFiles(directory).Length == 0);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
            }
        });
        await Check("a proactive renewal is reported as successful only when a later lookup succeeds after 429", async () =>
        {
            var clock = new FakeClaudeClock();
            var credentials = new MutableCredentials(new UsageCredential { AccessToken = "old-fixture", ExpiresAt = clock.UtcNow.AddSeconds(90) });
            var refresh = new StubClaudeRefresher(_ =>
            {
                credentials.Current = Fresh(clock);
                return Task.FromResult(ClaudeRefreshOutcome.Completed);
            });
            var diagnostics = new RecordingClaudeDiagnostics();
            var calls = 0;
            var handler = new StubHandler(request =>
            {
                Require(request.Headers.Authorization?.Parameter == "fresh-fixture");
                return ++calls == 1 ? RateLimitedFor(TimeSpan.FromMinutes(8)) : Json(readyBody);
            });
            using var client = new HttpClient(handler);
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, client, refresh, clock, diagnostics);

            var limited = await provider.FetchAsync();
            Require(limited.Status == UsageStatus.RateLimited && refresh.Calls == 1);
            Require(diagnostics.Events.Any(entry => entry.Name == ClaudeAuthEvent.ProactiveRenewalStarted));
            Require(!diagnostics.Events.Any(entry => entry.Name == ClaudeAuthEvent.RecoverySucceeded));

            clock.UtcNow = limited.RetryAfter!.Value;
            var recovered = await provider.FetchAsync();
            Require(recovered.Status == UsageStatus.Ready && handler.Calls == 2 && refresh.Calls == 1);
            Require(diagnostics.Events.Single(entry => entry.Name == ClaudeAuthEvent.RecoverySucceeded)
                is { CredentialChanged: true, FutureExpiry: true });
        });
        await Check("canceling the credential recheck after 429 cannot discard the server deadline", async () =>
        {
            var clock = new FakeClaudeClock();
            var credential = Fresh(clock);
            var throttleReceived = false;
            var canceledCredentialRecheck = false;
            using var cancel = new CancellationTokenSource();
            var credentials = new FixtureCredentials(token =>
            {
                if (throttleReceived && token == cancel.Token)
                {
                    canceledCredentialRecheck = true;
                    cancel.Cancel();
                    return Task.FromCanceled<UsageCredential?>(token);
                }
                return Task.FromResult<UsageCredential?>(credential);
            });
            var calls = 0;
            var handler = new StubHandler(_ =>
            {
                if (++calls == 1) return Json(readyBody);
                throttleReceived = true;
                return RateLimitedFor(TimeSpan.FromMinutes(45));
            });
            using var client = new HttpClient(handler);
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, client, clock: clock);

            Require((await provider.FetchAsync()).Status == UsageStatus.Ready);
            clock.UtcNow += TimeSpan.FromMinutes(2);
            try
            {
                await provider.FetchAsync(cancel.Token);
                throw new Exception("Credential recheck cancellation did not propagate");
            }
            catch (OperationCanceledException) when (canceledCredentialRecheck) { }

            var manual = await provider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow, RecoverAuthentication: true));
            Require(manual.Status == UsageStatus.RateLimited && manual.RetryAfter == clock.UtcNow.AddMinutes(45) && handler.Calls == 2);
        });
        await Check("429 keeps recent Claude values only until each quota resets or the observation is 30 minutes old", async () =>
        {
            var clock = new FakeClaudeClock();
            var start = clock.UtcNow;
            var credentials = new MutableCredentials(Fresh(clock));
            var body = JsonSerializer.Serialize(new
            {
                five_hour = new { utilization = 17, resets_at = start.AddMinutes(10) },
                seven_day = new { utilization = 25, resets_at = start.AddHours(1) }
            });
            var calls = 0;
            var handler = new StubHandler(_ => ++calls == 1 ? Json(body) : RateLimitedFor(TimeSpan.FromMinutes(45)));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), clock: clock);

            var ready = await provider.FetchAsync();
            Require(ready.Status == UsageStatus.Ready && ready.Windows.Count == 2 && ready.ObservedAt == start);
            clock.UtcNow = start.AddMinutes(2);
            var limited = await provider.FetchAsync();
            Require(limited.Status == UsageStatus.RateLimited && limited.ObservedAt == start && limited.Windows.SequenceEqual(ready.Windows));
            Require(limited.RetryAfter == start.AddMinutes(47));

            clock.UtcNow = start.AddMinutes(10);
            var afterReset = await provider.FetchAsync();
            Require(afterReset.Status == UsageStatus.RateLimited && afterReset.Windows.SequenceEqual(ready.Windows.Skip(1)));
            Require(afterReset.ObservedAt == start && afterReset.RetryAfter == limited.RetryAfter);
            clock.UtcNow = start.AddMinutes(30).AddTicks(-1);
            Require((await provider.FetchAsync()).Windows.Count == 1);

            clock.UtcNow = start.AddMinutes(30);
            var tooOld = await provider.FetchAsync();
            Require(tooOld.Status == UsageStatus.RateLimited && tooOld.Windows.Count == 0 && tooOld.ObservedAt is null);
            Require(tooOld.RetryAfter == limited.RetryAfter && tooOld.NextQueryAt == limited.RetryAfter && handler.Calls == 2);
        });
        foreach (var removeCredential in new[] { false, true })
            await Check($"429 preserves its deadline when Claude credentials are {(removeCredential ? "removed" : "replaced")}, while dropping old values", async () =>
            {
                var clock = new FakeClaudeClock();
                var credentials = new MutableCredentials(Fresh(clock));
                var calls = 0;
                var handler = new StubHandler(_ => ++calls == 1 ? Json(readyBody) : RateLimitedFor(TimeSpan.FromMinutes(45)));
                var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), clock: clock);

                Require((await provider.FetchAsync()).Status == UsageStatus.Ready);
                clock.UtcNow += TimeSpan.FromMinutes(2);
                var limited = await provider.FetchAsync();
                Require(limited.Status == UsageStatus.RateLimited && limited.Windows.Count == 1 && limited.ObservedAt is not null);
                clock.UtcNow += TimeSpan.FromMinutes(1);
                credentials.Current = removeCredential ? null : new UsageCredential { AccessToken = "another-account", ExpiresAt = clock.UtcNow.AddHours(1) };
                var switched = await provider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow, RecoverAuthentication: true));

                Require(switched.Status == UsageStatus.RateLimited && switched.Windows.Count == 0 && switched.ObservedAt is null);
                Require(switched.RetryAfter == limited.RetryAfter && switched.NextQueryAt == limited.RetryAfter && handler.Calls == 2);
            });
        await Check("a network failure keeps recent Claude values during backoff only within the 30-minute age limit", async () =>
        {
            var clock = new FakeClaudeClock();
            var start = clock.UtcNow;
            var calls = 0;
            var handler = new StubHandler(_ => ++calls == 1 ? Json(readyBody) : throw new HttpRequestException("offline"));
            var provider = new UsageProvider(UsageProviderKind.Claude, new MutableCredentials(Fresh(clock)), new HttpClient(handler), clock: clock);

            var ready = await provider.FetchAsync();
            Require(ready.Status == UsageStatus.Ready && ready.ObservedAt == start);
            clock.UtcNow = start.AddMinutes(29);
            var offline = await provider.FetchAsync();
            Require(offline.Status == UsageStatus.Unavailable && offline.Windows.SequenceEqual(ready.Windows) && offline.ObservedAt == start);
            Require(offline.NextQueryAt > start.AddMinutes(30));

            clock.UtcNow = start.AddMinutes(30);
            var tooOld = await provider.FetchAsync();
            Require(tooOld.Status == UsageStatus.Unavailable && tooOld.Windows.Count == 0 && tooOld.ObservedAt is null);
            Require(tooOld.NextQueryAt == offline.NextQueryAt && handler.Calls == 2);
        });
        await Check("twelve-hour lock, delayed credential save and visible recovery complete without login", async () =>
        {
            var clock = new FakeClaudeClock();
            var credentials = new MutableCredentials(new UsageCredential { AccessToken = "old-fixture", ExpiresAt = clock.UtcNow.AddHours(8) });
            var refresh = new StubClaudeRefresher(_ => Task.FromResult(ClaudeRefreshOutcome.Completed));
            var handler = new StubHandler(_ => Json(readyBody));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresh, clock);
            Require((await provider.FetchAsync()).Status == UsageStatus.Ready);
            clock.UtcNow += TimeSpan.FromHours(12);
            clock.OnDelay = count => { if (count == 3) credentials.Current = Fresh(clock); };
            var progress = new List<UsageStatus>(); provider.AuthenticationProgress += s => progress.Add(s.Status);
            var recovered = await provider.FetchAsync();
            Require(recovered.Status == UsageStatus.Ready && recovered.Windows.Count == 1 && refresh.Calls == 1 && handler.Calls == 2);
            Require(clock.Delays == 3 && progress.Contains(UsageStatus.AuthenticationRecovering));
        });
        await Check("status exit zero without credential change is bounded retry, never required login", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Expired(clock));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(new StubHandler(_ => throw new Exception("No usage"))),
                new StubClaudeRefresher(_ => Task.FromResult(ClaudeRefreshOutcome.Completed)), clock);
            var start = clock.UtcNow; var result = await provider.FetchAsync();
            if (result.Status != UsageStatus.AuthenticationRetry || result.Windows.Count != 0 || result.AuthenticationWorkPending)
                throw new Exception($"Unexpected recovery state {result.Status}, windows {result.Windows.Count}, pending {result.AuthenticationWorkPending}");
            if (clock.UtcNow - start != TimeSpan.FromSeconds(8) || clock.Delays != 16 || result.NextQueryAt != clock.UtcNow.AddSeconds(30))
                throw new Exception($"Unexpected timing: elapsed {clock.UtcNow-start}, polls {clock.Delays}, next {result.NextQueryAt:o}, expected {clock.UtcNow.AddSeconds(30):o}");
        });
        await Check("an outdated or broken CLI cannot shadow the first supported installation", async () =>
        {
            var clock = new FakeClaudeClock();
            var status = Task.FromResult<ClaudeCliResult?>(new(0));
            var broken = new FixtureRunner(_ => throw new System.ComponentModel.Win32Exception(), status);
            var outdated = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.316 (Claude Code)")), status);
            var supported = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.319 (Claude Code)")), status);
            var later = new FixtureRunner(_ => throw new Exception("Later installations must not be probed"), status);
            Func<IEnumerable<IClaudeCommandRunner>> candidates = () => [broken, outdated, supported, later];
            var constructor = typeof(ClaudeCredentialRefresher).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, [typeof(string), typeof(Func<IEnumerable<IClaudeCommandRunner>>), typeof(IClaudeRecoveryClock), typeof(IClaudeAuthDiagnostics)],
                modifiers: null) ?? throw new Exception("Candidate resolver constructor missing");
            var directory = Path.Combine(Path.GetTempPath(), "VibeMeters-cli-candidates-" + Guid.NewGuid().ToString("N"));
            var refresher = (ClaudeCredentialRefresher)constructor.Invoke([directory, candidates, clock, null]);

            Require(await refresher.RefreshAsync() == ClaudeRefreshOutcome.Completed);
            Require(broken.Commands.SequenceEqual(new[] { "--version" }));
            Require(outdated.Commands.SequenceEqual(new[] { "--version" }));
            Require(supported.Commands.SequenceEqual(new[] { "--version", "auth status" }));
            Require(later.Calls == 0);
        });
        await Check("shared CLI timeout never starts a second refresh command", async () =>
        {
            var clock = new FakeClaudeClock(); var status = new TaskCompletionSource<ClaudeCliResult?>();
            var runner = new FixtureRunner(args => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.319 (Claude Code)")), status.Task);
            var directory = Path.Combine(Path.GetTempPath(), "VibeMeters-clock-" + Guid.NewGuid().ToString("N"));
            var first = new ClaudeCredentialRefresher(directory, runner, clock);
            Require(await first.RefreshAsync() == ClaudeRefreshOutcome.TimedOut);
            var second = new ClaudeCredentialRefresher(directory, runner, clock);
            Require(await second.RefreshAsync() == ClaudeRefreshOutcome.Pending && runner.Calls == 2);
            status.SetResult(new(0)); await Task.Yield();
        });
        await Check("file write after CLI timeout still recovers within twenty-second total budget", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Expired(clock));
            var status = new TaskCompletionSource<ClaudeCliResult?>();
            var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.319 (Claude Code)")), status.Task);
            var refresher = new ClaudeCredentialRefresher(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), runner, clock);
            clock.OnDelay = count => { if (count == 4) { credentials.Current = Fresh(clock); status.TrySetResult(new(0)); } };
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(new StubHandler(_ => Json(readyBody))), refresher, clock);
            var start = clock.UtcNow; var result = await provider.FetchAsync();
            Require(result.Status == UsageStatus.Ready && clock.UtcNow - start == TimeSpan.FromSeconds(14));
        });
        await Check("exit code one and expired-access text are inconclusive; only complete OAuth invalid_grant is decisive", () =>
        {
            Require(!ClaudeCredentialRefresher.IsExplicitRejection("OAuth token expired"));
            Require(!ClaudeCredentialRefresher.IsExplicitRejection("log mentions invalid_grant"));
            Require(!ClaudeCredentialRefresher.IsExplicitRejection("""{"message":"invalid_grant"}"""));
            Require(ClaudeCredentialRefresher.IsExplicitRejection("""{"error":"invalid_grant"}"""));
            Require(new ClaudeCliResult(1, "fixture-secret", "fixture-email").ToString() == "[Claude CLI result]");
            return Task.CompletedTask;
        });
        await Check("explicit rejection is distinct from pending renewal", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Expired(clock));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(new StubHandler(_ => throw new Exception("No usage"))),
                new StubClaudeRefresher(_ => Task.FromResult(ClaudeRefreshOutcome.AuthenticationRejected)), clock);
            var result = await provider.FetchAsync();
            Require(result.Status == UsageStatus.AuthenticationRequired && !result.AuthenticationWorkPending && clock.Delays == 0);
        });
        foreach (var error in new[] { "temporary network failure", "{\"error\":\"invalid_grant\"}" })
            await Check($"CLI result classifies structured rejection separately from ordinary exit one {error.StartsWith('{')}", async () =>
            {
                var clock = new FakeClaudeClock();
                var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.319 (Claude Code)")), Task.FromResult<ClaudeCliResult?>(new(1, error: error)));
                var refresher = new ClaudeCredentialRefresher(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), runner, clock);
                Require(await refresher.RefreshAsync() == (error.StartsWith('{') ? ClaudeRefreshOutcome.AuthenticationRejected : ClaudeRefreshOutcome.Failed));
            });
        await Check("cancellation while sharing CLI does not terminate or restart its work", async () =>
        {
            var clock = new FakeClaudeClock(); var status = new TaskCompletionSource<ClaudeCliResult?>();
            var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.319 (Claude Code)")), status.Task);
            var refresher = new ClaudeCredentialRefresher(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), runner, clock);
            using var cancel = new CancellationTokenSource(); clock.OnWait = _ => cancel.Cancel();
            try { await refresher.RefreshAsync(cancel.Token); throw new Exception("Cancellation missing"); } catch (OperationCanceledException) { }
            Require(runner.Calls == 2 && !status.Task.IsCompleted); status.SetResult(new(0)); await Task.Yield();
        });
        await Check("account change during recovery probes current file, never overwrites credentials", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Expired(clock));
            var replacement = Fresh(clock); clock.OnDelay = _ => credentials.Current = replacement;
            var handler = new StubHandler(request => { Require(request.Headers.Authorization?.Parameter == replacement.AccessToken); return Json(readyBody); });
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), new StubClaudeRefresher(_ => Task.FromResult(ClaudeRefreshOutcome.Failed)), clock);
            Require((await provider.FetchAsync()).Status == UsageStatus.Ready && ReferenceEquals(replacement, credentials.Current));
        });
        await Check("account change during usage discards old account response", async () =>
        {
            var clock = new FakeClaudeClock(); var original = Fresh(clock); var credentials = new MutableCredentials(original);
            var handler = new StubHandler(_ => { credentials.Current = new() { AccessToken = "another-account", ExpiresAt = clock.UtcNow.AddHours(8) }; return Json(readyBody); });
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), clock: clock);
            var result = await provider.FetchAsync(); Require(result.Status == UsageStatus.AuthenticationRetry && result.Windows.Count == 0 && result.ObservedAt is null);
        });
        await Check("login preflight bypasses only auth retry delay, probes usage and opens zero browsers when recovered", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Expired(clock)); var refreshes = 0; var browsers = 0;
            var refresher = new StubClaudeRefresher(_ => { if (++refreshes == 2) credentials.Current = Fresh(clock); return Task.FromResult(ClaudeRefreshOutcome.Completed); });
            var handler = new StubHandler(_ => Json(readyBody));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresher, clock);
            Require((await provider.FetchAsync()).Status == UsageStatus.AuthenticationRetry);
            var flow = await ClaudeLoginFlow.RunAsync(token => provider.FetchAsync(token, new(true, clock.UtcNow, true)),
                _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
            Require(!flow.LoginStarted && flow.Result.Success && browsers == 0 && handler.Calls == 1 && refreshes == 2);
        });
        await Check("login preflight probes valid credentials despite previously required-login backoff", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Fresh(clock)); var browsers = 0; var calls = 0;
            var refresher = new StubClaudeRefresher(_ => Task.FromResult(ClaudeRefreshOutcome.AuthenticationRejected));
            var handler = new StubHandler(_ => ++calls == 1 ? new(HttpStatusCode.Unauthorized) : Json(readyBody));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresher, clock);
            Require((await provider.FetchAsync()).Status == UsageStatus.AuthenticationRequired);
            var flow = await ClaudeLoginFlow.RunAsync(token => provider.FetchAsync(token, new(true, clock.UtcNow, true)),
                _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
            Require(!flow.LoginStarted && browsers == 0 && calls == 2 && refresher.Calls == 1 && flow.Snapshot.Status == UsageStatus.Ready);
        });
        await Check("preflight preserves rate limit and suppresses browser login", async () =>
        {
            var clock = new FakeClaudeClock(); var credentials = new MutableCredentials(Fresh(clock)); var browsers = 0;
            var handler = new StubHandler(_ => new(HttpStatusCode.TooManyRequests));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), clock: clock);
            var first = await provider.FetchAsync();
            var flow = await ClaudeLoginFlow.RunAsync(token => provider.FetchAsync(token, new(true, clock.UtcNow, true)),
                _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
            Require(!flow.LoginStarted && browsers == 0 && handler.Calls == 1 && flow.Snapshot.RetryAfter == first.RetryAfter);
        });
        foreach (var status in new[] { UsageStatus.AuthenticationRecovering, UsageStatus.AuthenticationRetry })
            await Check($"known pending {status} never creates a competing login process", async () =>
            {
                var browsers = 0;
                var flow = await ClaudeLoginFlow.RunAsync(_ => Task.FromResult(new UsageSnapshot(UsageProviderKind.Claude, status, [], DateTimeOffset.UtcNow, AuthenticationWorkPending: true)),
                    _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
                Require(!flow.LoginStarted && browsers == 0);
            });
        await Check("completed but inconclusive preflight permits one explicitly requested browser login", async () =>
        {
            var browsers = 0;
            var flow = await ClaudeLoginFlow.RunAsync(_ => Task.FromResult(new UsageSnapshot(UsageProviderKind.Claude, UsageStatus.AuthenticationRetry, [], DateTimeOffset.UtcNow)),
                _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
            Require(flow.LoginStarted && browsers == 1);
        });
        await Check("preflight cancellation opens no browser", async () =>
        {
            var browsers = 0; using var cancel = new CancellationTokenSource();
            try
            {
                await ClaudeLoginFlow.RunAsync(_ => { cancel.Cancel(); return Task.FromResult(new UsageSnapshot(UsageProviderKind.Claude, UsageStatus.AuthenticationRetry, [], DateTimeOffset.UtcNow)); },
                    _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); }, cancel.Token);
                throw new Exception("Cancellation missing");
            }
            catch (OperationCanceledException) { }
            Require(browsers == 0);
        });
        await Check("closed diagnostic schema rotates, expires and cannot contain raw CLI output", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "VibeMeters-auth-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                var log = new ClaudeAuthDiagnostics(directory);
                for (var index = 0; index < 800; index++) log.Write(ClaudeAuthEvent.FileObserved, ClaudeRefreshOutcome.Pending, 1, false, false);
                var files = Directory.GetFiles(directory); Require(files.Length == 2 && files.All(file => new FileInfo(file).Length < 66 * 1024));
                foreach (var file in files)
                    foreach (var line in File.ReadLines(file)) { using var json = JsonDocument.Parse(line); Require(json.RootElement.EnumerateObject().Count() == 6); }
                foreach (var file in files) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
                log.Write(ClaudeAuthEvent.PreflightReady); Require(Directory.GetFiles(directory).Length == 1);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
            }
            return Task.CompletedTask;
        });
        return (passed, failed);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage RateLimitedFor(TimeSpan delay)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(delay);
        return response;
    }
    private sealed class FixtureCredentials(Func<CancellationToken, Task<UsageCredential?>> read) : ICredentialReader
    {
        public Task<UsageCredential?> ReadAsync(UsageProviderKind provider, CancellationToken cancellationToken = default) => read(cancellationToken);
    }

    private sealed class RecordingClaudeDiagnostics : IClaudeAuthDiagnostics
    {
        public List<(ClaudeAuthEvent Name, bool? CredentialChanged, bool? FutureExpiry)> Events { get; } = new();
        public void Write(ClaudeAuthEvent name, ClaudeRefreshOutcome? outcome = null, int? exitCode = null, bool? credentialChanged = null, bool? futureExpiry = null)
            => Events.Add((name, credentialChanged, futureExpiry));
    }

    private sealed class FixtureRunner(Func<string[], Task<ClaudeCliResult?>> version, Task<ClaudeCliResult?> status) : IClaudeCommandRunner
    {
        public List<string> Commands { get; } = new();
        public int Calls => Commands.Count;
        public Task<ClaudeCliResult?> RunAsync(string[] arguments)
        {
            Commands.Add(string.Join(' ', arguments));
            return arguments[0] == "--version" ? version(arguments) : status;
        }
    }
}

using System.Net;
using System.Net.Http;
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
        await Check("shared CLI timeout never starts a second refresh command", async () =>
        {
            var clock = new FakeClaudeClock(); var status = new TaskCompletionSource<ClaudeCliResult?>();
            var runner = new FixtureRunner(args => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.310 (Claude Code)")), status.Task);
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
            var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.310 (Claude Code)")), status.Task);
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
                var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.310 (Claude Code)")), Task.FromResult<ClaudeCliResult?>(new(1, error: error)));
                var refresher = new ClaudeCredentialRefresher(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), runner, clock);
                Require(await refresher.RefreshAsync() == (error.StartsWith('{') ? ClaudeRefreshOutcome.AuthenticationRejected : ClaudeRefreshOutcome.Failed));
            });
        await Check("cancellation while sharing CLI does not terminate or restart its work", async () =>
        {
            var clock = new FakeClaudeClock(); var status = new TaskCompletionSource<ClaudeCliResult?>();
            var runner = new FixtureRunner(_ => Task.FromResult<ClaudeCliResult?>(new(0, "2.1.310 (Claude Code)")), status.Task);
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
            var refresher = new StubClaudeRefresher(_ => { credentials.Current = new() { AccessToken = "replacement-fixture", ExpiresAt = clock.UtcNow.AddHours(8) }; return Task.FromResult(ClaudeRefreshOutcome.Completed); });
            var handler = new StubHandler(_ => ++calls <= 2 ? new(HttpStatusCode.Unauthorized) : Json(readyBody));
            var provider = new UsageProvider(UsageProviderKind.Claude, credentials, new HttpClient(handler), refresher, clock);
            Require((await provider.FetchAsync()).Status == UsageStatus.AuthenticationRequired);
            var flow = await ClaudeLoginFlow.RunAsync(token => provider.FetchAsync(token, new(true, clock.UtcNow, true)),
                _ => { browsers++; return Task.FromResult(new LoginResult(true, "fixture")); });
            Require(!flow.LoginStarted && browsers == 0 && calls == 3 && flow.Snapshot.Status == UsageStatus.Ready);
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
    private sealed class FixtureRunner(Func<string[], Task<ClaudeCliResult?>> version, Task<ClaudeCliResult?> status) : IClaudeCommandRunner
    {
        public int Calls { get; private set; }
        public Task<ClaudeCliResult?> RunAsync(string[] arguments) { Calls++; return arguments[0] == "--version" ? version(arguments) : status; }
    }
}

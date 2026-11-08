using System.Net;
using System.Text.Json;
using VibeMeters.Models;
using VibeMeters.Services;

internal static class CodexAppServerRegressionSuite
{
    private const string Initialized = """{"id":1,"result":{"userAgent":"fixture"}}""";
    private const string ChatGpt = """{"id":2,"result":{"account":{"type":"chatgpt","planType":"plus"}}}""";

    internal static async Task<(int Passed, int Failed)> RunAsync()
    {
        var passed = 0;
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); passed++; Console.WriteLine("PASS Codex app-server " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL Codex app-server " + name + ": " + ex.GetBaseException().Message); }
        }

        await Check("uses the official read-only sequence and prefers the Codex bucket", async () =>
        {
            var (snapshot, requests) = await RunFixture(true, "fixture-workspace", Initialized,
                """{"method":"account/updated","params":{"authMode":"chatgpt"}}""", ChatGpt,
                """
                {"id":3,"result":{"accountId":"fixture-workspace","rateLimits":{"primary":{"usedPercent":98}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1792756800},"secondary":{"usedPercent":41,"windowDurationMins":10080}}}}}
                """);
            Require(snapshot is { Status: UsageStatus.Ready, Plan: "plus" } && snapshot.Windows.Count == 2);
            Require(snapshot!.Windows[0] is { Name: "5시간", UsedPercent: 25 } &&
                snapshot.Windows[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792756800));
            Require(snapshot.Windows[1] is { Name: "주간", UsedPercent: 41 });
            Require(requests.Select(r => r.GetProperty("method").GetString())
                .SequenceEqual(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }));
            Require(requests.All(r => !r.TryGetProperty("jsonrpc", out _)));
            Require(requests[2].GetProperty("params").GetProperty("refreshToken").GetBoolean());
            Require(requests[3].GetProperty("params").GetProperty("excludeResetCreditDetails").GetBoolean());
        });

        await Check("retains Business individual-limit percentages from an older bucket shape", async () =>
        {
            var (snapshot, requests) = await RunFixture(false, null, Initialized,
                """{"id":2,"result":{"account":{"type":"chatgpt","planType":"business"}}}""",
                """
                {"id":3,"result":{"rateLimits":{"primary":null,"secondary":null,"credits":{"hasCredits":true,"unlimited":false},"individualLimit":{"limit":"100","used":"52","remainingPercent":48,"resetsAt":1792756800}}}}
                """);
            Require(snapshot is { Status: UsageStatus.Ready, Plan: "business" } && snapshot.Windows.Count == 1);
            Require(snapshot!.Windows[0] is { Name: "개인 한도", UsedPercent: 52, RemainingPercent: 48 } &&
                snapshot.Windows[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792756800));
            Require(!requests[2].GetProperty("params").GetProperty("refreshToken").GetBoolean());
        });

        await Check("discards quota for a different workspace", async () =>
        {
            var (snapshot, _) = await RunFixture(false, "expected-workspace", Initialized, ChatGpt,
                """{"id":3,"result":{"accountId":"other-workspace","rateLimits":{"primary":{"usedPercent":25}}}}""");
            Require(snapshot is { Status: UsageStatus.Unavailable } && snapshot.Windows.Count == 0);
            Require(!snapshot!.Message!.Contains("other-workspace", StringComparison.Ordinal));
        });

        await Check("does not request quota for an API-key account or a signed-out account", async () =>
        {
            foreach (var (account, status) in new[]
            {
                ("""{"type":"apiKey"}""", UsageStatus.Unavailable),
                ("null", UsageStatus.NotConnected)
            })
            {
                var (snapshot, requests) = await RunFixture(false, null, Initialized,
                    "{\"id\":2,\"result\":{\"account\":" + account + "}}");
                Require(snapshot?.Status == status && requests.Count == 3);
            }
        });

        await Check("distinguishes upstream throttling from authentication and other failures", async () =>
        {
            foreach (var (error, status) in new[]
            {
                ("failed to fetch codex rate limits: HTTP status 429 Too Many Requests", UsageStatus.RateLimited),
                ("failed to fetch codex rate limits: HTTP status 401 Unauthorized", UsageStatus.AuthenticationRequired),
                ("failed to fetch codex rate limits: upstream connection closed", UsageStatus.Unavailable)
            })
            {
                var (snapshot, _) = await RunFixture(false, null, Initialized, ChatGpt,
                    JsonSerializer.Serialize(new { id = 3, error = new { code = -32603, message = error } }));
                Require(snapshot?.Status == status && snapshot.Message?.Contains(error, StringComparison.Ordinal) == false);
            }
        });

        await Check("never responds to a server request for externally managed credentials", async () =>
        {
            using var input = new StringWriter();
            using var output = new StringReader(Initialized + "\n" +
                """{"id":99,"method":"account/chatgptAuthTokens/refresh","params":{}}""");
            try { await CodexAppServerUsage.ReadUsageAsync(input, output, true, null, CancellationToken.None); }
            catch (IOException)
            {
                Require(!input.ToString().Contains("chatgptAuthTokens", StringComparison.Ordinal));
                return;
            }
            throw new InvalidOperationException("Unexpected auth request did not abort the fixture.");
        });

        await Check("recovers through the CLI when the local credential file is absent", async () =>
        {
            var clock = new FakeClaudeClock();
            var cli = new StubAppServer(Ready(clock.UtcNow));
            var http = new StubHandler(_ => throw new InvalidOperationException("HTTP used without a credential."));
            var provider = new UsageProvider(UsageProviderKind.Codex, new MutableCredentials(null), cli, new HttpClient(http), clock: clock);
            var snapshot = await provider.FetchAsync();
            Require(snapshot is { Status: UsageStatus.Ready } && snapshot.Windows.Count == 1);
            Require(snapshot.ObservedAt == clock.UtcNow && cli.Calls.SequenceEqual(new[] { (false, (string?)null) }) && http.Calls == 0);
        });

        await Check("delegates a single HTTP 401 recovery without bypassing HTTP 429 cooldown", async () =>
        {
            var clock = new FakeClaudeClock();
            var credentials = new MutableCredentials(new() { AccessToken = "fixture-token", AccountId = "fixture-workspace" });
            var cli = new StubAppServer(Ready(clock.UtcNow));
            var unauthorized = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var provider = new UsageProvider(UsageProviderKind.Codex, credentials, cli, new HttpClient(unauthorized), clock: clock);
            var recovered = await provider.FetchAsync();
            Require(recovered.Status == UsageStatus.Ready && unauthorized.Calls == 1);
            Require(cli.Calls.SequenceEqual(new[] { (true, (string?)"fixture-workspace") }));

            var throttledCli = new StubAppServer(Ready(clock.UtcNow));
            var throttledHttp = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new(TimeSpan.FromMinutes(7));
                return response;
            });
            var limitedProvider = new UsageProvider(UsageProviderKind.Codex, credentials, throttledCli, new HttpClient(throttledHttp), clock: clock);
            var throttled = await limitedProvider.FetchAsync();
            clock.UtcNow += TimeSpan.FromMinutes(1);
            var manual = await limitedProvider.FetchAsync(options: new(BypassSuccessCache: true, RequestedAt: clock.UtcNow));
            Require(throttled.Status == UsageStatus.RateLimited && manual.Status == UsageStatus.RateLimited);
            Require(throttled.RetryAfter == clock.UtcNow.AddMinutes(6) && manual.RetryAfter == throttled.RetryAfter);
            Require(throttledHttp.Calls == 1 && throttledCli.Calls.Count == 0);
        });

        return (passed, failed);
    }

    private static UsageSnapshot Ready(DateTimeOffset now) =>
        new(UsageProviderKind.Codex, UsageStatus.Ready, [new("5시간", 30, null)], now);

    private static async Task<(UsageSnapshot? Snapshot, IReadOnlyList<JsonElement> Requests)> RunFixture(
        bool refreshToken, string? expectedAccountId, params string[] responses)
    {
        using var input = new StringWriter();
        using var output = new StringReader(string.Join("\n", responses));
        var snapshot = await CodexAppServerUsage.ReadUsageAsync(input, output, refreshToken, expectedAccountId, CancellationToken.None);
        var requests = new List<JsonElement>();
        foreach (var line in input.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            requests.Add(document.RootElement.Clone());
        }
        return (snapshot, requests);
    }

    private sealed class StubAppServer(UsageSnapshot snapshot) : ICodexAppServerUsage
    {
        internal List<(bool RefreshToken, string? AccountId)> Calls { get; } = [];

        public Task<UsageSnapshot?> FetchAsync(bool refreshToken, string? expectedAccountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((refreshToken, expectedAccountId));
            return Task.FromResult<UsageSnapshot?>(snapshot);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed");
    }
}

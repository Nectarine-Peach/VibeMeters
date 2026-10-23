using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeMeters.Models;
using VibeMeters.Services;

var suite = new RegressionSuite();
return await suite.RunAsync();

sealed class RegressionSuite
{
    private int _passed;
    private int _failed;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");

    public async Task<int> RunAsync()
    {
        Test("Codex windows, plan, remaining and absolute reset", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":25.5,"limit_window_seconds":18000,"reset_at":1792756800},"secondary_window":{"used_percent":100,"limit_window_seconds":604800}}}""");
            Equal(UsageStatus.Ready, s.Status); Equal("plus", s.Plan); Equal(2, s.Windows.Count);
            Equal(74.5, s.Windows[0].RemainingPercent); Equal("5시간", s.Windows[0].Name);
            Equal(DateTimeOffset.FromUnixTimeSeconds(1792756800), s.Windows[0].ResetsAt);
            Equal(0d, s.Windows[1].RemainingPercent); Equal("주간", s.Windows[1].Name);
            Equal<DateTimeOffset?>(null, s.Windows[1].ResetsAt);
        });
        Test("Codex alternate rate_limits and relative reset", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"rate_limits":{"primary_window":{"used_percent":0,"reset_after_seconds":3600,"limit_window_seconds":3600}}}""");
            Equal(100d, s.Windows[0].RemainingPercent); Equal(Now.AddHours(1), s.Windows[0].ResetsAt); Equal("1시간", s.Windows[0].Name);
        });
        Test("Claude primary, weekly, model-specific windows", () =>
        {
            var s = Parse(UsageProviderKind.Claude, """{"five_hour":{"utilization":42,"resets_at":"2026-10-23T20:00:00+09:00"},"seven_day":{"utilization":5},"seven_day_sonnet":{"utilization":11}}""");
            Equal(3, s.Windows.Count); Equal(58d, s.Windows[0].RemainingPercent);
            Equal(DateTimeOffset.Parse("2026-10-23T20:00:00+09:00"), s.Windows[0].ResetsAt); Equal("sonnet 주간", s.Windows[2].Name);
            Equal(TimeSpan.FromDays(7), s.Windows[1].Duration);
        });
        foreach (var kind in Enum.GetValues<UsageProviderKind>())
        {
            foreach (var input in new[] { "{}", "null", "[]", "42", "\"text\"" })
                Test($"{kind} unsupported root {input}", () => Equal(UsageStatus.Unavailable, Parse(kind, input).Status));
            var property = kind == UsageProviderKind.Codex ? "\"rate_limit\":{\"primary_window\":{\"used_percent\":" : "\"five_hour\":{\"utilization\":";
            var suffix = kind == UsageProviderKind.Codex ? "}}}" : "}}";
            foreach (var invalid in new[] { "null", "\"25\"", "1e999", "true" })
                Test($"{kind} rejects invalid percentage {invalid}", () => Equal(0, Parse(kind, "{" + property + invalid + suffix).Windows.Count));
            Test($"{kind} clamps percentage boundaries", () =>
            {
                Equal(100d, Parse(kind, "{" + property + "-20" + suffix).Windows[0].RemainingPercent);
                Equal(0d, Parse(kind, "{" + property + "120" + suffix).Windows[0].RemainingPercent);
            });
        }
        Test("Codex invalid reset stays unknown", () => Equal<DateTimeOffset?>(null, Parse(UsageProviderKind.Codex, """{"rate_limit":{"primary_window":{"used_percent":1,"reset_at":9223372036854775807,"reset_after_seconds":-2}}}""").Windows[0].ResetsAt));
        Test("Claude invalid reset stays unknown", () => Equal<DateTimeOffset?>(null, Parse(UsageProviderKind.Claude, """{"five_hour":{"utilization":2,"resets_at":"invalid"}}""").Windows[0].ResetsAt));
        Test("Fallback plan retained", () => Equal("pro", Parse(UsageProviderKind.Claude, "{}", "pro").Plan));
        Test("Credential string redacts token", () => True(!Credential().ToString().Contains("test-secret", StringComparison.Ordinal)));

        await TestAsync("No credentials do not make network request", async () =>
        {
            var handler = new StubHandler(_ => throw new Exception("Must not request"));
            var s = await Provider(handler, null).FetchAsync(); Equal(UsageStatus.NotConnected, s.Status); Equal(0, handler.Calls);
        });
        await TestAsync("Expired credentials do not make network request", async () =>
        {
            var handler = new StubHandler(_ => throw new Exception("Must not request"));
            var s = await Provider(handler, new UsageCredential { AccessToken = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }).FetchAsync();
            Equal(UsageStatus.AuthenticationRequired, s.Status); Equal(0, handler.Calls);
        });
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
            await TestAsync($"HTTP {(int)status} status mapping", async () =>
            {
                var handler = new StubHandler(_ => new HttpResponseMessage(status));
                var s = await Provider(handler).FetchAsync();
                Equal(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? UsageStatus.AuthenticationRequired : status == HttpStatusCode.TooManyRequests ? UsageStatus.RateLimited : UsageStatus.Unavailable, s.Status);
                if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError) True(s.RetryAfter > DateTimeOffset.UtcNow);
            });
        await TestAsync("429 Retry-After suppresses immediate requests", async () =>
        {
            var handler = new StubHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)); return r; });
            var provider = Provider(handler); var a = await provider.FetchAsync(); var b = await provider.FetchAsync();
            Equal(1, handler.Calls); Equal(a, b); True(a.RetryAfter > DateTimeOffset.UtcNow.AddMinutes(4));
        });
        await TestAsync("Malformed response safely unavailable", async () =>
        {
            var handler = new StubHandler(_ => JsonResponse("<html>bad gateway</html>"));
            Equal(UsageStatus.Unavailable, (await Provider(handler).FetchAsync()).Status);
        });
        await TestAsync("HTTP exception safely unavailable", async () =>
        {
            var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
            Equal(UsageStatus.Unavailable, (await Provider(handler).FetchAsync()).Status);
        });
        await TestAsync("Timeout safely unavailable", async () =>
        {
            var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
            Equal(UsageStatus.Unavailable, (await Provider(handler).FetchAsync()).Status);
        });
        await TestAsync("Codex request uses expected endpoint and account header", async () =>
        {
            var handler = new StubHandler(r => { Equal("https://chatgpt.com/backend-api/wham/usage", r.RequestUri?.AbsoluteUri); Equal("Bearer", r.Headers.Authorization?.Scheme); Equal("test-secret", r.Headers.Authorization?.Parameter); Equal("test-account", r.Headers.GetValues("ChatGPT-Account-Id").Single()); return JsonResponse("""{"rate_limit":{"primary_window":{"used_percent":12}}}"""); });
            var p = Provider(handler); var a = await p.FetchAsync(); var b = await p.FetchAsync(); Equal(UsageStatus.Ready, a.Status); Equal(88d, a.Windows[0].RemainingPercent); Equal(a, b); Equal(1, handler.Calls);
        });
        await TestAsync("Claude request sends OAuth beta header", async () =>
        {
            var handler = new StubHandler(r => { Equal("https://api.anthropic.com/api/oauth/usage", r.RequestUri?.AbsoluteUri); Equal("oauth-2025-04-20", r.Headers.GetValues("anthropic-beta").Single()); return JsonResponse("""{"five_hour":{"utilization":12}}"""); });
            Equal(UsageStatus.Ready, (await Provider(handler, kind: UsageProviderKind.Claude).FetchAsync()).Status);
        });
        await TestAsync("Concurrent refreshes coalesce behind provider cache", async () =>
        {
            var handler = new StubHandler(_ => JsonResponse("""{"rate_limit":{"primary_window":{"used_percent":12}}}"""));
            var provider = Provider(handler);
            var snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.FetchAsync()));
            Equal(1, handler.Calls); True(snapshots.All(s => s.Status == UsageStatus.Ready));
        });
        await TestAsync("Caller cancellation propagates", async () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await Provider(new StubHandler(_ => JsonResponse("{}"))).FetchAsync(cts.Token); throw new Exception("Cancellation did not propagate"); }
            catch (OperationCanceledException) { }
        });
        await TestAsync("Credential files parsed from isolated fixture directory", CredentialFilesAsync);
        foreach (var kind in Enum.GetValues<UsageProviderKind>())
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
                await TestAsync($"{kind} new login bypasses cached HTTP {(int)status}", async () =>
                {
                    var credentials = new MutableCredentials(Credential());
                    var handler = new StubHandler(r => r.Headers.Authorization?.Parameter == "test-secret"
                        ? new HttpResponseMessage(status)
                        : JsonResponse(kind == UsageProviderKind.Codex ? """{"rate_limit":{"primary_window":{"used_percent":18}}}""" : """{"five_hour":{"utilization":18}}"""));
                    var provider = new UsageProvider(kind, credentials, new HttpClient(handler));
                    var failed = await provider.FetchAsync(); True(failed.Status != UsageStatus.Ready);
                    credentials.Current = new UsageCredential { AccessToken = "new-login" };
                    var recovered = await provider.FetchAsync();
                    Equal(UsageStatus.Ready, recovered.Status); Equal(82d, recovered.Windows[0].RemainingPercent); Equal(2, handler.Calls);
                });
            await TestAsync($"{kind} expired credential replaced by login recovers", async () =>
            {
                var credentials = new MutableCredentials(new UsageCredential { AccessToken = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                var handler = new StubHandler(_ => JsonResponse(kind == UsageProviderKind.Codex ? """{"rate_limit":{"primary_window":{"used_percent":0}}}""" : """{"five_hour":{"utilization":0}}"""));
                var provider = new UsageProvider(kind, credentials, new HttpClient(handler));
                Equal(UsageStatus.AuthenticationRequired, (await provider.FetchAsync()).Status); Equal(0, handler.Calls);
                credentials.Current = new UsageCredential { AccessToken = "valid", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
                Equal(UsageStatus.Ready, (await provider.FetchAsync()).Status); Equal(1, handler.Calls);
            });
        }
        await TestAsync("Account switch cannot inherit previous account quota", async () =>
        {
            var credentials = new MutableCredentials(Credential());
            var handler = new StubHandler(r => r.Headers.GetValues("ChatGPT-Account-Id").Single() == "test-account"
                ? JsonResponse("""{"rate_limit":{"primary_window":{"used_percent":15}}}""") : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var provider = new UsageProvider(UsageProviderKind.Codex, credentials, new HttpClient(handler));
            Equal(1, (await provider.FetchAsync()).Windows.Count);
            credentials.Current = new UsageCredential { AccessToken = "test-secret", AccountId = "different-account" };
            var switched = await provider.FetchAsync(); Equal(UsageStatus.AuthenticationRequired, switched.Status); Equal(0, switched.Windows.Count); Equal(2, handler.Calls);
        });
        Test("Settings defaults are resource-friendly", () => { var s = new AppSettings(); Equal(120, s.RefreshIntervalSeconds); True(s.ShowInTaskbar); True(!s.AlwaysOnTop); True(!s.StartWithWindows); });
        foreach (var interval in new[] { 0, 30, 60, 120, 900, 86400 })
            Test($"Settings preserve valid interval {interval}", () => Equal(interval, SettingsStore.Normalize(new AppSettings { RefreshIntervalSeconds = interval }).RefreshIntervalSeconds));
        foreach (var interval in new[] { int.MinValue, -1, 1, 29, 86401, int.MaxValue })
            Test($"Settings normalize invalid interval {interval}", () => Equal(120, SettingsStore.Normalize(new AppSettings { RefreshIntervalSeconds = interval }).RefreshIntervalSeconds));
        Test("Settings clear non-finite coordinates", () => { var s = SettingsStore.Normalize(new AppSettings { Left = double.NaN, Top = double.PositiveInfinity }); Equal<double?>(null, s.Left); Equal<double?>(null, s.Top); });
        Test("Settings retain valid multi-monitor coordinates", () => { var s = SettingsStore.Normalize(new AppSettings { Left = -1500, Top = -300 }); Equal<double?>(-1500, s.Left); Equal<double?>(-300, s.Top); });
        Test("Window defaults allow compact useful dashboard", () => { var s = new AppSettings(); Equal(340d, s.WindowWidth); Equal(320d, s.WindowHeight); True(!s.CompactMode); });
        foreach (var value in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            Test($"Non-finite window size {value} restores defaults", () =>
            {
                var s = SettingsStore.Normalize(new AppSettings { WindowWidth = value, WindowHeight = value }); Equal(340d, s.WindowWidth); Equal(320d, s.WindowHeight);
            });
        Test("Tiny and enormous window dimensions stay bounded", () =>
        {
            var small = SettingsStore.Normalize(new AppSettings { WindowWidth = -1, WindowHeight = 0 }); Equal(220d, small.WindowWidth); Equal(140d, small.WindowHeight);
            var huge = SettingsStore.Normalize(new AppSettings { WindowWidth = double.MaxValue, WindowHeight = double.MaxValue }); Equal(1000d, huge.WindowWidth); Equal(1000d, huge.WindowHeight);
        });
        Test("Compact settings survive JSON roundtrip with chosen dimensions", () =>
        {
            var saved = new AppSettings { WindowWidth = 244, WindowHeight = 178, CompactMode = true, AlwaysOnTop = true };
            var restored = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!);
            Equal(244d, restored.WindowWidth); Equal(178d, restored.WindowHeight); True(restored.CompactMode); True(restored.AlwaysOnTop);
        });
        Console.WriteLine($"\n{_passed} passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task CredentialFilesAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VibeMeters-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reader = new CredentialReader(dir, dir);
            Equal<UsageCredential?>(null, await reader.ReadAsync(UsageProviderKind.Codex));
            await File.WriteAllTextAsync(Path.Combine(dir, "auth.json"), """{"OPENAI_API_KEY":"api-key-only"}""");
            Equal<UsageCredential?>(null, await reader.ReadAsync(UsageProviderKind.Codex));
            await File.WriteAllTextAsync(Path.Combine(dir, "auth.json"), """{"tokens":{"access_token":"fixture-codex","account_id":"fixture-account"}}""");
            var c = await reader.ReadAsync(UsageProviderKind.Codex); Equal("fixture-codex", c?.AccessToken); Equal("fixture-account", c?.AccountId);
            await File.WriteAllTextAsync(Path.Combine(dir, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"fixture-claude","subscriptionType":"max","expiresAt":1792756800000}}""");
            var a = await reader.ReadAsync(UsageProviderKind.Claude); Equal("fixture-claude", a?.AccessToken); Equal("max", a?.Plan); Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792756800000), a?.ExpiresAt);
            foreach (var expiry in new[] { "null", "\"invalid\"", "true", "9223372036854775807" })
            {
                await File.WriteAllTextAsync(Path.Combine(dir, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fixture\",\"expiresAt\":" + expiry + "}}");
                Equal<DateTimeOffset?>(null, (await reader.ReadAsync(UsageProviderKind.Claude))?.ExpiresAt);
            }
            foreach (var content in new[] { "null", "[]", "42", "{}", "{\"tokens\":null}", "{\"tokens\":{\"access_token\":true}}" })
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "auth.json"), content);
                Equal<UsageCredential?>(null, await reader.ReadAsync(UsageProviderKind.Codex));
            }
        }
        finally { foreach (var file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); }
    }

    private void Test(string name, Action action) { try { action(); _passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private async Task TestAsync(string name, Func<Task> action) { try { await action(); _passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static UsageSnapshot Parse(UsageProviderKind kind, string json, string? plan = null) { using var doc = JsonDocument.Parse(json); return UsageParser.Parse(kind, doc.RootElement, Now, plan); }
    private static UsageCredential Credential() => new() { AccessToken = "test-secret", AccountId = "test-account" };
    private static UsageProvider Provider(StubHandler handler, UsageCredential? credential, UsageProviderKind kind = UsageProviderKind.Codex) => new(kind, new StubCredentials(credential), new HttpClient(handler));
    private static UsageProvider Provider(StubHandler handler, UsageProviderKind kind = UsageProviderKind.Codex) => Provider(handler, Credential(), kind);
    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Condition was false"); }
}

sealed class StubCredentials(UsageCredential? credential) : ICredentialReader
{
    public Task<UsageCredential?> ReadAsync(UsageProviderKind provider, CancellationToken cancellationToken = default) => Task.FromResult(credential);
}

sealed class MutableCredentials(UsageCredential? credential) : ICredentialReader
{
    public UsageCredential? Current { get; set; } = credential;
    public Task<UsageCredential?> ReadAsync(UsageProviderKind provider, CancellationToken cancellationToken = default) => Task.FromResult(Current);
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(respond(request)); }
}

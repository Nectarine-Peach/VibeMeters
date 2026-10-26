using System.Reflection;
using System.Text;
using System.Text.Json;
using VibeMeters.Models;
using VibeMeters.Services;

internal static class GrokRegressionSuite
{
    private static readonly Type Bridge = typeof(GrokUsageProvider).Assembly.GetType("VibeMeters.Services.GrokCliBridge", true)!;
    internal static async Task<(int Passed, int Failed)> RunAsync()
    {
        var passed = 0; var failed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine("PASS Grok " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL Grok " + name + ": " + ex.GetBaseException().Message); }
        }
        void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        var ready = Parse(Billing(false));
        await Check("Build-only and shared subscription scope stay distinct", () =>
        {
            Require(ready.Status == UsageStatus.Ready && ready.Windows.Count == 1);
            Require(ready.Windows[0].Name == "Build 주간" && ready.Windows[0].RemainingPercent == 68);
            var shared = Parse(Billing(true)); Require(shared.Windows[0].Name == "공유 주간");
            Require(shared.Plan!.Contains("X 구독 공유", StringComparison.Ordinal));
            Require(ready.Message!.Contains("Grok Bot", StringComparison.Ordinal));
            Require(ready.Windows[0].ResetsAt == DateTimeOffset.Parse("2026-11-02T04:00:00Z"));
            Require(ready.Windows[0].Duration == TimeSpan.FromDays(7)); return Task.CompletedTask;
        });
        await Check("optional result wrapper and missing reset are safe", () =>
        {
            Require(Parse("{\"result\":" + Billing(true) + "}").Status == UsageStatus.Ready);
            var noReset = Parse(Billing(false).Replace("2026-11-02T04:00:00Z", "invalid"));
            Require(noReset.Windows[0].ResetsAt is null && noReset.Message!.Contains("초기화 시각", StringComparison.Ordinal));
            Require(Parse(Billing(false).Replace("USAGE_PERIOD_TYPE_WEEKLY", "UNKNOWN")).Windows[0].Name == "Build 한도");
            return Task.CompletedTask;
        });
        foreach (var invalid in new[] { "-1", "101", "1e999", "null", "true", "\"32\"" })
            await Check("rejects invalid included percentage " + invalid, () =>
            {
                Require(Parse(Billing(false).Replace("\"creditUsagePercent\":32", "\"creditUsagePercent\":" + invalid)).Status == UsageStatus.Unavailable);
                return Task.CompletedTask;
            });
        foreach (var input in new[] { "null", "[]", "{}", "{\"config\":null}", "{\"config\":{\"monthlyLimit\":10000,\"used\":3200}}" })
            await Check("never converts money or missing data into quota", () => { Require(Parse(input).Windows.Count == 0); return Task.CompletedTask; });
        await Check("legacy monthly label uses explicit period fields", () =>
        {
            var monthly = Parse("""{"config":{"creditUsagePercent":25,"monthlyLimit":{"val":10000},"billingPeriodStart":"2026-10-01T00:00:00Z","billingPeriodEnd":"2026-11-01T00:00:00Z","isUnifiedBillingUser":false}}""");
            Require(monthly.Windows[0].Name == "Build 월간" && monthly.Windows[0].RemainingPercent == 75);
            Require(monthly.Windows[0].Duration == TimeSpan.FromDays(31)); return Task.CompletedTask;
        });
        await Check("auth and throttle diagnostics expose no raw secrets", () =>
        {
            var auth = Error("Authentication required secret-token https://auth.x.ai/private");
            Require(auth.Status == UsageStatus.AuthenticationRequired && auth.Windows.Count == 0);
            Require(!auth.Message!.Contains("secret-token", StringComparison.Ordinal) && !auth.Message.Contains("https://", StringComparison.Ordinal));
            Require(Error("HTTP 429 rate limit").Status == UsageStatus.RateLimited);
            Require(Error("403 forbidden").Status == UsageStatus.Unavailable); return Task.CompletedTask;
        });
        await Check("actual ACP internal-error data preserves 429 and 401 classification", () =>
        {
            UsageSnapshot Internal(string data)
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { code = -32603, message = "Internal error", data }));
                return (UsageSnapshot)Bridge.GetMethod("ParseError", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [json.RootElement])!;
            }
            var limited = Internal("Billing service error: HTTP 429 secret-data");
            Require(limited.Status == UsageStatus.RateLimited && !limited.Message!.Contains("secret-data", StringComparison.Ordinal));
            Require(Internal("Billing service error: HTTP 401").Status == UsageStatus.AuthenticationRequired);
            Require(Error("", -32000).Status == UsageStatus.AuthenticationRequired);
            return Task.CompletedTask;
        });
        await Check("RPC requests contain no session, prompt or authenticate action", () =>
        {
            var initialize = Request("InitializeRequest"); var billing = Request("BillingRequest");
            using var first = JsonDocument.Parse(initialize); using var second = JsonDocument.Parse(billing);
            Require(first.RootElement.GetProperty("method").GetString() == "initialize");
            Require(first.RootElement.GetProperty("params").GetProperty("_meta").GetProperty("startupHints").GetProperty("nonInteractive").GetBoolean());
            Require(first.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString() == "VibeMeters");
            Require(second.RootElement.GetProperty("method").GetString() == "_x.ai/billing");
            Require(!initialize.Contains("authenticate", StringComparison.Ordinal) && !billing.Contains("session", StringComparison.Ordinal)); return Task.CompletedTask;
        });
        await Check("RPC matches exact response id and ignores notifications/other responses", async () =>
        {
            var reader = Reader("{\"jsonrpc\":\"2.0\",\"method\":\"status\"}\n{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":" + Billing(false) + "}\n");
            var result = await Response(reader, 2); Require(result.GetProperty("id").GetInt32() == 2);
        });
        await Check("interactive server requests are rejected without permission replies", async () =>
        {
            try { await Response(Reader("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"session/request_permission\"}\n"), 2); throw new Exception("Interactive request accepted"); }
            catch (IOException) { }
        });
        await Check("oversize and truncated CLI output fail closed", async () =>
        {
            try { await Response(Reader(new string('x', 65538) + "\n"), 2); throw new Exception("Oversize accepted"); }
            catch (IOException) { }
            try { await Response(Reader(""), 2); throw new Exception("EOF accepted"); }
            catch (IOException) { }
        });
        await Check("successful concurrent refreshes coalesce and mtime change invalidates cache", async () =>
        {
            var calls = 0; long stamp = 1;
            var provider = new GrokUsageProvider(_ => { calls++; return Task.FromResult(ready); }, () => stamp);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.FetchAsync())); Require(calls == 1);
            stamp++; await provider.FetchAsync(); Require(calls == 2);
        });
        await Check("failed refresh never inherits old account percentages", async () =>
        {
            var calls = 0;
            var provider = new GrokUsageProvider(_ => Task.FromResult(++calls == 1 ? ready : Error("Authentication required")), () => 0);
            await provider.FetchAsync(); provider.NotifyLoginCompleted();
            var error = await provider.FetchAsync(); Require(error.Windows.Count == 0 && error.Status == UsageStatus.AuthenticationRequired);
        });
        await Check("external login recovers immediately", async () =>
        {
            var calls = 0; long stamp = 1;
            var provider = new GrokUsageProvider(_ => Task.FromResult(++calls == 1 ? Error("401 unauthorized") : ready), () => stamp);
            await provider.FetchAsync();
            Require((await provider.FetchAsync()).Status == UsageStatus.AuthenticationRequired && calls == 1);
            stamp++; Require((await provider.FetchAsync()).Status == UsageStatus.Ready && calls == 2);
        });
        await Check("account change cannot bypass 429 cooldown", async () =>
        {
            var calls = 0; long stamp = 1;
            var provider = new GrokUsageProvider(_ => { calls++; return Task.FromResult(Error("429 rate limit")); }, () => stamp);
            var first = await provider.FetchAsync(); stamp++; provider.NotifyLoginCompleted();
            var second = await provider.FetchAsync(); Require(calls == 1 && second.Windows.Count == 0);
            Require(second.RetryAfter == first.RetryAfter && first.RetryAfter > DateTimeOffset.UtcNow.AddMinutes(9));
        });
        await Check("mid-query account change discards result and still preserves throttle", async () =>
        {
            long stamp = 1;
            var provider = new GrokUsageProvider(_ => { stamp++; return Task.FromResult(ready); }, () => stamp);
            Require((await provider.FetchAsync()).Windows.Count == 0);
            var calls = 0;
            var throttled = new GrokUsageProvider(_ => { calls++; stamp++; return Task.FromResult(Error("429 rate limit")); }, () => stamp);
            await throttled.FetchAsync(); Require((await throttled.FetchAsync()).Status == UsageStatus.RateLimited && calls == 1);
        });
        await Check("explicit login generation invalidates an in-flight response", async () =>
        {
            var result = new TaskCompletionSource<UsageSnapshot>();
            var provider = new GrokUsageProvider(_ => result.Task, () => 0);
            var pending = provider.FetchAsync(); provider.NotifyLoginCompleted(); result.SetResult(ready);
            Require((await pending).Windows.Count == 0);
        });
        await Check("caller cancellation launches no usage process", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var provider = new GrokUsageProvider(_ => throw new Exception("Must not query"), () => 0);
            try { await provider.FetchAsync(cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        });
        await Check("Grok remains separate from HTTP/CredentialReader paths", async () =>
        {
            try { _ = new UsageProvider(UsageProviderKind.Grok, new CredentialReader()); throw new Exception("Wrong provider accepted"); }
            catch (ArgumentException) { }
            Require(UsageProviderFactory.CreateDefault().Single(p => p.Kind == UsageProviderKind.Grok) is GrokUsageProvider);
            Require(await new CredentialReader().ReadAsync(UsageProviderKind.Grok) is null);
            var fixtureHome = Path.Combine(Path.GetTempPath(), "VibeMeters-Grok-Path-Only");
            Require(new CredentialReader(grokHome: fixtureHome).GetCredentialPath(UsageProviderKind.Grok) == Path.Combine(fixtureHome, "auth.json"));
        });
        await Check("official auth path overrides and relative paths match CLI home cwd", () =>
        {
            var oldHome = Environment.GetEnvironmentVariable("GROK_HOME");
            var oldPath = Environment.GetEnvironmentVariable("GROK_AUTH_PATH");
            var resolver = typeof(GrokUsageProvider).Assembly.GetType("VibeMeters.Services.GrokAuthPath", true)!;
            var method = resolver.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!;
            var profile = Path.Combine(Path.GetTempPath(), "Grok-Path-Fixture");
            string Resolve() => (string)method.Invoke(null, [profile])!;
            try
            {
                Environment.SetEnvironmentVariable("GROK_AUTH_PATH", null); Environment.SetEnvironmentVariable("GROK_HOME", null);
                Require(Resolve() == Path.Combine(profile, ".grok", "auth.json"));
                Environment.SetEnvironmentVariable("GROK_HOME", "custom-home");
                Require(Resolve() == Path.Combine(profile, "custom-home", "auth.json"));
                Environment.SetEnvironmentVariable("GROK_AUTH_PATH", "other-auth.json");
                Require(Resolve() == Path.Combine(profile, "other-auth.json"));
                var absolute = Path.Combine(profile, "absolute-auth.json"); Environment.SetEnvironmentVariable("GROK_AUTH_PATH", absolute);
                Require(Resolve() == absolute && new CredentialReader().GetCredentialPath(UsageProviderKind.Grok) == absolute);
            }
            finally { Environment.SetEnvironmentVariable("GROK_HOME", oldHome); Environment.SetEnvironmentVariable("GROK_AUTH_PATH", oldPath); }
            return Task.CompletedTask;
        });
        await Check("Grok-only visibility and display mode survive serialization", () =>
        {
            var settings = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings {
                ShowCodex = false, ShowClaude = false, ShowGemini = false, ShowGrok = true, GrokDisplayMode = UsageDisplayMode.Used }))!);
            Require(settings.IsProviderVisible(UsageProviderKind.Grok) && !settings.IsProviderVisible(UsageProviderKind.Codex));
            Require(settings.GetDisplayMode(UsageProviderKind.Grok) == UsageDisplayMode.Used && !new AppSettings().ShowGrok);
            Require(SettingsStore.Normalize(new AppSettings { GrokDisplayMode = (UsageDisplayMode)99 }).GrokDisplayMode == UsageDisplayMode.Remaining);
            return Task.CompletedTask;
        });
        await Check("OAuth browser fallback allows only the official auth host", () =>
        {
            var method = typeof(LoginService).GetMethod("BrowserLoginUri", BindingFlags.Static | BindingFlags.NonPublic)!;
            Require(method.Invoke(null, [UsageProviderKind.Grok, "https://auth.x.ai/oauth/authorize"]) is Uri);
            Require(method.Invoke(null, [UsageProviderKind.Grok, "https://auth.x.ai.evil.invalid/"]) is null);
            return Task.CompletedTask;
        });
        return (passed, failed);
    }
    private static string Billing(bool unified) => "{\"config\":{\"creditUsagePercent\":32,\"isUnifiedBillingUser\":" + (unified ? "true" : "false") + ",\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2026-10-26T04:00:00Z\",\"end\":\"2026-11-02T04:00:00Z\"}}}";
    private static UsageSnapshot Parse(string text) { using var json = JsonDocument.Parse(text); return (UsageSnapshot)Bridge.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [json.RootElement])!; }
    private static UsageSnapshot Error(string message, int code = -32603) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { code, message })); return (UsageSnapshot)Bridge.GetMethod("ParseError", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [json.RootElement])!; }
    private static string Request(string name) => (string)Bridge.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
    private static object Reader(string text) => Activator.CreateInstance(Bridge.GetNestedType("BoundedLines", BindingFlags.NonPublic)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
        [new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)))], null)!;
    private static Task<JsonElement> Response(object reader, int id) => (Task<JsonElement>)Bridge.GetMethod("ReadResponseAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [reader, id, CancellationToken.None])!;
}

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VibeMeters.Models;
using VibeMeters.Services;

if (args.Contains("--grok-live-check"))
{
    var actual = await new GrokUsageProvider().FetchAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { Status = actual.Status.ToString(), actual.Plan, actual.CheckedAt,
        actual.Message, Windows = actual.Windows.Select(window => new { window.Name, window.UsedPercent, window.RemainingPercent, window.ResetsAt }) }));
    return actual.Status == UsageStatus.Ready ? 0 : 2;
}
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
        foreach (var kind in OAuthProviders)
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
        Test("Codex Business nested individual limit matches official remaining percentage", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"plan_type":"business","rate_limit":null,"credits":{"has_credits":true},"spend_control":{"reached":false,"individual_limit":{"source":"group_based_spend_controls","unit":"credit","limit":"10000","used":"5200","remaining":"4800","used_percent":52,"remaining_percent":48,"reset_after_seconds":3600,"reset_at":1793491200}}}""");
            Equal(UsageStatus.Ready, s.Status); Equal("business", s.Plan); Equal(1, s.Windows.Count);
            Equal("개인 한도", s.Windows[0].Name); Equal(48d, s.Windows[0].RemainingPercent); Equal(52d, s.Windows[0].UsedPercent);
            Equal(DateTimeOffset.FromUnixTimeSeconds(1793491200), s.Windows[0].ResetsAt); Equal<TimeSpan?>(null, s.Windows[0].Duration);
            var window = typeof(LoginService).Assembly.GetType("VibeMeters.MainWindow", true)!;
            Equal("개인", (string)window.GetMethod("ShortQuotaName", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [s.Windows[0].Name])!);
        });
        Test("Codex individual remaining percentage wins over used percentage", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"spend_control":{"individual_limit":{"remaining_percent":48,"used_percent":20}}}""");
            Equal(48d, s.Windows[0].RemainingPercent); Equal(52d, s.Windows[0].UsedPercent);
        });
        Test("Codex individual used percentage and relative reset are supported", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"spend_control":{"individual_limit":{"used_percent":25,"reset_at":9223372036854775807,"reset_after_seconds":3600}}}""");
            Equal(75d, s.Windows[0].RemainingPercent); Equal(Now.AddHours(1), s.Windows[0].ResetsAt);
        });
        Test("Codex personal subscription and individual limit remain separate", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"rate_limit":{"primary_window":{"used_percent":10},"secondary_window":{"used_percent":20}},"spend_control":{"individual_limit":{"remaining_percent":48}}}""");
            Equal(3, s.Windows.Count); Equal("5시간", s.Windows[0].Name); Equal("주간", s.Windows[1].Name); Equal("개인 한도", s.Windows[2].Name);
        });
        foreach (var value in new[] { "null", "\"48\"", "1e999", "true" })
            Test("Codex individual limit rejects invalid remaining percent " + value, () =>
            {
                var s = Parse(UsageProviderKind.Codex, "{\"spend_control\":{\"individual_limit\":{\"remaining_percent\":" + value + ",\"limit\":\"100\",\"used\":\"20\"}}}");
                Equal(0, s.Windows.Count); Equal(UsageStatus.Unavailable, s.Status);
            });
        Test("Codex malformed individual limit retains valid credit state", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"spend_control":{"individual_limit":null},"credits":{"has_credits":true}}""");
            Equal(UsageStatus.Ready, s.Status); Equal(0, s.Windows.Count);
        });
        Test("Codex individual percentages clamp and unknown reset stays unknown", () =>
        {
            var full = Parse(UsageProviderKind.Codex, """{"spend_control":{"individual_limit":{"remaining_percent":120,"reset_after_seconds":-1}}}""");
            Equal(100d, full.Windows[0].RemainingPercent); Equal<DateTimeOffset?>(null, full.Windows[0].ResetsAt);
            var empty = Parse(UsageProviderKind.Codex, """{"spend_control":{"individual_limit":{"remaining_percent":-1}}}""");
            Equal(0d, empty.Windows[0].RemainingPercent);
        });
        Test("Business Codex credits authenticate without inventing quota", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"plan_type":"business","rate_limit":null,"credits":{"has_credits":true,"unlimited":false,"balance":null}}""");
            Equal(UsageStatus.Ready, s.Status); Equal("business", s.Plan); Equal(0, s.Windows.Count);
            True(s.Message!.Contains("비율", StringComparison.Ordinal));
            var window = typeof(LoginService).Assembly.GetType("VibeMeters.MainWindow", true)!;
            Equal(1, (int)window.GetMethod("QuotaRowCount", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [s])!);
        });
        Test("Business credit state preserves exhausted and unlimited distinctions", () =>
        {
            var exhausted = Parse(UsageProviderKind.Codex, """{"credits":{"has_credits":false,"unlimited":false,"overage_limit_reached":true}}""");
            Equal(UsageStatus.Ready, exhausted.Status); True(exhausted.Message!.Contains("도달", StringComparison.Ordinal)); Equal(0, exhausted.Windows.Count);
            var unlimited = Parse(UsageProviderKind.Codex, """{"credits":{"unlimited":true}}""");
            Equal(UsageStatus.Ready, unlimited.Status); True(unlimited.Message!.Contains("무제한", StringComparison.Ordinal)); Equal(0, unlimited.Windows.Count);
        });
        Test("Personal Codex keeps subscription quota when credits are also present", () =>
        {
            var s = Parse(UsageProviderKind.Codex, """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":20}},"credits":{"has_credits":true}}""");
            Equal(UsageStatus.Ready, s.Status); Equal(1, s.Windows.Count); Equal(80d, s.Windows[0].RemainingPercent); Equal<string?>(null, s.Message);
        });
        foreach (var credits in new[] { "null", "{}", "[]", "{\"has_credits\":\"true\"}" })
            Test("Malformed credit state is not mistaken for connected " + credits, () =>
                Equal(UsageStatus.Unavailable, Parse(UsageProviderKind.Codex, "{\"credits\":" + credits + "}").Status));
        Test("Enterprise Claude displays supplied monthly additional usage", () =>
        {
            var s = Parse(UsageProviderKind.Claude, """{"five_hour":null,"seven_day":null,"extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":6000,"utilization":60}}""", "enterprise");
            Equal(UsageStatus.Ready, s.Status); Equal("enterprise", s.Plan); Equal(1, s.Windows.Count);
            Equal("월 추가 사용", s.Windows[0].Name); Equal(60d, s.Windows[0].UsedPercent);
            Equal<TimeSpan?>(null, s.Windows[0].Duration); Equal<DateTimeOffset?>(null, s.Windows[0].ResetsAt);
            True(s.Message!.Contains("구독 한도와 다릅니다", StringComparison.Ordinal));
        });
        Test("Personal Claude retains subscription and additional usage separately", () =>
        {
            var s = Parse(UsageProviderKind.Claude, """{"five_hour":{"utilization":20},"seven_day":{"utilization":30},"extra_usage":{"is_enabled":true,"utilization":40}}""", "pro");
            Equal(3, s.Windows.Count); Equal("5시간", s.Windows[0].Name); Equal("주간", s.Windows[1].Name); Equal("월 추가 사용", s.Windows[2].Name);
        });
        Test("Disabled extra usage is omitted", () =>
            Equal(0, Parse(UsageProviderKind.Claude, """{"extra_usage":{"is_enabled":false,"utilization":40}}""").Windows.Count));
        foreach (var value in new[] { "null", "\"25\"", "1e999", "true" })
            Test("Enabled extra usage never fabricates invalid percentage " + value, () =>
            {
                var s = Parse(UsageProviderKind.Claude, "{\"extra_usage\":{\"is_enabled\":true,\"utilization\":" + value + "}}");
                Equal(UsageStatus.Ready, s.Status); Equal(0, s.Windows.Count);
            });
        Test("Browser fallback accepts only matching official HTTPS hosts", () =>
        {
            static Uri? Link(UsageProviderKind kind, string text) => (Uri?)typeof(LoginService)
                .GetMethod("BrowserLoginUri", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [kind, text]);
            True(Link(UsageProviderKind.Codex, "Open https://auth.openai.com/oauth/authorize?state=fixture") is not null);
            True(Link(UsageProviderKind.Claude, "https://claude.ai/oauth/authorize?state=fixture") is not null);
            foreach (var url in new[] { "http://auth.openai.com/", "https://auth.openai.com.evil.invalid/", "https://evil.invalid/auth.openai.com", "https://user@auth.openai.com/", "https://auth.openai.com:444/", "file:///C:/fixture", "https://claude.ai/" })
                Equal<Uri?>(null, Link(UsageProviderKind.Codex, url));
            Equal<Uri?>(null, Link(UsageProviderKind.Gemini, "https://auth.openai.com/"));
        });
        Test("Login diagnostics redact raw network errors", () =>
        {
            var type = typeof(LoginService).GetNestedType("LoginDiagnostics", BindingFlags.NonPublic)!;
            var diagnostics = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [UsageProviderKind.Claude], null)!;
            type.GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(diagnostics, ["proxy failed secret-token https://evil.invalid/?state=secret-state"]);
            var message = (string)type.GetProperty("FailureMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(diagnostics)!;
            True(message.Contains("프록시", StringComparison.Ordinal)); True(!message.Contains("secret", StringComparison.Ordinal)); True(!message.Contains("https", StringComparison.Ordinal));
        });
        await TestAsync("Proxy authentication failure has actionable network message", async () =>
        {
            var s = await Provider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired))).FetchAsync();
            Equal(UsageStatus.Unavailable, s.Status); True(s.Message!.Contains("프록시", StringComparison.Ordinal));
        });
        await TestAsync("Forbidden response differs from expired login", async () =>
        {
            var s = await Provider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))).FetchAsync();
            Equal(UsageStatus.AuthenticationRequired, s.Status); True(s.Message!.Contains("권한", StringComparison.Ordinal)); True(!s.Message.Contains("만료", StringComparison.Ordinal));
        });
        Test("Credential string redacts token", () => True(!Credential().ToString().Contains("test-secret", StringComparison.Ordinal)));
        Test("Gemini official bridge parses model quotas, plan and reset", () =>
        {
            var s = ParseGemini("""{"status":"Ready","plan":"Google AI Pro","windows":[{"name":"2.5-pro","remainingPercent":62.5,"resetsAt":"2026-10-24T12:00:00Z"},{"name":"2.5-flash","remainingPercent":0},{"name":"3-pro","remainingPercent":100}]}""");
            Equal(UsageProviderKind.Gemini, s.Provider); Equal(UsageStatus.Ready, s.Status); Equal("Google AI Pro", s.Plan);
            Equal(3, s.Windows.Count); Equal("2.5-pro", s.Windows[0].Name); Equal(62.5, s.Windows[0].RemainingPercent);
            Equal(DateTimeOffset.Parse("2026-10-24T12:00:00Z"), s.Windows[0].ResetsAt);
            Equal(0d, s.Windows[1].RemainingPercent); Equal(100d, s.Windows[2].RemainingPercent);
        });
        foreach (var value in new[] { "null", "\"25\"", "1e999", "true", "-1", "101" })
            Test($"Gemini bridge rejects invalid remaining percentage {value}", () =>
            {
                var s = ParseGemini("{\"status\":\"Ready\",\"windows\":[{\"name\":\"pro\",\"remainingPercent\":" + value + "}]}");
                Equal(UsageStatus.Unavailable, s.Status); Equal(0, s.Windows.Count);
            });
        foreach (var input in new[] { "null", "[]", "42", "{}", "{\"status\":\"Ready\"}", "{\"status\":\"Ready\",\"windows\":[{},null,42]}" })
            Test($"Gemini bridge safely handles missing quota {input}", () => Equal(UsageStatus.Unavailable, ParseGemini(input).Status));
        foreach (var (reason, status) in new[]
        {
            ("AUTH_REQUIRED", UsageStatus.AuthenticationRequired), ("RATE_LIMITED", UsageStatus.RateLimited),
            ("PROJECT_REQUIRED", UsageStatus.Unavailable), ("RUNTIME_REQUIRED", UsageStatus.Unavailable),
            ("NO_QUOTA", UsageStatus.Unavailable), ("NETWORK", UsageStatus.Unavailable)
        })
            Test($"Gemini bridge maps {reason}", () => Equal(status, ParseGemini("{\"status\":\"Unavailable\",\"reason\":\"" + reason + "\"}").Status));
        Test("Gemini unsupported client is not reported as a project error", () =>
        {
            var s = ParseGemini("""{"status":"Unavailable","reason":"UNSUPPORTED_CLIENT"}""");
            True(s.Message!.Contains("개인 계정 지원이 종료", StringComparison.Ordinal));
            True(!s.Message.Contains("프로젝트", StringComparison.Ordinal));
        });
        const string agyQuota = """{"status":"SUCCESS","num_turns":0,"usage":{"total_tokens":0},"command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.625,"reset_time":"2026-11-02T04:36:49Z"},{"id":"gemini-5h","window":"5h","remaining_fraction":1,"reset_time":"2026-10-26T09:36:49Z"}]},{"name":"Claude and GPT models","buckets":[{"id":"3p-weekly","window":"weekly","remaining_fraction":0.1}]},{"name":"Gemini 4 Argon","buckets":[{"id":"gemini-argon-wtu-weekly","window":"weekly","remaining_fraction":0}]}]}}}""";
        Test("Antigravity command parses Gemini shared quotas without mixing Claude", () =>
        {
            var s = ParseAntigravity(agyQuota);
            Equal(UsageStatus.Ready, s.Status); Equal("Antigravity", s.Plan); Equal(3, s.Windows.Count);
            Equal("5시간", s.Windows[0].Name); Equal(100d, s.Windows[0].RemainingPercent);
            Equal("주간", s.Windows[1].Name); Equal(62.5, s.Windows[1].RemainingPercent);
            Equal("4 Argon 주간", s.Windows[2].Name); Equal(0d, s.Windows[2].RemainingPercent);
            Equal(DateTimeOffset.Parse("2026-11-02T04:36:49Z"), s.Windows[1].ResetsAt);
            Equal(TimeSpan.FromDays(7), s.Windows[1].Duration);
        });
        foreach (var input in new[] { "null", "[]", "{}", agyQuota.Replace("\"num_turns\":0", "\"num_turns\":1"),
            agyQuota.Replace("\"num_turns\":0", "\"num_turns\":\"0\""), agyQuota.Replace("\"total_tokens\":0", "\"total_tokens\":1"),
            agyQuota.Replace("\"name\":\"usage\"", "\"name\":\"agent\""), agyQuota.Replace("SUCCESS", "ERROR") })
            Test("Antigravity rejects malformed or model-generated usage envelope", () => Equal(UsageStatus.Unavailable, ParseAntigravity(input).Status));
        foreach (var value in new[] { "null", "\"0.5\"", "-0.1", "1.1", "1e999", "true" })
            Test($"Antigravity rejects invalid fraction {value}", () =>
            {
                var s = ParseAntigravity(agyQuota.Replace("0.625", value));
                Equal(2, s.Windows.Count); True(s.Windows.All(w => w.Name != "주간"));
            });
        Test("Antigravity minimum version prevents old print-mode model requests", () =>
        {
            var bridge = typeof(GeminiUsageProvider).Assembly.GetType("VibeMeters.Services.AntigravityUsageBridge", true)!;
            bool Supported(string version) => (bool)bridge.GetMethod("IsSupportedVersion", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [version])!;
            True(Supported("1.4.7\n")); True(Supported("2.0.0")); True(!Supported("1.4.6")); True(!Supported("unknown"));
        });
        Test("Antigravity ignores non-Gemini groups and reports missing quota", () =>
            Equal(UsageStatus.Unavailable, ParseAntigravity(agyQuota.Replace("Gemini ", "Other ")).Status));
        Test("Antigravity sanitizes authentication and rate limit failures", () =>
        {
            var auth = ParseAntigravity("""{"status":"ERROR","error":"authentication required secret-token https://example.invalid/oauth"}""");
            Equal(UsageStatus.AuthenticationRequired, auth.Status); Equal(0, auth.Windows.Count);
            True(!auth.Message!.Contains("secret-token", StringComparison.Ordinal)); True(!auth.Message.Contains("https://", StringComparison.Ordinal));
            Equal(UsageStatus.RateLimited, ParseAntigravity("""{"status":"ERROR","error":"429 rate limit"}""").Status);
        });
        await TestAsync("Antigravity refresh reads current keyring account and drops stale quota on failure", async () =>
        {
            var calls = 0;
            var provider = new GeminiUsageProvider(_ => Task.FromResult(++calls == 1 ? ParseAntigravity(agyQuota)
                : new UsageSnapshot(UsageProviderKind.Gemini, UsageStatus.AuthenticationRequired, [], Now, "Antigravity", "로그인 필요")), () => 0);
            Equal(3, (await provider.FetchAsync()).Windows.Count);
            var failure = await provider.FetchAsync();
            Equal(2, calls); Equal(0, failure.Windows.Count); Equal(UsageStatus.AuthenticationRequired, failure.Status);
        });
        await TestAsync("Gemini account change cannot bypass Antigravity rate limit or inherit quota", async () =>
        {
            var calls = 0; long stamp = 0;
            var provider = new GeminiUsageProvider(_ => Task.FromResult(++calls == 1 ? ParseAntigravity(agyQuota)
                : new UsageSnapshot(UsageProviderKind.Gemini, UsageStatus.RateLimited, [], Now, "Antigravity", "요청 제한")), () => stamp);
            await provider.FetchAsync();
            Equal(UsageStatus.RateLimited, (await provider.FetchAsync()).Status);
            stamp++; provider.NotifyLoginCompleted();
            var limited = await provider.FetchAsync();
            Equal(2, calls); Equal(UsageStatus.RateLimited, limited.Status); Equal(0, limited.Windows.Count);
        });
        await TestAsync("Antigravity external login recovers on next manual refresh", async () =>
        {
            var calls = 0;
            var provider = new GeminiUsageProvider(_ => Task.FromResult(++calls == 1
                ? new UsageSnapshot(UsageProviderKind.Gemini, UsageStatus.AuthenticationRequired, [], Now, "Antigravity", "로그인 필요")
                : ParseAntigravity(agyQuota)), () => 0);
            Equal(UsageStatus.AuthenticationRequired, (await provider.FetchAsync()).Status);
            Equal(UsageStatus.Ready, (await provider.FetchAsync()).Status); Equal(2, calls);
        });
        await TestAsync("Gemini provider propagates caller cancellation", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var provider = new GeminiUsageProvider(_ => throw new Exception("Must not query"), () => 0);
            try { await provider.FetchAsync(cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        });
        Test("Gemini is routed to its dedicated official CLI provider", () =>
        {
            True(UsageProviderFactory.CreateDefault().Single(p => p.Kind == UsageProviderKind.Gemini) is GeminiUsageProvider);
            try { _ = Provider(new StubHandler(_ => throw new Exception("Must not request")), kind: UsageProviderKind.Gemini); throw new Exception("Wrong provider accepted Gemini"); }
            catch (ArgumentException) { }
        });

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
        foreach (var kind in OAuthProviders)
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
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
            await TestAsync($"{kind} token renewal cannot bypass HTTP 429 cooldown", async () =>
            {
                var credentials = new MutableCredentials(Credential());
                var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                var provider = new UsageProvider(kind, credentials, new HttpClient(handler));
                var limited = await provider.FetchAsync();
                credentials.Current = new UsageCredential { AccessToken = "new-login", AccountId = "new-account" };
                var retried = await provider.FetchAsync();
                Equal(UsageStatus.RateLimited, retried.Status); Equal(limited, retried); Equal(1, handler.Calls);
                True(retried.RetryAfter > DateTimeOffset.UtcNow.AddSeconds(kind == UsageProviderKind.Claude ? 290 : 50));
            });
            await TestAsync($"{kind} successful polling floor survives token renewal", async () =>
            {
                var credentials = new MutableCredentials(Credential());
                var handler = new StubHandler(_ => JsonResponse(kind == UsageProviderKind.Codex ? """{"rate_limit":{"primary_window":{"used_percent":18}}}""" : """{"five_hour":{"utilization":18}}"""));
                var provider = new UsageProvider(kind, credentials, new HttpClient(handler));
                var original = await provider.FetchAsync();
                credentials.Current = new UsageCredential { AccessToken = "renewed-token", AccountId = "test-account" };
                Equal(original, await provider.FetchAsync()); Equal(1, handler.Calls);
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
        foreach (var kind in OAuthProviders)
        {
            await TestAsync($"{kind} explicit login clears prior account values and polling floor", async () =>
            {
                var credentials = new MutableCredentials(Credential());
                var handler = new StubHandler(r => JsonResponse(kind == UsageProviderKind.Codex
                    ? (r.Headers.Authorization!.Parameter == "test-secret" ? """{"rate_limit":{"primary_window":{"used_percent":20}}}""" : """{"plan_type":"business","rate_limit":null,"credits":{"has_credits":true}}""")
                    : (r.Headers.Authorization!.Parameter == "test-secret" ? """{"five_hour":{"utilization":20}}""" : """{"extra_usage":{"is_enabled":true,"utilization":80}}""")));
                var provider = new UsageProvider(kind, credentials, new HttpClient(handler));
                Equal(1, (await provider.FetchAsync()).Windows.Count);
                credentials.Current = new UsageCredential { AccessToken = "different-account" };
                provider.NotifyLoginCompleted();
                var switched = await provider.FetchAsync(); Equal(UsageStatus.Ready, switched.Status); Equal(2, handler.Calls);
                if (kind == UsageProviderKind.Codex) Equal(0, switched.Windows.Count);
                else { Equal("월 추가 사용", switched.Windows[0].Name); Equal(80d, switched.Windows[0].UsedPercent); }
            });
            await TestAsync($"{kind} explicit account switch preserves 429 deadline without old values", async () =>
            {
                var calls = 0;
                var handler = new StubHandler(_ => ++calls == 1
                    ? JsonResponse(kind == UsageProviderKind.Codex ? """{"rate_limit":{"primary_window":{"used_percent":20}}}""" : """{"five_hour":{"utilization":20}}""")
                    : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                var provider = Provider(handler, kind: kind);
                Equal(1, (await provider.FetchAsync()).Windows.Count);
                typeof(UsageProvider).GetField("_nextAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, DateTimeOffset.MinValue);
                var limited = await provider.FetchAsync(); Equal(1, limited.Windows.Count);
                provider.NotifyLoginCompleted();
                var switched = await provider.FetchAsync();
                Equal(UsageStatus.RateLimited, switched.Status); Equal(limited.RetryAfter, switched.RetryAfter); Equal(0, switched.Windows.Count); Equal(2, handler.Calls);
            });
        }
        Test("Settings defaults are resource-friendly", () => { var s = new AppSettings(); Equal(120, s.RefreshIntervalSeconds); True(s.ShowInTaskbar); True(!s.AlwaysOnTop); True(!s.StartWithWindows); });
        foreach (var interval in new[] { 0, 30, 60, 120, 900, 86400 })
            Test($"Settings preserve valid interval {interval}", () => Equal(interval, SettingsStore.Normalize(new AppSettings { RefreshIntervalSeconds = interval }).RefreshIntervalSeconds));
        foreach (var interval in new[] { int.MinValue, -1, 1, 29, 86401, int.MaxValue })
            Test($"Settings normalize invalid interval {interval}", () => Equal(120, SettingsStore.Normalize(new AppSettings { RefreshIntervalSeconds = interval }).RefreshIntervalSeconds));
        Test("Settings clear non-finite coordinates", () => { var s = SettingsStore.Normalize(new AppSettings { Left = double.NaN, Top = double.PositiveInfinity }); Equal<double?>(null, s.Left); Equal<double?>(null, s.Top); });
        Test("Settings retain valid multi-monitor coordinates", () => { var s = SettingsStore.Normalize(new AppSettings { Left = -1500, Top = -300 }); Equal<double?>(-1500, s.Left); Equal<double?>(-300, s.Top); });
        Test("Window defaults allow compact useful dashboard", () => { var s = new AppSettings(); Equal(360d, s.WindowWidth); Equal(360d, s.WindowHeight); True(!s.CompactMode); });
        foreach (var value in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            Test($"Non-finite window size {value} restores defaults", () =>
            {
                var s = SettingsStore.Normalize(new AppSettings { WindowWidth = value, WindowHeight = value }); Equal(360d, s.WindowWidth); Equal(360d, s.WindowHeight);
            });
        Test("Tiny and enormous window dimensions stay bounded", () =>
        {
            var small = SettingsStore.Normalize(new AppSettings { WindowWidth = -1, WindowHeight = 0 }); Equal(220d, small.WindowWidth); Equal(140d, small.WindowHeight);
            var huge = SettingsStore.Normalize(new AppSettings { WindowWidth = double.MaxValue, WindowHeight = double.MaxValue }); Equal(16384d, huge.WindowWidth); Equal(16384d, huge.WindowHeight);
        });
        Test("Large window geometry survives normalization and JSON roundtrip", () =>
        {
            var saved = new AppSettings { WindowWidth = 1480, WindowHeight = 1120, Left = -1820, Top = -260 };
            var restored = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!);
            Equal(1480d, restored.WindowWidth); Equal(1120d, restored.WindowHeight);
            Equal<double?>(-1820, restored.Left); Equal<double?>(-260, restored.Top);
        });
        Test("Window visibility defaults to visible for new and legacy settings", () =>
        {
            True(new AppSettings().WindowVisible);
            True(SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>("{\"WindowWidth\":244,\"WindowHeight\":178}")!).WindowVisible);
        });
        foreach (var visible in new[] { true, false })
            Test($"Window visibility {visible} survives JSON roundtrip", () =>
            {
                var restored = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { WindowVisible = visible }))!);
                Equal(visible, restored.WindowVisible);
            });
        Test("Compact settings survive JSON roundtrip with chosen dimensions", () =>
        {
            var saved = new AppSettings { WindowWidth = 244, WindowHeight = 178, CompactMode = true, AlwaysOnTop = true };
            var restored = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!);
            Equal(244d, restored.WindowWidth); Equal(178d, restored.WindowHeight); True(restored.CompactMode); True(restored.AlwaysOnTop);
        });
        Test("Hidden services cannot leave an empty dashboard", () =>
        {
            var s = SettingsStore.Normalize(new AppSettings { ShowCodex = false, ShowClaude = false, ShowGemini = false });
            True(s.ShowCodex); True(!s.ShowClaude); True(!s.ShowGemini);
        });
        Test("Theme and service display choices survive JSON roundtrip", () =>
        {
            var saved = new AppSettings { Theme = AppTheme.Light, ShowCodex = false, ShowClaude = false, ShowGemini = true,
                CodexDisplayMode = UsageDisplayMode.Used, ClaudeDisplayMode = UsageDisplayMode.Remaining, GeminiDisplayMode = UsageDisplayMode.Used };
            var restored = SettingsStore.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!);
            Equal(AppTheme.Light, restored.Theme); True(restored.IsProviderVisible(UsageProviderKind.Gemini));
            True(!restored.IsProviderVisible(UsageProviderKind.Codex)); True(!restored.IsProviderVisible(UsageProviderKind.Claude));
            Equal(UsageDisplayMode.Used, restored.GetDisplayMode(UsageProviderKind.Codex));
            Equal(UsageDisplayMode.Remaining, restored.GetDisplayMode(UsageProviderKind.Claude));
            Equal(UsageDisplayMode.Used, restored.GetDisplayMode(UsageProviderKind.Gemini));
        });
        Test("Invalid theme and usage modes restore safe defaults", () =>
        {
            var s = SettingsStore.Normalize(new AppSettings { Theme = (AppTheme)999, CodexDisplayMode = (UsageDisplayMode)(-1),
                ClaudeDisplayMode = (UsageDisplayMode)999, GeminiDisplayMode = (UsageDisplayMode)999 });
            Equal(AppTheme.System, s.Theme);
            foreach (var kind in Enum.GetValues<UsageProviderKind>()) Equal(UsageDisplayMode.Remaining, s.GetDisplayMode(kind));
        });
        Test("Official native CLI stays hidden and passes arguments literally", () => WithCliFixture(".exe", (name, path) =>
        {
            string[] arguments = ["login", "name with spaces", "apostrophe's", "$(unexpected) & ;"];
            var start = CreateCliStartInfo(name, arguments)!;
            Equal(path, start.FileName); True(start.ArgumentList.SequenceEqual(arguments));
            AssertHiddenCli(start);
        }));
        Test("Official npm CLI quotes special characters in encoded hidden invocation", () => WithCliFixture(".cmd", (name, path) =>
        {
            string[] arguments = ["auth", "login", "--claudeai", "apostrophe's & $(unexpected); with spaces"];
            var start = CreateCliStartInfo(name, arguments)!;
            Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), start.FileName);
            True(start.ArgumentList.Take(4).SequenceEqual(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" }));
            Equal(5, start.ArgumentList.Count);
            var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[4]));
            static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
            Equal("& " + Quote(path) + " " + string.Join(" ", arguments.Select(Quote)) + "; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 }", decoded);
            True(!start.ArgumentList.Contains("-NoExit")); AssertHiddenCli(start);
        }));
        Test("Missing official CLI returns no start info", () => Equal<ProcessStartInfo?>(null, CreateCliStartInfo("vibemeters-missing-" + Guid.NewGuid().ToString("N"), "login")));
        foreach (var kind in Enum.GetValues<UsageProviderKind>())
            await TestAsync($"{kind} already-cancelled login returns without launch", async () =>
            {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var result = await LoginService.LoginAsync(kind, cancelled.Token);
                True(!result.Success); True(result.Message.Contains("취소", StringComparison.Ordinal));
            });
        var grok = await GrokRegressionSuite.RunAsync(); _passed += grok.Passed; _failed += grok.Failed;
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
    private static readonly UsageProviderKind[] OAuthProviders = [UsageProviderKind.Codex, UsageProviderKind.Claude];
    // Exercise the sanitized bridge contract without launching Node, reading user credentials or contacting Google.
    private static UsageSnapshot ParseGemini(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var bridge = typeof(GeminiUsageProvider).Assembly.GetType("VibeMeters.Services.GeminiCliBridge", throwOnError: true)!;
        return (UsageSnapshot)bridge.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [doc.RootElement])!;
    }
    private static UsageSnapshot ParseAntigravity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var bridge = typeof(GeminiUsageProvider).Assembly.GetType("VibeMeters.Services.AntigravityUsageBridge", true)!;
        return (UsageSnapshot)bridge.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [doc.RootElement])!;
    }
    private static ProcessStartInfo? CreateCliStartInfo(string name, params string[] arguments)
    {
        var helper = typeof(LoginService).Assembly.GetType("VibeMeters.Services.OfficialCliProcess", throwOnError: true)!;
        return (ProcessStartInfo?)helper.GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [name, arguments]);
    }
    private static void WithCliFixture(string extension, Action<string, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VibeMeters CLI ' & $(literal) " + Guid.NewGuid().ToString("N"));
        var name = "vibemeters-test-cli-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, name + extension);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS");
        var originalNodePath = Environment.GetEnvironmentVariable("NODE_PATH");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "Fixture only. Never execute.");
            Environment.SetEnvironmentVariable("PATH", directory);
            Environment.SetEnvironmentVariable("NODE_OPTIONS", "--fixture-option");
            Environment.SetEnvironmentVariable("NODE_PATH", "fixture-node-path");
            test(name, path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("NODE_OPTIONS", originalOptions);
            Environment.SetEnvironmentVariable("NODE_PATH", originalNodePath);
            File.Delete(path); Directory.Delete(directory);
        }
    }
    private static void AssertHiddenCli(ProcessStartInfo start)
    {
        True(!start.UseShellExecute); True(start.CreateNoWindow); Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        True(start.RedirectStandardInput); True(start.RedirectStandardOutput); True(start.RedirectStandardError);
        Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), start.WorkingDirectory);
        Equal("1", start.Environment["NO_COLOR"]);
        True(!start.Environment.ContainsKey("NODE_OPTIONS")); True(!start.Environment.ContainsKey("NODE_PATH"));
    }
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

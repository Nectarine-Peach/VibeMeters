using System;
using VibeMeters.Models;

namespace VibeMeters.Services;

public static class UsageRefreshText
{
    private static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", Localizer.Culture);
    private static string Label(string value) => Localizer.T(value);

    public static string Summary(UsageSnapshot snapshot)
    {
        if (snapshot.WaitReason is UsageWaitReason.RateLimited or UsageWaitReason.AuthenticationBackoff or UsageWaitReason.FailureBackoff)
        {
            var reason = snapshot.WaitReason switch
            {
                UsageWaitReason.RateLimited => "요청 제한 대기",
                UsageWaitReason.AuthenticationBackoff => "인증 대기",
                _ => "오류 후 대기"
            };
            return Label(reason) + (snapshot.NextQueryAt is { } next ? " · " + Time(next) : " · " + Label("로그인 확인 필요"));
        }
        var observed = snapshot.ObservedAt ?? (snapshot.Status == UsageStatus.Ready ? snapshot.CheckedAt : (DateTimeOffset?)null);
        var text = observed is { } at ? Label("확인") + " " + Time(at) : Label("아직 조회 전");
        if (snapshot.NextAutomaticAt is { } automatic) text += " · " + Label("다음 자동") + " " + Time(automatic);
        else if (snapshot.IsCached) text += " · " + Label("저장된 값");
        return text;
    }

    public static string Details(UsageSnapshot snapshot)
    {
        var observed = snapshot.ObservedAt ?? (snapshot.Status == UsageStatus.Ready ? snapshot.CheckedAt : (DateTimeOffset?)null);
        var result = Label("사용량 관측") + ": " + (observed is { } at ? at.ToLocalTime().ToString("MM/dd HH:mm:ss", Localizer.Culture) : "—");
        result += "\n" + Label("조회 확인") + ": " + snapshot.CheckedAt.ToLocalTime().ToString("MM/dd HH:mm:ss", Localizer.Culture);
        if (snapshot.IsCached) result += "\n" + Label("저장된 결과 · 새 요청 없음");
        if (snapshot.NextQueryAt is { } next && snapshot.WaitReason != UsageWaitReason.None)
            result += "\n" + Label(snapshot.WaitReason == UsageWaitReason.SuccessCache ? "기본 성공 캐시 만료" : "보호 대기 종료 · 재조회 가능") + ": " + Time(next);
        if (snapshot.NextAutomaticAt is { } automatic) result += "\n" + Label("다음 자동 조회") + ": " + Time(automatic);
        return result;
    }
}

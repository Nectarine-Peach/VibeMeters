# Final verification / 최종 검증 — v1.4.4 · 2026-10-26

CLI 설치 기능을 추가하기 전의 변경 상태에서 회귀 **139개**, 실행 창·배치·복원 검사 **239개**를 통과했습니다. 새 창 검사는 합성 Codex Business 개인 한도·Claude 월 추가 사용·비율 미제공 상태의 실제 WPF 렌더링을 포함합니다. [당시 실행 창 검사 원문](docs/verification/v1.4.4-window-checks.txt).

이후 로그인용 공식 CLI 설치 안내·동의·진행·취소·재탐색 기능을 추가했습니다. **사용자 요청에 따라 추가 이후의 회귀 및 UI 스모크는 실행하지 않았습니다.** 아래 139/239 성공은 이 기능의 검증 증거가 아닙니다. 최종본은 Release 컴파일/패키징과 기존 Gemini 계정 없는 내장 runtime 진단만 수행하며, 이 PC에 CLI를 설치하거나 계정 로그인을 실행하지 않았습니다. [설치 범위와 미검증 항목](docs/cli-installation.md).

- `dotnet run --project tests/VibeMeters.Tests.csproj -c Release -r win-x64 -p:SelfContained=true`: 139 passed, 0 failed. 기본 dotnet 환경은 SDK 10.0.400 / runtime 10.0.11이므로 .NET 8을 자체 포함해 검사했습니다.
- `bin/Release/net8.0-windows/win-x64/VibeMeters.exe --smoke-test`: 239 passed, 0 failed. 별도 mutex·메모리 설정·합성 데이터로 실행하며 실제 제공자/인증 파일 감시와 사용자 설정 쓰기를 사용하지 않았습니다. Business 및 비율 미제공 캡처를 시각적으로 확인했습니다.
- 배포는 Release win-x64 .NET self-contained를 유지하고, Gemini core 0.65.0 / esbuild 0.25.12 / 기존 배포와 동일한 Node 24.22.0을 사용합니다. `scripts/publish.ps1`은 node_modules 없이 저장소 밖·빈 홈·네트워크/브라우저 차단 상태에서 SDK 로드·mock project/quota·미로그인 상태와 번들 해시를 검사한 뒤 ZIP을 만듭니다.
- 패키징용 npm은 PATH의 설치 도구이며 고정하지 않았습니다. npm lifecycle scripts는 차단하고 의존성은 lock으로 복원합니다. 배포 Node는 버전·해시를 고정합니다. [패키징 범위](docs/packaging.md).

실제 계정 재로그인·SSO 승인·브라우저 실패 복구·Gemini 실계정 조회·OS 재부팅은 이번 릴리스 검사에 포함하지 않았습니다. 캡처는 WPF RenderTargetBitmap이며 데스크톱 전체·OS 트레이 픽셀 검증은 아닙니다. 아래 회사 PC 관측과 이전 버전 검증은 각각의 당시 기록입니다.

---
# 회사·개인 계정 수정 — 2026-10-26

원인: 두 서비스의 로그인은 유효했지만 기존 파서가 회사 계정의 개인 한도·추가 사용 응답을 지원하지 않았습니다. Codex는 `spend_control.individual_limit.remaining_percent`와 초기화 시점을, Claude는 `extra_usage.utilization`을 표시하도록 수정했습니다. 기존 개인 구독 창과 계정 전환 후 캐시 제거·429 대기 보존도 유지합니다.

정정: 초기 조사에서 Codex의 `spend_control` 하위 개인 한도를 확인하지 않고 ‘비율 미제공’으로 판단한 결론은 철회했습니다. 추가 읽기 전용 확인에서 API 계정이 저장된 CLI 계정과 일치했고, 개인 한도 잔여 **48%** 및 `reset_at`을 확인했습니다. 비율이 실제로 없는 크레딧형 응답에 대한 연결 상태 표시는 유지합니다. 개인 한도를 5시간·주간 한도로 오인하거나 금액 문자열로 비율을 만들어내지 않습니다.

진단: Codex CLI 0.163.0 / Claude Code 2.1.306 설치, 기존 회사 OAuth 인증의 두 API HTTP 200 확인. 인증 파일·토큰 변경과 자체 갱신은 수행하지 않았습니다. 로그인 원시 출력은 저장하지 않고 네트워크·인증서·SSO 오류 및 401/403/407을 구분합니다.

최종 회귀 검증: .NET SDK 8.0.427로 `dotnet run --project tests/VibeMeters.Tests.csproj -c Release` 실행, **139개 통과·0개 실패**. 개인 한도 실제 응답 구조·remaining 우선·used fallback·절대/상대 초기화·구독창 공존·잘못된 값 처리와 기존 개인/회사 계정 회귀를 포함합니다. 컴파일 오류·경고 메시지는 없었습니다. 이전 단계의 207개 실행 창 검사는 통과했으나 개인 한도 수정 이후 전체 창 검사를 반복하지 않았습니다. `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/VibeMeters-account-fix-v2-win-x64`가 성공했습니다. 최신 배포 EXE로 앱을 교체 실행한 뒤 Windows UI Automation으로 Codex 개인 한도 잔여 48%와 Claude 월추가 비율 표시를 확인했습니다. 실행 파일은 `dist/VibeMeters-account-fix-v2-win-x64/VibeMeters.exe`입니다.

실제 개인/회사 계정 재로그인·SSO 승인 및 브라우저 실패 복구 실동작은 자동 실행하지 않았습니다. [사용 범위와 제약](docs/account-usage.md).
---
# Final verification / 최종 검증 — v1.4.3 · 2026-10-24

**자동 회귀 107개, 실행 창·배치·복원 검사 207개 통과. 빌드 경고·오류 0개.**

- 창 크기·음수 모니터 좌표·표시 상태의 JSON 저장 및 복원, 이전 설정의 기본값 호환을 검사했습니다.
- 실행한 274×174 WPF 창을 저장한 뒤 별도의 새 창을 생성해 크기와 위치가 복원되는 것을 확인했습니다. 최소화 중에는 정상 창의 RestoreBounds를 보존합니다.
- 숨김 CLI 실행 옵션, 표준 입출력 리디렉션, npm 경로·인수 이스케이프, 취소 처리를 격리된 테스트로 검사했습니다. 실제 OAuth 인증을 시작하거나 기존 로그인을 변경하지 않았습니다.
- 창 이동·크기 변경 후 400ms 저장, 종료·Windows 세션 종료·절전 저장 경로를 구현했습니다. **실제 Windows 재부팅·종료는 수행하지 않았습니다.**
- [실행 창 검사 원문](docs/verification/v1.4.3-window-checks.txt). 기존 화면 구성 검사와 캡처 방식·제약은 아래 v1.4.2 기록과 같습니다.

**107 regression checks and 207 shown-window/layout/restore checks passed; zero build warnings or errors.** Tests cover geometry and visibility serialization, reconstruction of a shown window, minimized RestoreBounds preservation, hidden CLI process configuration, argument quoting and cancellation. Actual OAuth approval and OS reboot/shutdown were not exercised. App-render capture limitations remain as described below.

---
# Final verification / 최종 검증 — v1.4.2 · 2026-10-24

**97 regression checks passed; 197 shown-window layout/state checks passed. Zero failures.** Release build completed with zero warnings and zero errors. Captures were visually inspected for standard, mini, minimum, light-theme, settings, and all-service layouts.

**자동 회귀 97개와 실행 창 배치·상태 검사 197개를 모두 통과했습니다.** 배포 구성 빌드 경고·오류는 0개입니다. 표준·미니·최소 창, 라이트 테마, 설정, 세 서비스 화면의 캡처도 직접 확인했습니다.

## Window matrix / 크기별 결과

Tested on Windows x64 at **125% scaling (120 DPI)**. Sizes below are logical pixels; Windows can round fractional device pixels by up to one logical pixel.

| Scenario / 구성 | Logical size / 논리 크기 | Result / 결과 |
| --- | --- | --- |
| Standard / 표준 | 360 × 267 | Pass / 통과 |
| Mini / 미니 | 274 × 174 | Pass / 통과 |
| Minimum / 최소 | 220 × 140 | Pass / 통과 |
| Wide / 가로 | 600 × 300 | Pass / 통과 |
| All services / 세 서비스 | 360 × 394 | Pass / 통과 |
| All services minimum / 세 서비스 최소 | 220 × 160 | Pass / 통과 |

All sizes keep vertically stacked quota rows, with label → central meter → right-aligned percentage. Claude's five-hour and weekly quotas remain separate rows. Card/content bounds, inline alignment, all visible quotas, no scroll viewers, and removal of initial badges were checked. More services increase the minimum height instead of hiding rows.

미니·최소 창에서도 항목명 → 중앙 막대 → 오른쪽 수치 구성을 유지하며 Claude의 5시간·주간 한도를 세로 두 줄로 표시합니다. 카드와 내용의 경계, 정렬, 모든 한도 표시, 스크롤·서비스 배지 제거를 검사했습니다. 한도가 많으면 숨기지 않고 최소 높이를 늘립니다.

## Scope / 검증 범위

- Provider/parser/settings regressions include Gemini bridge JSON, invalid data, authentication states, 429 cooldown preservation, display modes, and saved preferences.
- Shown-window checks exercise real WPF controls and event handlers: pin, taskbar state, minimize, hide/restore, separate settings, theme selections, per-service percentages, cached values, missing-data placeholders, and login-event dispatch.
- **Captures use synthetic demo data**, with no provider instances, credential watchers, account requests, or user-settings writes. [Raw window-check report](docs/screenshots/verification.txt).
- Windows Graphics Capture failed with `FrameArrived timed out` / `window capture timed out`, and input geometry was unavailable. The fallback captures the **shown application's real WPF client visual** using `RenderTargetBitmap` at its monitor DPI, including the window background. These are app-rendered captures, not AI mockups or desktop screenshots. Native compositor borders, tray/taskbar pixels, and physical mouse input are outside this result.
- Live provider availability, login completion, other DPI settings, and multi-monitor transitions were not tested in this run.

실행 창의 실제 WPF 렌더링을 캡처한 결과이며 AI 시안이 아닙니다. Windows 화면 캡처 도구의 시간 초과로 데스크톱 전체·OS 트레이 픽셀·실제 마우스 조작은 검증하지 못했습니다. 실제 계정 로그인·서버 조회와 다른 배율·모니터 이동도 이번 검증에 포함하지 않았습니다.

## Reproduce / 재현

```powershell
dotnet run --project tests/VibeMeters.Tests.csproj -c Release
dotnet build -c Release
dotnet .\bin\Release\net8.0-windows\VibeMeters.dll --smoke-test
```

The isolated window check exits with code 0 on success and 1 on failure; PNG captures and `smoke-test.txt` are under `bin/Release/net8.0-windows/artifacts/`.

---
# v1.4.2 구현 기록 — 미니 창의 카드 구성 통일

미니 전용 가로 분할 및 막대 하단 배치를 제거하고, 모든 크기에서 같은 카드 렌더러를 사용합니다. 큰 창의 서비스 제목·구분선·항목명·중앙 막대·오른쪽 비율 구성을 유지하며 가용 폭과 높이에 따라 글자와 여백을 조절합니다. 한도가 많으면 행을 숨기지 않고 필요한 최소 높이를 확보합니다.

사용자의 기존 지시에 따라 테스트·스크린샷·실행 화면 검증은 생략합니다. 배포용 컴파일과 앱 실행만 진행합니다.

---
# v1.4.1 구현 기록 — 시안 비례 일치 수정

기존 초소형 저장 크기로 인해 시안 대신 압축 배치가 열리던 경로를 변경했습니다. 업데이트 첫 실행에 표준 폭 360과 선택 서비스에 맞는 카드 높이를 적용하고, 항목명·중앙 막대·오른쪽 수치를 한 줄에 둡니다. 서비스의 C/A/G 배지는 사용자 요청으로 제거합니다.

조회가 제한되어 값이 없는 경우 실제 비율을 만들어 넣지 않고 대시와 빈 막대를 사용해 카드 비례를 유지합니다. 자동 테스트와 실행 화면 검증은 기존 사용자 지시에 따라 생략하며 배포용 컴파일과 실행만 진행합니다.

---
# v1.4.0 구현 기록 — ImageGen 시안 적용

내장 ImageGen으로 다크·라이트·작은 창 디자인 보드를 생성하고 docs/design/에 원본 시안과 정확한 프롬프트를 저장했습니다. 이 이미지는 디자인 참고 자료이며 앱 실행 화면이 아닙니다.

WPF UI는 이미지의 배지·패널·미터·수치·상단 아이콘 언어를 적용하도록 수정했습니다. 기존 기능, 테마, 서비스 선택, 표시 기준과 반응형 동작은 유지합니다. 이미지 자체를 UI 배경으로 사용하지 않습니다.

사용자의 기존 지시에 따라 테스트, 앱 스크린샷 및 런타임 검증은 생략합니다. 배포용 컴파일과 앱 실행만 진행합니다. 이하 검증 기록은 각각의 이전 버전에 해당합니다.

---
# v1.3.1 구현 기록 — 검증 생략

사용자 요청에 따라 이번 변경도 테스트·화면 렌더링·실제 사용량 조회 검증은 실행하지 않았습니다. 배포용 컴파일과 앱 실행만 진행합니다.

구현: 작은 화면의 서비스 영역·수치 정렬·중립 카드·상태 점·줄바꿈 툴팁, Claude 최소 120초 조회 캐시, 429 후 5/10/20/30/60분 대기와 서버 Retry-After 준수. 이 간격은 공급자의 공식 보장 수치가 아닌 앱의 보수적인 조회 정책입니다.

---
# v1.3 구현 기록 — 검증 생략

사용자 요청에 따라 테스트, UI 조작, 스크린샷 및 실제 Gemini 할당량 조회 검증을 실행하지 않았습니다. 배포용 컴파일과 앱 실행만 진행합니다.

구현: 앱 전용 공식 Gemini CLI 코어 0.65.0과 Node를 통한 숨겨진 모델별 할당량 조회, 5분 캐시, 브라우저 로그인, 시스템·다크·라이트 테마, 서비스별 남은 양/사용한 양, 상단 새로고침·설정, 최근 확인 문구 및 수동 미니 전환 제거.

Gemini는 런타임 폴더를 함께 배포하며 공식 코어 내부 인터페이스에 의존합니다. 실제 계정의 성공 응답이나 모든 테마·창 크기의 동작을 이번 변경에서 확인한 것은 아닙니다. 아래 검증 결과와 해시는 각 이전 버전에만 해당합니다.

---
# v1.2 구현 기록 — 검증 생략

사용자 요청에 따라 이번 변경에서는 테스트, 스크린샷 검토, UI 조작 및 사용량 연동 검증을 수행하지 않았습니다. 배포용 컴파일과 앱 실행은 요청 범위에 포함되어 진행합니다.

구현 항목: 메인 스크롤 제거 및 높이에 따른 밀도 조절, 표시 서비스 선택, 별도 다크 설정 창, 작업표시줄 진행 막대 제거, 선택 서비스만 조회·트레이 표시, Gemini 공식 CLI `/stats` 열기.

Gemini는 공식 CLI에서 사용량을 표시하며 위젯에 잔여 비율을 자동으로 가져오는 기능은 제공하지 않습니다. 로그인·사용량 창 실행 경로는 공식 문서와 소스로 구현했고 실제 동작 검증은 생략했습니다.

아래 결과와 해시는 이전 v1.1에 해당합니다.

---
# 최종 검증 — v1.1 / 2026-10-24

Windows x64, .NET SDK 8.0.426, 자체 포함 Release 배포본을 검증했습니다.

| 항목 | 결과 |
|---|---|
| Release 빌드·win-x64 publish | 경고 0, 오류 0 |
| 파서·HTTP·인증·설정 회귀 테스트 | 72 통과, 0 실패 |
| 배포 EXE의 WPF 스모크 테스트 | 16 통과, 0 실패 |
| 창 크기별 렌더링 | 220×140, 260×180, 340×320, 600×300 |
| 미니 전환 | 240×148 전환, 이전 일반 크기 및 수동 초소형 크기 복원 |
| 창 기능 | 항상 위, 작업표시줄, 최소화, 트레이 숨김·복원, 설정 패널 |
| 인증 오류 UI | 일반·미니 화면의 로그인 버튼과 경고 상태 |
| 실제 Codex 조회 | 정상 응답, 현재 계정 주간 한도 표시 |
| 실제 Claude 확인 | 초기 만료 인증 확인 후 사용자 재로그인 반영. 최종 앱에서 5시간 100%, 주간 55% 남음 정상 표시 |

로그인 복구는 새 토큰이 이전 401·403·429 캐시를 해제하는지, 계정 변경 때 이전 수치가 사라지는지, 만료 토큰을 새 토큰으로 교체하면 정상 조회되는지를 격리된 인증 파일과 HTTP 응답으로 검증했습니다. 이후 사용자가 재로그인한 실제 Claude 계정도 실행 중인 앱에 반영되어 정상 수치와 초기화 시간이 표시되는 것을 Windows UI Automation으로 확인했습니다. 앱 로그인 버튼은 설치된 공식 CLI의 대화형 인증 창을 열도록 연결했습니다. 사용자의 실제 계정 인증 절차는 대신 수행하지 않았습니다.

## 자원 측정

| 환경 | 작업 집합 | 전용 메모리 | 유휴 CPU 증가 |
|---|---:|---:|---:|
| 소프트웨어 렌더링 데모, 숨김 | 81.0MiB | 33.3MiB | 15초간 0ms |
| 최종 배포본, 실제 계정·사용자 설정 | 227.1MiB | 117.4MiB | 10초간 0ms |

실제 계정 표본은 자식 프로세스 0개, 스레드 35개였습니다. 데모와 실제 계정은 인증·TLS·Windows 및 창 상태가 달라 같은 조건의 비교가 아닙니다. 짧은 유휴 측정이며 모든 PC의 메모리나 장시간 누수 없음을 보장하지 않습니다. 작은 정적 창에 불필요한 GPU 장치를 만들지 않도록 소프트웨어 렌더링을 사용하고, 화면·트레이 아이콘은 갱신 이벤트에서만 그립니다. 로그인 중에는 사용자가 요청한 공식 CLI 터미널이 별도로 열립니다.

## 배포본

- 실행 파일: `dist/VibeMeters-win-x64/VibeMeters.exe`
- ZIP: `dist/VibeMeters-win-x64.zip`
- 이미지 및 16개 UI 결과: `dist/VibeMeters-win-x64/artifacts/`
- EXE SHA-256: `DD402DEBD0CB24A15BE69D802B523C9C7386EA4750F4325D993518139EEAA4C0`

사용자 계정의 토큰을 기록·복사·수정하지 않았습니다. 실제 Windows 재부팅 자동시작, 절전·잠금 전환, 모든 다중 모니터 및 DPI 조합은 이번 세션에서 직접 재현하지 않았습니다. 관련 설정·이벤트 처리 코드는 포함되어 있습니다.






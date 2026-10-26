# v1.4.6 문서 포함 배포 — 2026-10-26

최신 한/영 README와 네 서비스 이미지 9종, 캡처 방법·파일별 해시 기록을 배포 ZIP에 포함하는 버전입니다. v1.4.5의 애플리케이션 동작 코드는 유지하고 버전 메타데이터와 릴리스 문서만 갱신합니다. 기존 `scripts/publish.ps1`의 루트 README 복사와 `docs` 전체 복사를 사용합니다.

배포 검증 범위는 최종 커밋의 Release win-x64 self-contained 컴파일·패키징, 계정 없는 Gemini 번들 내장 진단, EXE 버전/커밋 일치, ZIP 전체 파일 및 원본 README/docs의 SHA-256 일치, 이미지 9종 포함과 README 상대 링크 해석, 원격 tag·published/latest·asset 크기/digest 확인입니다. 최종 배포 Node는 24.22.0을 유지합니다.

사용자 요청에 따라 회귀 테스트·UI 스모크·새 화면 캡처·실제 CLI 설치·실계정 조회/로그인·로컬 앱 교체를 실행하지 않습니다. 아래 문서 캡처와 개발 시점 테스트는 각 당시 기록입니다.

```powershell
.\scripts\publish.ps1 -Node .\dist\comparison\original-node\node.exe -OutputDirectory .\dist\releases\v1.4.6\VibeMeters-win-x64
git diff --check
```

---
# README 문서 캡처 — 2026-10-26

사용자가 승인한 스크린샷 갱신 범위에서 현재 v1.4.5 DLL의 실제 WPF 컨트롤을 합성 데이터로 렌더링했습니다. 개인 Gemini Antigravity(5시간·주간·Argon)와 Grok Build 주간을 포함한 네 서비스의 표준/라이트/미니/최소 폭/설정 이미지 9종을 생성하고 직접 열어 시각적으로 확인했습니다. [캡처 방법·크기·한계](docs/screenshots/README.md).

앱 시작·제공자·인증 감시·사용자 설정 저장을 사용하지 않는 임시 캡처 하네스만 빌드·실행했습니다. 전체 회귀/UI 스모크, 실계정 조회·설치·로그인, 로컬 실행 앱 교체는 수행하지 않았습니다. 이 기록은 아래 릴리스 검증 범위를 확대하거나 과거 테스트를 다시 통과했다고 주장하는 근거가 아닙니다. 버전·ZIP·Release 태그도 변경하지 않습니다.

---
# v1.4.5 릴리스 검증 범위 — 2026-10-26

이번 릴리스의 배포 검사는 **최종 커밋의 Release win-x64 self-contained 컴파일·패키징**, **사용자 홈·네트워크·브라우저를 차단한 Gemini 번들 내장 진단**, **ZIP/버전/원격 태그·asset 해시 확인**으로 제한합니다. Grok ACP에 전달하는 클라이언트 버전은 어셈블리 버전을 사용합니다. 배포 Node는 기존 24.22.0을 유지합니다.

사용자 요청에 따라 이번 릴리스 작업에서 **회귀 테스트·UI 스모크·실제 CLI 설치·실계정 조회·로그인·로컬 앱 교체를 실행하지 않습니다.** 아래의 194/284 및 162/5/2 성공과 실계정/UI 관측은 각각의 개발 시점 기록이며, 이번 릴리스에서 다시 검증했다고 주장하지 않습니다. 특히 아래 PID·설정·계정 비율은 현재 상태를 뜻하지 않습니다.

최종 커밋 후 배포 명령:

```powershell
.\scripts\publish.ps1 -Node .\dist\comparison\original-node\node.exe -OutputDirectory .\dist\releases\v1.4.5\VibeMeters-win-x64
git diff --check
```

회귀 스위트와 `account-status.test.mjs`는 실행하지 않으며, 패키징에 포함된 `verify-runtime.mjs`만 계정 없이 실행합니다. 기존 Release/tag를 덮어쓰지 않고 새 v1.4.5 ZIP과 SHA-256을 게시합니다.

---
# 개발 시점 검증 기록 — 2026-10-26 Grok Build 구독 사용량

공식 Grok Build CLI 연동 구현·검증·로컬 적용을 완료했습니다. 일반 조회는 `grok agent --no-leader stdio`에서 ACP `initialize`와 `_x.ai/billing` 두 요청만 사용합니다. 모델·세션·authenticate·브라우저 로그인을 자동 실행하지 않으며 앱은 인증 파일 내용을 읽지 않습니다. 기존 CLI 1.0.40을 재사용했고 확장·Native Messaging·새 의존성을 설치하지 않았습니다.

- **실계정 성공:** 사용자 공식 CLI 로그인 후 새 auth 파일 생성의 메타데이터를 확인하고 2026-10-26 **14:44:27 KST**에 실제 provider를 한 번 조회했습니다. `Ready`, `isUnifiedBillingUser=true`, **공유 주간 사용 1% / 남음 99%**, 초기화 **2026-10-30 10:16:45 KST**를 확인했습니다. 초기 인증 필요 관측 후 동일 만료 인증으로 진단을 반복하지 않았고, 새 로그인 뒤 재검증했습니다. 원래 공식 웹 화면의 SuperGrok 주간 1%·초기화 시점과 일치합니다. 웹 Grok Bot 7%는 CLI 응답에 없어 표시하지 않습니다.
- **실제 앱 표시:** Windows Computer Use 접근성 텍스트에서 `Grok / 남음 / 공유 주간 / 진행률 99 / 99%`를 확인했습니다. 같은 창의 `Gemini · Antigravity` 5시간·주간·Argon 각 100%도 유지됩니다. 실제 UI 증거는 `verification/ui-accessibility.txt`, 비밀값 없는 provider 결과는 `verification/real-account-quota.json`입니다. hover 툴팁과 네이티브 픽셀 캡처는 별도 실행하지 않았습니다. 초기화 값은 실제 응답과 파서 회귀로 확인했습니다.
- **최종 회귀 194/194:** 비율/기간/공유 대 Build 범위, Cent 객체 기간 fallback(금액 비율 계산 없음), 실제 ACP error.data의 429/401 분류, JSON-RPC ID·출력 제한·대화형 요청 거부, 캐시/계정 변경/429 대기, 공식 auth 경로 override 및 메타데이터 감시를 포함합니다. 별도 정적 보안 재검토의 주요 finding도 수정 후 해소 확인했습니다.
- **배포·UI 검사:** Release win-x64 self-contained publish 성공. 새 stage의 Gemini 번들 오프라인 **2/2**, 기존 WPF smoke **284/284** 통과했습니다. 4개 서비스의 정상/최소 크기, 설정 저장·복원 및 `Build 주간` 한 행을 확인했습니다. `artifacts/window-four-services-grok-dark.png` 등은 **합성 데이터 렌더**이며 실제 계정 수치 증거와 구분합니다. 이전 확장 검토안의 파일·검증은 최종 코드에 포함하지 않습니다.
- **로컬 적용:** `D:\Applications\VibeMeters\VibeMeters.exe`, 최종 PID **12200** 한 개. stage와 실행 EXE SHA256은 **ac924f7c1820fffad41708e300a69e7d5dc24e3bd2d62bea347f08b7a8cdcd8e**로 일치합니다. 기존 EXE·설정은 `dist/VibeMeters-before-grok-build-20261026/`에 보관했습니다. 사용자 요청에 따라 `ShowGrok=true`만 추가했고 기존 설정은 보존했습니다. 4카드 최소 높이만 174→209로 자동 조정됐습니다. 실제 UI 확인용 임시 `ShowInTaskbar=true`는 원래 **false**로 복원했고 UI 전후 설정 차이가 없음을 확인했습니다.
- **제약:** 실제 429·회사 권한 오류를 유발하거나 계정 전환을 반복하지 않았습니다. 해당 경계는 fixture 회귀로 검증했습니다. CLI의 공개 소스상 billing extension 계약을 사용하므로 향후 CLI/서버 변경 시 호환성 확인이 필요합니다. [최종 범위·출처](docs/grok-usage.md).

실행한 주요 명령(작업 디렉터리 `D:\Repository\VibeMeters`):

```powershell
dotnet run --project tests\VibeMeters.Tests.csproj -c Release -r win-x64 --self-contained true
dotnet run --project tests\VibeMeters.Tests.csproj -c Release -r win-x64 --self-contained true --no-build -- --grok-live-check
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\VibeMeters-grok-build-win-x64
& D:\Applications\VibeMeters\runtime\gemini-quota\node.exe runtime\gemini-quota\verify-runtime.mjs dist\VibeMeters-grok-build-win-x64\runtime\gemini-quota
# 기존 격리 smoke 경로: 메모리 설정·합성 데이터 사용
dist\VibeMeters-grok-build-win-x64\VibeMeters.exe --smoke-test
git diff --check
```

---

# 검증 완료 — 2026-10-26 Gemini 개인 계정 조회 복구

- 원인: 실제 계정의 Gemini CLI `loadCodeAssist`가 `free-tier / UNSUPPORTED_CLIENT`를 반환했습니다. Google의 개인 계정 Gemini CLI 지원 종료에 따라 공식 Antigravity CLI 1.4.7의 `agy -p /usage --output-format json`으로 조회하도록 연결했습니다. 회사 currentTier/paidTier/프로젝트가 유효하면 기존 Code Assist 경로를 유지합니다. 별도 설치·새 로그인·계정/Cloud 프로젝트/과금 변경은 하지 않았습니다. 공식 updater는 이미 최신 1.4.7이라고 응답하여 바이너리도 변경하지 않았습니다.
- 최종 .NET 회귀: `dotnet run --project tests/VibeMeters.Tests.csproj -c Release -r win-x64 --self-contained true` — **162 passed, 0 failed**. 일반 `dotnet run`은 이 PC에 .NET 8 실행 호스트가 없어 실행되지 않았으며, 기존 캐시의 self-contained 런타임으로 검증했습니다. 파서·공유 한도·타 제품 제외·비율 경계·명령 응답 검증·계정 전환/429·외부 로그인 후 복구를 포함합니다.
- 공식 Node 24.22.0으로 `node --test runtime/gemini-quota/account-status.test.mjs` — **5 passed**. `build.mjs` 후 `verify-runtime.mjs` — **2 isolated checks passed**, 번들 해시 확인, 사용자 홈·네트워크·브라우저 차단 상태 확인. .NET Release win-x64 self-contained publish 성공. 최종 EXE SHA256: `5a24cf963ddbb9fd08ec3fcc355727faef641e52881da1b5b90a87684552025b`.
- 실계정: 2026-10-26 13:47 KST 공식 `/usage`는 `SUCCESS`, `num_turns=0`, `total_tokens=0`으로 응답했습니다. Gemini 공유 5시간·주간 및 별도 Gemini 4 Argon 주간이 각각 **잔여 100%**였습니다. 이 조회의 초기화 시점은 각각 10/26 18:47 KST, 11/2 13:47 KST, 11/2 13:47 KST였습니다. 미사용 한도의 reset은 관측 요청마다 이동할 수 있어 고정 시각으로 추정하지 않습니다.
- 실제 실행 창의 Windows Computer Use 접근성 텍스트에서 **Gemini · Antigravity**, **5시간 100% / 주간 100% / Argon 100%** 및 진행률 값을 확인했습니다. 최종 추가 수정은 인증 오류 후 새로고침 캐시 정책뿐이며, 위 UI 렌더 구현은 동일합니다. 마지막 수정 이후 162개 회귀와 publish를 다시 통과했습니다.
- 캡처는 `FrameArrived timed out`, 재선택 후 `window capture timed out`으로 실패했습니다. 픽셀·hover 툴팁은 미검증입니다. 초기화 값은 실제 JSON과 파서 회귀로 확인했으며 화면 툴팁을 직접 확인했다고 주장하지 않습니다. 트레이 전용 창을 접근성 도구에 노출하기 위해 `ShowInTaskbar`를 잠시 켜고 검증 후 원래 `false`로 복원했습니다. 다른 설정은 보존했고 3개 한도를 표시하기 위한 최소 높이만 170→174로 자동 조정되었습니다.
- 로컬 적용: `D:\Applications\VibeMeters\VibeMeters.exe`와 번들을 교체하고 당시 PID **10532** 한 개를 확인했습니다. 기존 EXE·bundle·설정은 `dist/VibeMeters-before-gemini-antigravity-20261026/`에 보관했습니다. 비밀값 없는 실제 응답/접근성 증거는 `dist/VibeMeters-gemini-antigravity-win-x64/verification/real-account-quota.json`, `ui-accessibility.txt`에 있습니다. 실제 로그인·계정 전환·서버 429 유발은 실행하지 않았고 해당 경계는 회귀 테스트로 검증했습니다.

실행한 빌드·검사 명령(작업 디렉터리 `D:\Repository\VibeMeters`):

```powershell
dotnet run --project tests\VibeMeters.Tests.csproj -c Release -r win-x64 --self-contained true
& D:\Applications\VibeMeters\runtime\gemini-quota\node.exe --test runtime\gemini-quota\account-status.test.mjs
& D:\Applications\VibeMeters\runtime\gemini-quota\node.exe runtime\gemini-quota\build.mjs dist\VibeMeters-gemini-antigravity-win-x64\runtime\gemini-quota dist\VibeMeters-gemini-antigravity-win-x64.gemini-bundle-meta.json
& D:\Applications\VibeMeters\runtime\gemini-quota\node.exe runtime\gemini-quota\verify-runtime.mjs dist\VibeMeters-gemini-antigravity-win-x64\runtime\gemini-quota
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\VibeMeters-gemini-antigravity-win-x64
git diff --check
```

---
아래는 각 변경 시점의 역사 기록입니다. 최신 Gemini 검증 결과는 위 절을 따릅니다.

# 이전 검증 — v1.4.4 · 2026-10-26

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

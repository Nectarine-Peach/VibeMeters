# 영문 현지화 이미지 — v1.4.7 · 2026-10-26

`window-standard-en-dark.png`, `window-standard-en-light.png`, `window-mini-en.png`, `settings-en.png`, `window-cached-en.png`, `cli-install-en.png`, `cli-install-progress-en.png`는 실제 v1.4.7 WPF 컨트롤의 합성 렌더입니다. 영문 화면과 설치 안내/진행 문구 예시를 직접 열어 가독성을 확인했습니다. 설치 이미지는 대화상자 클라이언트 콘텐츠이며 실제 설치를 실행한 결과가 아닙니다.

캡처 전용 하네스에서 한국어→English→한국어→English를 선택하고 캐시된 사용량의 레이블·요청 제한 툴팁·설치 상태가 원문으로 복원되는 것을 확인했습니다. 일반 레이블 폭도 42→52→42→52로 복원됐고 제공자 원문 한도 이름은 변하지 않았습니다. [전환·캡처 기록](localization-capture.json).

전체 회귀/UI 스모크를 실행하지 않았으며 실제 제공자 조회·설치·로그인·사용자 설정 쓰기 없이 메모리 설정과 합성 상태만 사용했습니다. 아래 한국어 v1.4.5 이미지 기록은 당시의 별도 캡처입니다.

---
# 한국어 README 이미지 — 2026-10-26

현재 README의 대표·표준·라이트·최소 폭·설정 이미지는 v1.4.5 커밋 `a52ee12e580a795a8cf623326634276a4c5b6192`의 실제 WPF 컨트롤을 사용한 **합성 데모 렌더**입니다. `Gemini · Antigravity`와 `Grok / Build 주간`의 출처·범위를 명확하게 표시합니다. 모든 이미지를 실제로 열어 가독성·잘림·표시 범위를 확인했습니다.

| 이미지 | 논리 크기 |
| --- | --- |
| `window-standard-dark.png`, `window-standard-light.png`, `window-all-services-dark.png` | 360×551 |
| `window-mini-dark.png`, `window-mini-light.png` | 274×300 |
| `window-minimum-dark.png`, `window-all-services-minimum-dark.png` | 220×223 |
| `window-wide-dark.png` | 1040×280 |
| `settings-dark.png` | 410×710 |

96 DPI에서 기존 `VisualVerification.Capture`의 `RenderTargetBitmap` 경로를 재사용했습니다. 네 서비스 모두 표시했으며, 좁은 창은 한도 행을 숨기는 대신 높이가 늘어납니다. [파일별 캡처 정보](capture-2026-10-26.json).

데이터는 Codex 남음 73%/61%, Claude 80%/55%, 개인 Gemini Antigravity 5시간 83%·주간 68%·Argon 42%, Grok Build 주간 68%로 지정한 예시입니다. 실제 계정 비율이나 서버 상태를 나타내지 않습니다. Gemini는 웹·앱 한도가 아니며 Grok은 Build 전용 예시입니다. 공유 구독 여부는 실제 CLI 응답에 따릅니다.

현재 게시 DLL을 참조하는 임시 캡처 전용 STA 하네스에서 `MainWindow`와 `SettingsWindow`만 생성했습니다. 기본 WPF Application을 사용해 VibeMeters 앱 시작, 제공자 생성, 인증 감시, 자동 조회, 사용자 설정 저장을 실행하지 않았습니다. 현재 사용 중인 앱·인증·설정을 교체하거나 변경하지 않았습니다. 하네스는 `dist/readme-capture-20261026/`에 있으며 앱 코드는 수정하지 않았습니다.

이미지는 표시된 WPF 클라이언트 영역을 렌더링한 것으로 OS 합성 테두리·작업표시줄·트레이 픽셀이나 실제 마우스 입력을 검증하지 않습니다. 전체 회귀·UI 스모크·실계정 조회·로그인도 실행하지 않았습니다. 기존 `verification.txt`와 캐시/로그인 오류 예시 이미지는 이전 스모크 기록으로, 이번 캡처 생성의 통과 기록이 아닙니다.

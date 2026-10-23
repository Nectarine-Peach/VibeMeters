# VibeMeters

Codex와 Claude 구독의 **남은 사용량**을 확인하는 작은 Windows 트레이 앱입니다. WPF/.NET 8 기반이며 브라우저 엔진과 상주 CLI 프로세스를 사용하지 않습니다.

## 실행

Windows 10/11 x64에서 `dist/VibeMeters-win-x64/VibeMeters.exe`를 실행하세요. 배포본은 .NET을 포함하므로 별도 설치가 필요하지 않습니다. ZIP을 원하는 고정 폴더에 압축 해제한 후 실행하세요. Windows 시작 시 실행을 켰다면 앱 위치를 옮긴 뒤 해당 옵션을 다시 켜 주세요. 개인 빌드로 코드 서명이 없어 Windows가 게시자를 확인하지 못할 수 있습니다.

Codex CLI에서 ChatGPT 구독 계정으로, Claude Code에서 Claude 구독 계정으로 로그인하면 자동 연결됩니다. Claude 인증 오류는 Claude Code의 `/login`으로 다시 로그인한 뒤 앱에서 새로고침하세요. Codex는 `codex login`을 사용합니다. API 키의 비용·토큰 통계가 아닌 계정 구독 한도를 표시합니다.

**v1.1 앱 내 로그인:** 카드의 로그인 버튼, 설정의 계정 연결 또는 트레이의 Claude/Codex 로그인을 누르면 설치된 공식 CLI의 로그인 창이 열립니다. Claude는 `claude auth login --claudeai`를 실행합니다. 열리는 터미널·브라우저에서 로그인을 완료하면 인증 파일 변경을 감지해 자동 조회합니다. 로그인 창을 열었다는 안내는 인증 완료를 뜻하지 않습니다. Claude 웹/데스크톱 앱 로그인만으로는 현재 Windows Claude Code 인증이 갱신되지 않을 수 있습니다.

## 조작

- 트레이 아이콘 클릭: 창 열기. 우클릭: 창 열기, 새로고침, 항상 위, 종료.
- 창의 핀: 항상 위. `F5`: 새로고침. `Esc` 및 닫기: 트레이로 숨기기.
- 최소화: 작업표시줄 표시가 켜져 있으면 작업표시줄로 최소화합니다.
- 설정: 갱신 간격 30초·1분·2분(기본)·5분·15분·수동, 작업표시줄 표시, Windows 시작 시 실행.
- 테두리 드래그로 크기 조절: 기본 340×320, 최소 220×140. 너비 280 미만 또는 높이 220 미만에서는 핵심 비율만 두 줄로 표시하며, 너비 520 이상이면 서비스별 카드를 가로로 배치합니다. 크기는 논리 픽셀 기준입니다.
- `Ctrl+M`, 제목 더블클릭 또는 미니 버튼으로 240×148 미니 창과 이전 크기를 전환합니다. 창 크기·위치·미니 모드를 저장합니다.
- 수치 위에 마우스를 올리면 정확한 초기화 시각과 확인 상태를 볼 수 있습니다. 미니 창의 경고 표시는 오래된 데이터나 인증 오류를 뜻하며 인증 오류의 서비스 이름을 누르면 로그인합니다.
- 트레이 아이콘의 왼쪽 막대는 Codex, 오른쪽은 Claude입니다. 제공된 한도 중 가장 적게 남은 비율을 보여주며 20% 이하 노랑, 5% 이하 빨강, 조회 불가 회색입니다.
- 선택 가능한 20%·5% 알림은 새 사용량이 임계점을 내려갈 때만 표시합니다. 시작 직후나 같은 수치를 반복 조회할 때는 중복 알림을 띄우지 않습니다.
- 작업표시줄 진행 막대는 확인 가능한 창 중 가장 높은 **사용 비율**을 표시합니다. 카드와 트레이 툴팁은 **남은 비율**을 표시합니다.
- 두 번째 실행은 기존 창을 엽니다. 종료는 트레이 메뉴에서 선택합니다.

## 조회와 자원 사용

서버 푸시를 제공하지 않는 조회 방식이므로 실시간 스트리밍 대신 설정한 주기로 확인합니다. 두 서비스는 병렬 조회하며 중복 요청을 방지합니다. 네트워크 시간 제한 15초, 응답 최대 1MiB, HTTP 연결 재사용, 실패·429 응답의 지수 백오프 및 Retry-After 대기를 적용합니다. 창이 숨겨져도 설정한 주기로 확인하며 PC 잠금·절전에서는 정지합니다. 애니메이션이나 프레임 단위 폴링이 없고 화면은 새 응답 때만 갱신합니다.

인증은 `%USERPROFILE%\.codex\auth.json`, `%USERPROFILE%\.claude\.credentials.json`에서 매번 읽습니다. `CODEX_HOME`과 `CLAUDE_CONFIG_DIR`도 지원합니다. 토큰을 복사·기록·갱신하지 않으며 고정된 서비스 HTTPS 주소에만 전송합니다. Windows Credential Manager에만 저장된 계정은 현재 지원하지 않습니다.

Codex는 `https://chatgpt.com/backend-api/wham/usage`, Claude는 `https://api.anthropic.com/api/oauth/usage`를 사용합니다. 제품 내부 엔드포인트는 변경·접근 제한될 수 있으며 공식적인 안정성 계약이 없습니다. Claude의 제3자 앱 조회가 계정·서버 정책에 따라 거절되면 로그인 안내 또는 조회 오류를 표시합니다. [Codex 공식 클라이언트 소스](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client.rs), [Codex App Server 문서](https://learn.chatgpt.com/docs/app-server).

조회 실패 시 이전 수치를 유지하고 오류 메시지로 오래된 데이터임을 알립니다. 초기화까지 남은 시간은 마지막 조회 시점 기준이며 절대 초기화 시간도 함께 표시합니다. 설정은 `%LOCALAPPDATA%\VibeMeters\settings.json`, 자동시작은 현재 사용자의 Run 레지스트리에만 저장합니다.

## 개발과 검증

.NET 8 Windows SDK가 필요합니다.

```powershell
dotnet build -c Release
dotnet run --project tests/VibeMeters.Tests.csproj -c Release
.\scripts\publish.ps1
.\dist\VibeMeters-win-x64\VibeMeters.exe --smoke-test
```

`--demo`는 계정에 접근하지 않는 예시 데이터 미리보기입니다. `--smoke-test`는 별도 데모 인스턴스에서 실제 WPF 창을 렌더링하고 항상 위·작업표시줄·숨기기·복원 상태를 검증한 뒤 종료합니다. 결과와 창 이미지는 실행 파일 옆 `artifacts/`에 생성됩니다. 정상 계정의 인증 정보를 변경하지 않습니다. HTTP·파서·설정 회귀 테스트는 격리된 임시 인증 파일과 가짜 HTTP 응답을 사용합니다.

최신 스모크 검증은 220×140·260×180·340×320·600×300 반응형, 미니 전환, 설정 및 로그인 안내를 포함합니다. [유사 앱 조사](RESEARCH.md), [검증 기록](VERIFICATION.md)을 참고하세요.

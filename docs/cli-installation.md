# CLI 설치 및 업데이트

VibeMeters에서 Codex나 Claude에 로그인하려면 해당 서비스의 공식 CLI가 필요합니다. 이미 로그인한 계정은 저장된 인증 정보로 사용량을 조회합니다. 조회 중에는 Codex CLI를 통해 한도를 다시 확인하거나 Claude Code를 통해 만료된 인증을 갱신할 수 있습니다. Gemini 회사 계정의 Code Assist 런타임은 ZIP에 포함되어 있습니다. 개인 계정의 Antigravity 연결에는 별도로 설치한 Antigravity CLI를 사용합니다. [Gemini 계정 연결](account-usage.md#gemini)

## 설치 후 로그인

설정의 **계정 · CLI** 탭에 있는 **CLI 설치 및 업데이트**에는 Codex CLI, Claude Code, Antigravity CLI의 설치 상태와 실행해서 확인한 버전이 표시됩니다. **설치** 또는 **업데이트**를 한 번 누르면 해당 작업을 시작하며, 진행 중 같은 버튼으로 취소할 수 있습니다. 탭을 바꿔도 진행 중인 작업은 유지되며, 설정 창을 닫으면 취소합니다. 이 섹션에서는 로그인하지 않습니다. 설치 후 같은 탭의 **계정 연결** 버튼을 사용하세요.

설정은 **일반**과 **계정 · CLI** 두 탭으로 나뉩니다. 기본 크기에서는 스크롤 없이 표시하고, 창을 작게 줄이거나 안내가 길어진 경우에만 해당 탭 안에서 스크롤합니다. 상태 확인용 `--version` 프로세스는 Claude와 Antigravity의 자동 업데이트를 해당 프로세스에서만 끕니다. 사용자 전역 환경이나 CLI 설정 파일은 바꾸지 않습니다.

1. VibeMeters에서 로그인할 서비스를 선택합니다. CLI가 설치되어 있으면 공식 로그인이 바로 열립니다.
2. 설치 안내가 열리면 **설치 후 로그인**을 누릅니다. 공식 설치 프로그램이 현재 Windows 사용자 계정에 CLI를 설치합니다. 인터넷 연결이 필요하며, 회사에서 관리하는 PC에서는 소프트웨어 설치 정책이 적용됩니다.
3. 설치가 끝나면 VibeMeters가 사용자 PATH를 다시 읽고 설치된 CLI로 로그인을 시작합니다. 실패한 경우 안내 창에서 **다시 설치**를 선택할 수 있습니다.

## 설치 경로와 취소

| 서비스 | 기본 실행 파일 폴더 | 공식 설치 원본 |
| --- | --- | --- |
| Codex | `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin` | `https://chatgpt.com/codex/install.ps1` |
| Claude | `%USERPROFILE%\.local\bin` | `https://claude.ai/install.ps1` |
| Antigravity | `%LOCALAPPDATA%\agy\bin` | `https://antigravity.google/cli/install.cmd` |

VibeMeters는 기본 경로 외에도 사용자·시스템 PATH, npm 설치 경로, Codex 사용자 지정 경로에서 CLI를 찾습니다. 공식 설치 프로그램은 Windows PowerShell에서 현재 사용자 권한으로 실행합니다. [OpenAI 설치 환경 변수](https://learn.chatgpt.com/docs/config-file/environment-variables) · [Codex Windows 배포 안내](https://learn.chatgpt.com/docs/enterprise/windows-deployment) · [Claude Code 설치 안내](https://code.claude.com/docs/en/setup)

설치는 최대 10분 동안 진행됩니다. 설치를 취소하거나 앱을 종료하면 실행 중인 설치 프로세스도 종료됩니다. 공식 설치 프로그램이 이미 기록한 파일은 남을 수 있습니다.

## 기존 CLI 업데이트

- 공식 native Codex 설치는 기존 실행 파일 폴더를 유지해 공식 설치 프로그램을 다시 실행합니다. 구형 설치 형식을 바꾸는 확인 요청은 자동 승인하지 않으며 실패를 표시합니다.
- native Claude Code와 Antigravity는 현재 실행 파일의 `update` 명령을 사용합니다. [Claude 업데이트](https://code.claude.com/docs/en/setup#update-claude-code) · [Antigravity 명령 안내](https://antigravity.google/docs/cli/commands/changelog)
- Codex·Claude의 npm shim과 패키지를 확인한 경우 동일한 global prefix에서 공식 패키지의 `latest`를 설치합니다. 알 수 없는 shim을 다른 설치 방식으로 덮어쓰지 않습니다.
- WinGet 관리 경로의 실행 파일은 해당 패키지를 `winget upgrade`로 업데이트합니다. 회사 정책이나 설치 권한 때문에 진행하지 못하면 실패를 표시하며 권한을 자동으로 올리지 않습니다.

CLI별 중복 작업을 막고, 작업 종료 후 VibeMeters가 다시 찾은 실행 경로와 `--version` 성공을 확인합니다. 종료 코드가 0이거나 버전이 같다는 이유만으로 **최신 버전**이라고 단정하지 않습니다. 업데이트 명령 완료와 확인한 버전을 표시합니다. 기본 설치에는 공식 HTTPS 출처만 사용하며 TLS 검증을 끄지 않습니다. [Antigravity 공식 설치 안내](https://antigravity.google/docs/getting-started?tab=cli)

## 검증 범위

설치·업데이트·취소·중복 실행 방지·실패·버전 확인은 합성 실행기로 검증했습니다. 설정 탭을 바꾸는 동안 작업을 유지하고 창을 닫을 때 취소하는 흐름도 확인했습니다. 실제 CLI 설치와 업데이트, 설치 직후 로그인, 회사 CA·프록시·소프트웨어 설치 정책에 따른 동작은 직접 실행하지 않았습니다. [검증 기록](../VERIFICATION.md)

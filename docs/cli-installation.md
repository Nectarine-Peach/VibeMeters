# Codex와 Claude CLI 설치

VibeMeters에서 Codex나 Claude에 로그인하려면 해당 서비스의 공식 CLI가 필요합니다. 이미 로그인한 계정은 저장된 인증 정보로 사용량을 조회합니다. 조회 중에는 Codex CLI를 통해 한도를 다시 확인하거나 Claude Code를 통해 만료된 인증을 갱신할 수 있습니다. Gemini 로그인에는 VibeMeters에 포함된 런타임을 사용합니다.

## 설치 후 로그인

1. VibeMeters에서 로그인할 서비스를 선택합니다. CLI가 설치되어 있으면 공식 로그인이 바로 열립니다.
2. 설치 안내가 열리면 **설치 후 로그인**을 누릅니다. 공식 설치 프로그램이 현재 Windows 사용자 계정에 CLI를 설치합니다. 인터넷 연결이 필요하며, 회사에서 관리하는 PC에서는 소프트웨어 설치 정책이 적용됩니다.
3. 설치가 끝나면 VibeMeters가 사용자 PATH를 다시 읽고 설치된 CLI로 로그인을 시작합니다. 실패한 경우 안내 창에서 **다시 설치**를 선택할 수 있습니다.

## 설치 경로와 취소

| 서비스 | 기본 실행 파일 폴더 | PowerShell 설치 원본 |
| --- | --- | --- |
| Codex | `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin` | `https://chatgpt.com/codex/install.ps1` |
| Claude | `%USERPROFILE%\.local\bin` | `https://claude.ai/install.ps1` |

VibeMeters는 기본 경로 외에도 사용자·시스템 PATH, npm 설치 경로, Codex 사용자 지정 경로에서 CLI를 찾습니다. 공식 설치 프로그램은 Windows PowerShell에서 현재 사용자 권한으로 실행합니다. [OpenAI 설치 환경 변수](https://learn.chatgpt.com/docs/config-file/environment-variables) · [Codex Windows 배포 안내](https://learn.chatgpt.com/docs/enterprise/windows-deployment) · [Claude Code 설치 안내](https://code.claude.com/docs/en/setup)

설치는 최대 10분 동안 진행됩니다. 설치를 취소하거나 앱을 종료하면 실행 중인 설치 프로세스도 종료됩니다. 공식 설치 프로그램이 이미 기록한 파일은 남을 수 있습니다.

## 초기 검증 기록

설치 기능을 처음 추가했을 때 빌드, 배포 파일 생성, 계정 없이 실행하는 Gemini 런타임 진단을 확인했습니다. 당시 검증 범위에는 실제 CLI 설치와 취소 후 복구, 설치 직후 로그인이 포함되지 않았습니다.

# Codex와 Claude 로그인 도구 설치

설정에서 **Codex 로그인** 또는 **Claude 로그인**을 누르면 공식 CLI로 인증을 시작합니다. 이미 설치한 CLI가 있으면 그대로 사용합니다.

## 설치 안내가 열릴 때

1. **설치 후 로그인**을 선택하세요. 공식 Windows 설치 프로그램이 현재 사용자 계정에 CLI를 설치합니다.
2. 설치가 끝나면 VibeMeters가 실행 파일을 다시 찾고 로그인을 시작합니다. 브라우저에서 사용할 계정이나 워크스페이스를 선택하세요.
3. 사용량 창으로 돌아와 **F5**를 누르세요.

인터넷 연결이 필요하며 회사 PC에서는 설치 정책이 적용됩니다. VibeMeters는 관리자 권한을 요청하지 않습니다. 설치를 취소했거나 실패했다면 안내에 따라 다시 시도하거나, 아래 공식 안내에서 직접 설치할 수 있습니다.

| 서비스 | 기본 실행 파일 폴더 | 설치 안내 |
|---|---|---|
| Codex | `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin` | [OpenAI Windows 배포 안내](https://learn.chatgpt.com/docs/enterprise/windows-deployment) |
| Claude | `%USERPROFILE%\.local\bin` | [Claude Code 설치 안내](https://code.claude.com/docs/en/setup) |

앱은 사용자·시스템 PATH에서도 CLI를 찾습니다. 설치 후 실행 파일을 찾지 못하면 해당 폴더가 PATH에 있는지 확인하세요. 앱에서 설치를 시작한 경우 갱신된 사용자 PATH를 다시 읽습니다.

Gemini 개인 계정에는 Antigravity CLI를, Grok에는 Grok Build CLI를 사용합니다. Gemini 회사 계정의 Code Assist 조회 런타임은 ZIP에 포함되어 있습니다. [계정별 연결 방법](account-usage.md) · [Grok Build 안내](grok-usage.md)

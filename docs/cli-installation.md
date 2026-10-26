# 로그인용 공식 CLI 설치

Codex·Claude 사용량 조회는 기존 인증 파일과 HTTP로 처리합니다. 조회나 앱 시작 때 CLI를 자동 설치하지 않습니다. CLI는 공식 로그인 절차를 실행할 때 필요합니다. Gemini는 기존에 동봉한 코어 런타임을 사용하므로 이 설치 흐름에 포함하지 않습니다.

로그인 버튼에서 CLI를 찾지 못하면 설치 안내 창을 엽니다. 사용자가 **설치 후 로그인**을 선택할 때만 설치를 실행합니다. 취소할 수 있으며, 설치 중에는 진행 상태를 표시하고 실패하면 고정된 오류 안내와 **다시 설치**를 제공합니다. 설치 종료 코드가 성공이어도 실행 파일을 다시 찾기 전에는 로그인으로 진행하지 않습니다.

공식 Windows PowerShell 설치 원본:

- Codex: `https://chatgpt.com/codex/install.ps1`. [OpenAI 설치 환경 변수](https://learn.chatgpt.com/docs/config-file/environment-variables), [Windows 배포 안내](https://learn.chatgpt.com/docs/enterprise/windows-deployment).
- Claude: `https://claude.ai/install.ps1`. [Anthropic 공식 설치 안내](https://code.claude.com/docs/en/setup).

현재 사용자 계정의 공식 설치 프로그램을 숨겨진 Windows PowerShell에서 실행합니다. 별도 Node/npm/winget 설치는 필요하지 않으며 관리자 권한 상승을 요청하거나 시스템 실행 정책·TLS 인증서 검증을 변경하지 않습니다. Codex는 공식 비대화형 설치 옵션을 사용해 다른 패키지 설치 제거·이전 설치 교체·설치 후 CLI 자동 실행에 대한 기본 거절을 유지합니다. 설치의 파일/버전/무결성 관리는 공급자 공식 설치 코드에 맡깁니다. 인터넷과 회사의 설치 허용 정책이 필요하며 공식 설치 프로그램이 선택한 릴리스를 설치합니다.

Codex의 기본 명령 경로는 `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`, Claude는 `%USERPROFILE%\.local\bin`입니다. 기존 npm·사용자·시스템 PATH와 Codex 사용자 지정 경로도 탐색합니다. 설치 뒤 갱신된 사용자 PATH를 다시 읽어 앱 재시작 없이 CLI를 찾습니다. 사용량 인증 파일의 위치·토큰 갱신 정책은 변경하지 않습니다.

설치 대기는 10분이며 취소/앱 종료 시 실행 중인 설치 프로세스 트리를 종료합니다. 취소 후 공급자 설치 파일 일부가 남을 수 있으며 앱이 임의로 삭제하거나 기존 설치를 제거하지 않습니다. 설치 원시 출력과 OAuth URL은 파일·로그에 저장하지 않습니다.

사용자의 요청으로 CLI 설치 기능 추가 후 회귀·UI 검사는 실행하지 않았습니다. 컴파일/배포 생성과 기존 Gemini 패키지의 계정 없는 내장 runtime 진단만 실행합니다. 실제 CLI 설치·설치 취소 복구·로그인 연결은 이 PC에서 실행하지 않았으며 검증 완료로 표시하지 않습니다.

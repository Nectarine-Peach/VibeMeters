# Grok Build 구독 사용량

VibeMeters는 설치된 공식 **Grok Build CLI**로 구독 사용 비율과 초기화 시각을 읽습니다. 브라우저 확장, Native Messaging 등록, 브라우저 프로필 변경은 필요하지 않습니다. API 비용·추가 사용 금액·로컬 토큰 사용 통계는 표시하지 않습니다.

설정에서 Grok 표시를 켜고 **Grok 로그인**을 선택하면 공식 `grok login --oauth`를 실행합니다. 필요한 브라우저 로그인은 사용자가 직접 완료합니다. CLI에서 직접 `grok login --oauth` 또는 `grok login --device-auth`를 실행해도 됩니다. 이 PC의 기존 Grok Build 1.0.40을 재사용하며 새로 설치하거나 업데이트하지 않았습니다.

한도 범위는 공식 응답을 그대로 따릅니다. `isUnifiedBillingUser=true`이면 **공유 주간/월간** 등으로 표시합니다. false이거나 제공되지 않으면 **Build 주간/월간/한도**로 표시하며 Grok 웹 전체 한도라고 간주하지 않습니다. 별도 Grok Bot 한도는 이 응답에서 제공하지 않으므로 행을 만들지 않습니다. 비율·초기화 시각·기간이 없으면 금액으로 추정하지 않습니다.

일반 조회는 사용자 홈을 작업 폴더로 숨겨진 `grok agent --no-leader stdio`를 실행하고 ACP `initialize`와 `_x.ai/billing` 두 요청만 전송합니다. `session/new`, `session/prompt`, `authenticate`, 토큰 갱신, 브라우저 로그인은 전송하지 않습니다. 사용자의 기존 leader/모델 세션을 재사용하거나 중단하지 않습니다. 앱은 Grok 인증 파일 내용을 읽지 않으며 공식 CLI가 자체 인증을 관리합니다.

공식 소스 근거(2026-10-26 확인):

- [billing.rs](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/extensions/billing.rs#L27-L109): 사용 비율·현재 기간·공유 구독 여부의 응답 구조. [182~269행](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/extensions/billing.rs#L182-L269)은 읽기 전용 billing 조회입니다.
- [manager.rs](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-login/src/manager.rs#L319-L409): 공식 CLI가 저장된 인증을 읽어 초기화합니다.
- [acp_agent.rs](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/agent/mvp_agent/acp_agent.rs#L288-L294): initialize 시 디스크의 인증을 다시 읽습니다. [billing dispatch](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/agent/mvp_agent/acp_agent.rs#L2266)와 [auth gate](https://github.com/xai-org/grok-build/blob/main/crates/codegen/xai-grok-shell/src/extensions/auth_gate.rs#L11-L17)는 별도의 로그인 RPC 없이 현재 인증으로 처리합니다.

앱은 공식 CLI와 같은 경로의 인증 파일 **수정 시각만** 감시합니다. 비어 있지 않은 `GROK_AUTH_PATH`가 우선이며, 없으면 `GROK_HOME/auth.json`, 둘 다 설정되지 않았으면 `%USERPROFILE%\.grok\auth.json`입니다. 상대 경로는 CLI 작업 폴더인 사용자 홈 기준으로 해석합니다. 외부 로그인/계정 변경 후 성공 캐시를 비우며 인증 파일이나 토큰을 수정·삭제하지 않습니다. 정상 조회는 2분 캐시하며 요청 제한은 10분 대기합니다. 계정이 바뀌어도 대기를 우회하지 않고 이전 수치를 제거합니다. 조회 중 인증이 바뀌면 그 결과도 표시하지 않습니다. 실패·인증 필요 상태에 이전 계정 비율을 재사용하지 않습니다. 인증 필요 상태는 파일 변경이나 명시적 로그인 전까지 재요청하지 않습니다.

CLI 응답은 JSON-RPC ID로 매칭하며 한 줄 64KiB·전체 1MiB·30초 제한을 둡니다. 예상하지 않은 대화형 요청은 거부하고 CLI를 종료합니다. 원시 로그·오류·OAuth URL·계정 식별자는 UI나 파일로 전달하지 않습니다.

# 개인·회사 계정 사용량과 로그인

## Gemini 개인 계정과 Antigravity

Google은 [2026년 6월 18일부터 개인·AI Pro·Ultra 계정의 Gemini CLI Google 로그인을 지원하지 않습니다](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals). Standard/Enterprise 지원은 별개입니다. 개인 계정에 반환된 `free-tier / UNSUPPORTED_CLIENT`를 Cloud 프로젝트 누락으로 잘못 표시하던 처리를 수정했습니다.

해당 개인용 경로가 거부되면 설치된 **Antigravity CLI 1.4.7 이상**의 공식 `agy -p /usage --output-format json` 명령으로 조회합니다. [공식 명령 안내](https://www.antigravity.google/docs/cli/commands/usage/)와 [공식 유지관리자의 JSON 호출 안내](https://github.com/google-antigravity/antigravity-cli/issues/590)를 따릅니다. 앱은 CLI나 계정을 자동 설치·전환하지 않으며 keyring 토큰을 직접 읽지 않습니다. 숨겨진 headless 조회는 새 로그인을 진행하지 않고 인증 필요 상태로 끝납니다.

카드 제목 **Gemini · Antigravity**는 Antigravity에서 사용하는 Gemini의 한도임을 나타냅니다. 서버가 Gemini Pro/Flash 공유 주간·5시간 한도와 별도 Argon 주간 한도를 주면 그대로 표시하며, 공유 한도를 모델별로 나누지 않습니다. Claude/GPT 그룹은 이 카드에서 제외합니다. Gemini 웹·앱 한도와 합산하거나 같은 값으로 간주하지 않습니다. 초기화 시점과 전체 한도 이름은 각 행의 툴팁에서 확인할 수 있습니다.

이 경로의 **Gemini 로그인**은 공식 Antigravity 창을 엽니다. 창에서 로그인/계정 전환을 완료한 뒤 앱을 새로고침하세요. Antigravity 인증은 Gemini CLI의 Google 로그인 캐시와 별개입니다. 기존 회사 계정의 유효한 Code Assist 프로젝트/한도 경로는 유지됩니다.

Antigravity는 keyring 계정 ID를 사용량 응답에 노출하지 않으므로 성공 수치를 다음 새로고침에 재사용하지 않습니다. 실패·로그인 변경 때 이전 수치를 표시하지 않으며 요청 제한 시에는 10분 대기를 유지합니다. CLI가 없거나 오래되었거나 인증이 필요하거나 Gemini 한도를 주지 않으면 해당 이유를 표시합니다.

VibeMeters는 각 PC의 공식 Codex CLI / Claude Code가 현재 로그인한 계정을 사용합니다. 계정을 전환해서 사용하며 여러 계정을 동시에 표시하지 않습니다. 집 PC의 인증이 회사 PC로 자동 이전되지는 않습니다.

사용량 조회 자체는 저장된 인증 파일과 HTTP를 사용하므로 CLI 설치를 요구하지 않습니다. 로그인할 때 CLI가 없으면 공식 사용자 설치를 명시적으로 선택할 수 있습니다. [앱의 CLI 설치 안내](cli-installation.md).

## 2026-10-26 회사 PC 원인

기존 OAuth 인증으로 두 사용량 API가 HTTP 200을 반환했습니다. 로그인 자체는 유효했지만, 기존 파서가 개인 구독의 5시간·주간 창만 인식해 회사 계정의 정상 응답을 표시하지 못했습니다.

- Codex Business는 `rate_limit: null`이어도 `spend_control.individual_limit`에 개인 한도를 제공합니다. 앱은 **개인** 행에 `remaining_percent`를 우선 사용하고, 초기화 시점을 함께 표시합니다. 실제 회사 계정은 잔여 48%를 반환했습니다. 초기 조사에서 이 중첩 필드를 누락해 비율 미제공으로 판단한 결론은 철회했습니다. 개인 한도는 5시간·주간 창과 구분하며 기간을 임의로 추정하지 않습니다. 비율과 개인 한도가 실제로 없는 크레딧형 응답에만 **연결됨 · 비율 미제공**을 표시합니다.
- Claude Enterprise는 5시간·주간 창 대신 `extra_usage.utilization`을 반환했습니다. 앱은 **월추가** 행에 이 비율을 표시합니다. 이는 서버가 제공한 월 추가 사용 예산이며 개인 구독의 5시간·주간 한도와 다른 값입니다. 조직 전체 예산인지 구성원 예산인지는 응답만으로 추정하지 않습니다.
- 개인 구독 계정은 기존 5시간·주간 창을 계속 표시합니다. 추가 사용이 활성화되어 있으면 별도 월추가 행을 함께 표시합니다.

## 계정 전환

앱에서 로그인에 성공하면 이전 계정 수치와 성공 캐시를 비우고 새 계정을 조회합니다. 서버의 429 재시도 대기는 계정 전환으로 우회하지 않으며, 대기 중에도 이전 계정 수치는 제거합니다.

설정 또는 트레이의 서비스 로그인 버튼으로 공식 CLI 로그인 절차를 진행하고 브라우저에서 사용할 개인/회사 계정 및 워크스페이스를 선택합니다. 회사 SSO가 요구되면 공식 CLI에서 `claude auth login --sso`를 사용한 뒤 앱을 새로고침할 수 있습니다.

Codex 브라우저 콜백 포트를 사용할 수 없으면 공식 CLI의 `codex login --device-auth`를 사용할 수 있습니다. 회사 워크스페이스의 device 인증 허용 여부는 관리자 정책을 따릅니다. 앱은 인증 파일을 수정·삭제하거나 토큰을 자체 갱신하지 않습니다.

## 로그인 실패 안내

CLI의 원시 출력과 OAuth URL은 파일이나 로그에 저장하지 않습니다. 인증서·프록시·콜백 포트·SSO 오류를 비밀값 없는 안내로 표시합니다. CLI가 브라우저 자동 실행 실패를 명시한 경우에만 HTTPS 공식 로그인 호스트의 URL을 운영체제 브라우저로 한 번 엽니다. 일반적인 수동 접속 안내만으로 중복 창을 열지 않습니다.

401은 재로그인, 403은 서비스 권한·SSO·회사 네트워크 정책 확인, 407은 프록시 인증 확인으로 안내합니다. TLS 인증서 검증을 끄거나 조직의 정책을 우회하지 않습니다. 기존 `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, `NODE_EXTRA_CA_CERTS`는 CLI 자식 프로세스에 상속됩니다.

Codex의 파일 기반 `CODEX_HOME/auth.json`과 Claude의 `CLAUDE_CONFIG_DIR/.credentials.json` 인증을 읽습니다. 이번 PC에서 확인한 인증 저장소는 모두 파일 기반입니다. OS keyring만 사용하는 다른 환경은 이번 변경에서 지원을 검증하지 않았습니다.

공식 참고: [Codex 인증](https://learn.chatgpt.com/docs/auth), [Claude CLI](https://code.claude.com/docs/en/cli-reference), [Claude 회사 네트워크](https://code.claude.com/docs/en/network-config).

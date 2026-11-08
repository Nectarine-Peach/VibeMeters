# 개인·회사 계정 사용량과 로그인

## Gemini 개인 계정과 Antigravity

Google은 [2026년 6월 18일부터 개인·AI Pro·Ultra 계정의 Gemini CLI Google 로그인을 지원하지 않습니다](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals). Standard/Enterprise 지원은 별개입니다. 개인 계정에 반환된 `free-tier / UNSUPPORTED_CLIENT`를 Cloud 프로젝트 누락으로 잘못 표시하던 처리를 수정했습니다.

해당 개인용 경로가 거부되면 설치된 **Antigravity CLI 1.4.7 이상**의 공식 `agy -p /usage --output-format json` 명령으로 조회합니다. [공식 명령 안내](https://www.antigravity.google/docs/cli/commands/usage/)와 [공식 유지관리자의 JSON 호출 안내](https://github.com/google-antigravity/antigravity-cli/issues/590)를 따릅니다. 앱은 CLI나 계정을 자동 설치·전환하지 않으며 keyring 토큰을 직접 읽지 않습니다. 숨겨진 headless 조회는 새 로그인을 진행하지 않고 인증 필요 상태로 끝납니다.

카드 제목 **Gemini · Antigravity**는 Antigravity에서 사용하는 Gemini의 한도임을 나타냅니다. 서버가 Gemini Pro/Flash 공유 주간·5시간 한도와 별도 Argon 주간 한도를 주면 그대로 표시하며, 공유 한도를 모델별로 나누지 않습니다. Claude/GPT 그룹은 이 카드에서 제외합니다. Gemini 웹·앱 한도와 합산하거나 같은 값으로 간주하지 않습니다. 초기화 시점과 전체 한도 이름은 각 행의 툴팁에서 확인할 수 있습니다.

이 경로의 **Gemini 로그인**은 공식 Antigravity 창을 엽니다. 창에서 로그인/계정 전환을 완료한 뒤 앱을 새로고침하세요. Antigravity 인증은 Gemini CLI의 Google 로그인 캐시와 별개입니다. 기존 회사 계정의 유효한 Code Assist 프로젝트/한도 경로는 유지됩니다.

Antigravity는 keyring 계정 ID를 사용량 응답에 노출하지 않으므로 성공 수치를 다음 새로고침에 재사용하지 않습니다. 실패·로그인 변경 때 이전 수치를 표시하지 않으며 요청 제한 시에는 10분 대기를 유지합니다. CLI가 없거나 오래되었거나 인증이 필요하거나 Gemini 한도를 주지 않으면 해당 이유를 표시합니다.

회사 Code Assist는 HTTP 429를 받으면 기본 10분을 기다립니다. 서버가 `Retry-After`로 더 늦은 시각을 지정하면 그때까지 기다립니다. 수동 새로고침이나 로그인 완료로 이 시간을 앞당기지 않습니다.

## Codex와 Claude 연결

VibeMeters는 각 PC에서 공식 Codex CLI와 Claude Code에 연결된 현재 계정을 표시합니다. 유효한 인증 파일로 일반 조회를 할 수 있고, 인증 복구에는 해당 공식 CLI를 사용합니다. 로그인할 때 CLI가 없으면 공식 사용자 설치를 선택할 수 있습니다. [앱의 CLI 설치 안내](cli-installation.md)

## 2026-10-26 회사 PC 원인

기존 OAuth 인증으로 두 사용량 API가 HTTP 200을 반환했습니다. 로그인 자체는 유효했지만, 기존 파서가 개인 구독의 5시간·주간 창만 인식해 회사 계정의 정상 응답을 표시하지 못했습니다.

- Codex Business는 `rate_limit: null`이어도 `spend_control.individual_limit`에 개인 한도를 제공합니다. 앱은 **개인** 행에 `remaining_percent`를 우선 사용하고, 초기화 시점을 함께 표시합니다. 실제 회사 계정은 잔여 48%를 반환했습니다. 초기 조사에서 이 중첩 필드를 누락해 비율 미제공으로 판단한 결론은 철회했습니다. 개인 한도는 5시간·주간 창과 구분하며 기간을 임의로 추정하지 않습니다. 비율과 개인 한도가 실제로 없는 크레딧형 응답에만 **연결됨 · 비율 미제공**을 표시합니다.
- Claude Enterprise는 5시간·주간 창 대신 `extra_usage.utilization`을 반환했습니다. 앱은 **월추가** 행에 이 비율을 표시합니다. 이는 서버가 제공한 월 추가 사용 예산이며 개인 구독의 5시간·주간 한도와 다른 값입니다. 조직 전체 예산인지 구성원 예산인지는 응답만으로 추정하지 않습니다.
- 개인 구독 계정은 기존 5시간·주간 창을 계속 표시합니다. 추가 사용이 활성화되어 있으면 별도 월추가 행을 함께 표시합니다.

## 계정 전환

수동 버튼·F5·트레이 및 설정 주기의 자동 조회는 **정상 성공 캐시만** 우회합니다. Claude는 30초·1분 자동 설정에서도 2분 성공 캐시를 사용합니다. 429/Retry-After와 인증·실패 보호 대기는 모든 조회에 적용합니다. 인증 파일 변경·로그인 완료·잠금 해제는 별도 조회 트리거입니다. 카드의 `확인`은 마지막 성공 수치를 관측한 시각이고, 툴팁의 `조회 확인`은 마지막 결과·오류 확인 시각입니다. `다음 자동`은 보호 대기 이후 조회할 수 있는 첫 타이머 시각입니다. 이미 진행 중인 조회나 Windows 잠금·절전으로 실제 조회가 늦어질 수 있습니다.

앱에서 로그인에 성공하면 이전 계정 수치와 성공 캐시를 비우고 새 계정을 조회합니다. 서버의 429 재시도 대기는 계정 전환으로 우회하지 않으며, 대기 중에도 이전 계정 수치는 제거합니다.

Grok Build CLI가 사용량 조회 중 새 인증을 저장하면 앱은 공식 CLI를 한 번 더 실행합니다. 새 조회 중 인증이 다시 바뀌면 결과를 버리고, 요청 제한을 받으면 대기 시간을 적용합니다. [Grok 연결 범위](grok-usage.md)

설정 또는 트레이의 서비스 로그인 버튼으로 공식 CLI 로그인 절차를 진행하고 브라우저에서 사용할 개인/회사 계정 및 워크스페이스를 선택합니다. 회사 SSO가 요구되면 공식 CLI에서 `claude auth login --sso`를 사용한 뒤 앱을 새로고침할 수 있습니다.

Codex 브라우저 콜백 포트를 사용할 수 없으면 공식 CLI의 `codex login --device-auth`를 사용할 수 있습니다. 회사 워크스페이스의 device 인증 허용 여부는 관리자 정책을 따릅니다. 앱은 인증 파일을 수정·삭제하거나 토큰을 자체 갱신하지 않습니다.

## 로그인 실패 안내

CLI의 원시 출력과 OAuth URL은 파일이나 로그에 저장하지 않습니다. 인증서·프록시·콜백 포트·SSO 오류를 비밀값 없는 안내로 표시합니다. CLI가 브라우저 자동 실행 실패를 명시한 경우에만 HTTPS 공식 로그인 호스트의 URL을 운영체제 브라우저로 한 번 엽니다. 일반적인 수동 접속 안내만으로 중복 창을 열지 않습니다.

Codex와 Claude는 사용량 서버가 401을 반환하면 기존 인증 복구부터 시도합니다. 403은 서비스 권한·SSO·회사 네트워크 정책 확인, 407은 프록시 인증 확인으로 안내합니다. CLI 자식 프로세스에는 기존 `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, `NODE_EXTRA_CA_CERTS` 설정이 전달됩니다.

Codex의 `CODEX_HOME/auth.json`과 Claude의 `CLAUDE_CONFIG_DIR/.credentials.json` 인증을 읽습니다. Codex 파일을 읽을 수 없거나 사용량 조회에 401이 반환되면 같은 `CODEX_HOME`의 공식 `codex app-server`에서 `account/read`와 `account/rateLimits/read`를 확인합니다. 401인 경우 공식 CLI에 자체 관리 인증 갱신을 요청합니다. 파일이 없는 경우에는 공식 CLI가 선택한 인증 저장소를 사용합니다. ChatGPT 구독 계정 한도만 표시하며 CLI가 반환한 계정 ID가 요청 계정과 다르면 새 응답을 버립니다. [공식 Codex 계정 프로토콜](https://github.com/openai/codex/blob/748ab55ca7de0b647d9a7a9d9a8e70e00f28666d/codex-rs/app-server-protocol/src/protocol/v2/account.rs#L341-L357)

## Claude 인증 자동 갱신과 재로그인 표시

2026-10-27 조사에서 저장된 Claude Pro 인증은 전날 21:21 KST에 만료되었고, 같은 토큰의 usage API는 HTTP 401을 반환했습니다. 기존 앱은 인증 실패에도 마지막 비율을 보존해 새로고침이 멈춘 것처럼 보일 수 있었습니다.

만료 2분 이내의 사용량 조회, 만료 또는 HTTP 401 때 설치된 **공식 Claude Code 2.1.317 이상**의 비대화형 `claude auth status`로 갱신을 시도합니다. [2.1.317](https://github.com/anthropics/claude-code/releases/tag/v2.1.317)에는 이 명령이 느린 갱신을 저장하기 전에 종료되어 다음 실행에서 로그아웃되던 문제의 수정이 포함되어 있습니다. 여러 설치가 있으면 첫 설치가 오래됐거나 실행되지 않을 때 다음 후보의 버전을 확인합니다. 이 버전보다 오래된 CLI만 있으면 [공식 업데이트 방법](https://code.claude.com/docs/en/setup#update-claude-code)을 따르세요. npm으로 설치했다면 `npm install -g @anthropic-ai/claude-code@latest`를 사용합니다.

만료 전 시도는 토큰당 한 번이며, 새 인증이 아직 저장되지 않았더라도 같은 토큰이 유효하면 현재 인증으로 조회를 이어갑니다. 만료 또는 401이 발생하면 기존 복구 경로가 동작합니다. [공식 CLI 명령](https://code.claude.com/docs/en/cli-reference)의 성공 종료는 상태 확인을 의미합니다. 같은 설정 경로에서 새 토큰과 미래 만료 시각을 읽고 사용량 조회까지 성공해야 갱신 완료로 판단합니다. 이 흐름은 [Claude GPT Usage Tray의 CLI 갱신 방식](https://github.com/kjhbond/claude-gpt-usage-tray/blob/80f148fb6120d451b8cad6b4bf267350c39d3716/claude_quota.py#L19-L51)을 참고했습니다.

앱은 refresh token을 직접 읽거나 교환하지 않고, 인증 파일·잠금·회전 토큰 저장은 공식 CLI가 관리합니다. 원본의 미지 필드를 덮어쓰거나 별도 토큰 복사본을 만들지 않습니다. 조회 중 다른 CLI/로그인이 토큰을 바꾼 경우 현재 파일 값을 다시 읽습니다. 자동 갱신용 CLI에는 같은 `CLAUDE_CONFIG_DIR`를 전달하고 별도 API 키·OAuth 환경 토큰·gateway 인증은 사용하지 않습니다. 프록시·회사 CA 설정은 유지합니다.

2026-10-28부터 **인증 갱신 확인 중 / 인증 재시도 대기 / 재로그인 필요**를 구분합니다. 최초 CLI 대기는 최대12초, 이미 진행 중인 같은 설정 경로의 CLI는 최대2초 공유 대기합니다. 그 뒤500ms 간격으로 최대16회(8초) 파일 반영을 확인합니다. 인증 확인 단계의 최대 대기는 약20초이며 기존 usage HTTP 요청의 15초 제한은 별도입니다. 저장 중인 CLI를 강제 종료하거나 같은 갱신 명령을 중복 실행하지 않습니다. 명령이 끝나지 않으면 파일은 재확인하되 CLI 자체 상태 확인이 필요할 수 있습니다.

시간 초과·진행 중 작업·CLI 실패·파일 미교체와 같은 미확정 결과는 **30초 재시도 대기**로 처리합니다. 교체된 토큰의 사용량 조회가 401인 경우에도 갱신 인증 자체의 만료를 단정하지 않고 다시 확인합니다. 완전한 구조화 OAuth `error: invalid_grant`가 확인되면 재로그인을 안내합니다. 403은 권한·SSO 안내로 구분합니다. 만료 후 복구 중이거나 인증이 확인되지 않으면 이전 비율과 관측 시각을 숨깁니다.

429나 일시적인 연결·서버 오류 때는 [참고 위젯의 이전 값 처리](https://github.com/kjhbond/claude-gpt-usage-tray/blob/80f148fb6120d451b8cad6b4bf267350c39d3716/display_state.py#L5-L49)처럼 같은 인증으로 확인한 최근 값만 **이전 값**으로 표시합니다. 새로고침마다 마지막 정상 조회로부터 30분이 지났는지, 한도가 초기화됐는지, 기존 인증이 유지되는지 확인합니다. 값이 지워져도 서버의 재시도 대기 시간은 유지합니다. 구독 사용량 비율이 0–100 범위를 벗어난 항목은 다른 유효한 항목과 구분해 숨깁니다.

Claude 429 대기의 종료 시각과 연속 제한 횟수는 `%LOCALAPPDATA%\VibeMeters\claude-usage-cooldown.json`에 기록해 앱 재시작 후에도 적용합니다. 이후 사용량 조회에 성공하면 이 기록을 지웁니다. 같은 실행 중 갱신한 토큰의 첫 조회가 429이면, 대기 이후 같은 토큰으로 사용량을 받아야 진단에 갱신 성공을 기록합니다.

사용자가 로그인 버튼을 누르면 브라우저 전에 현재 파일을 다시 읽고 인증 전용 대기를 한 번 재확인합니다. 기존 인증으로 실제 usage를 확인하면 브라우저를 열지 않습니다. 429·일반 네트워크/서버 보호 대기 및 알려진 진행 중 CLI도 경쟁 로그인 없이 안내합니다. 진행 중 CLI가 없고 제한된 복구 후에도 미확정이거나 재로그인이 필요한 경우에는 사용자 요청으로 공식 로그인을 한 번 실행합니다. 로그인 완료 전 파일이 갱신돼도 watcher가 복구할 수 있으며, 뒤늦은 로그인 결과가 이미 복구된 수치를 덮지 않도록 처리합니다. 자동 조회에서 브라우저나 모델 요청은 실행하지 않습니다.

원인 재구성용 `claude-auth-diagnostics.jsonl`은 `%LOCALAPPDATA%\VibeMeters`에 원인 enum·CLI 종료코드·토큰 변경 여부·미래 만료 여부만 기록합니다. 토큰·계정·OAuth URL·CLI 원문은 저장하지 않으며 현재/보관 파일 2개를 사용합니다. 현재 파일이 64KiB 이상이면 다음 기록 전에 회전하여 한 행만큼 초과할 수 있습니다. 마지막 수정 후 7일 지난 파일을 다음 진단 기록 때 정리하며, 개별 행의 7일 보존 기한은 보장하지 않습니다.

공식 참고: [Codex 인증](https://learn.chatgpt.com/docs/auth), [Claude CLI](https://code.claude.com/docs/en/cli-reference), [Claude 회사 네트워크](https://code.claude.com/docs/en/network-config).

설정 경로별 CLI 작업 표는 완료된 작업도 다음 복구 요청으로 교체되기 전까지 앱 프로세스 안에 유지합니다. 같은 경로에서 실행 중인 명령은 공유하며 대기 취소/시간 초과로 강제 종료하지 않습니다. 명령 자체가 끝나지 않으면 후속 복구도 해당 작업을 공유하므로 CLI 상태를 직접 확인해야 할 수 있습니다. 앱 종료는 남은 CLI 작업의 강제 종료를 보장하지 않습니다.

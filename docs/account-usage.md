# 구독 계정 연결과 사용량 확인

설정의 **계정 · CLI → 계정 연결**에서 사용할 서비스에 로그인한 다음 사용량 창에서 **F5**를 누르세요. 화면에 표시되는 한도는 서비스, 요금제, 회사에서 부여한 권한에 따라 달라집니다. [CLI 설치와 로그인](cli-installation.md)

## Codex

Codex CLI에 로그인한 계정과 워크스페이스의 한도를 표시합니다. 개인 구독에서는 5시간·주간 한도를, Business 워크스페이스에서는 조직이 정한 개인 한도를 받을 수 있습니다. 회사 계정의 **개인** 행은 해당 구성원에게 배정된 한도입니다. 다른 워크스페이스의 한도를 보려면 Codex에서 계정을 전환한 뒤 새로고침하세요.

## Claude

**Claude 로그인**을 누르면 공식 Claude Code 로그인 절차가 시작됩니다. 회사에서 SSO를 요구하면 CLI에서 `claude auth login --sso`를 완료한 뒤 새로고침하세요. 개인 구독의 5시간·주간 한도와 Enterprise 계정의 월 추가 사용 한도는 별도 행으로 표시됩니다. **월추가**는 서비스가 알려 준 월 추가 사용 예산에 대한 비율입니다. [Claude 인증 안내](https://code.claude.com/docs/en/authentication)

로그인이 만료될 때는 앱이 먼저 공식 CLI로 인증 갱신을 시도합니다. **인증 갱신 확인 중**이면 다음 조회를 기다리세요. **로그인 필요**가 계속되면 설정에서 다시 로그인하세요. 인증 복구에는 Claude Code 2.1.317 이상이 필요합니다. [Claude Code 업데이트](https://code.claude.com/docs/en/setup#update-claude-code)

## Gemini

**설정 → 계정 · CLI → Gemini 계정**에서 조회할 프로필을 고릅니다. 여러 계정을 등록해도 화면에는 선택한 계정의 사용량이 표시됩니다.

| 선택 | 연결 방법과 표시되는 한도 |
|---|---|
| Antigravity | 개인 계정으로 공식 Antigravity CLI 1.4.7 이상에 로그인합니다. **Gemini · Antigravity** 카드에는 서비스가 제공한 공유 한도와 별도 Argon 한도가 표시됩니다. |
| Code Assist 1 · 2 | 사용할 슬롯을 선택하고 **Gemini 로그인**을 누릅니다. 회사 계정 두 개를 각각 다른 슬롯에 연결해 전환할 수 있습니다. |

회사 계정의 모델별 한도가 보이지 않으면 선택한 계정에 Code Assist 프로젝트 권한이 있는지 확인하세요. 개인 계정의 공유 한도는 Pro와 Flash를 함께 쓸 때 적용될 수 있습니다. [Antigravity 사용량 명령](https://www.antigravity.google/docs/cli/commands/usage/) · [Google의 개인 계정 지원 안내](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals)

## 수치가 갱신되지 않을 때

| 화면에 보이는 상태 | 확인할 사항 |
|---|---|
| 연결됨 · 비율 미제공 | 로그인은 완료됐고 서비스에서 표시할 백분율을 보내지 않은 상태입니다. |
| 요청 제한 대기 | 툴팁의 재시도 시각을 확인하세요. 서버가 지정한 대기는 수동 새로고침과 계정 전환에도 적용됩니다. |
| 계정을 바꾼 직후 | 새 계정으로 로그인한 뒤 다시 조회하세요. 이전 계정의 수치는 계정 전환 시 지워집니다. |
| 수치가 오래된 것처럼 보임 | 카드의 **확인**은 사용량을 마지막으로 관측한 시각입니다. 툴팁에서 마지막 조회 결과와 다음 자동 조회 시각을 확인하세요. |

Grok 계정 연결과 공유 구독 한도는 [Grok Build 사용량 안내](grok-usage.md)에 정리했습니다. 인증 복구와 조회 제한의 구현 및 검증 기록은 [조사 기록](../RESEARCH.md)과 [검증 기록](../VERIFICATION.md)에서 볼 수 있습니다.

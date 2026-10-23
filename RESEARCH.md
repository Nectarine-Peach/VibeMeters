# 유사 앱 조사와 적용 방향

조사일: 2026-10-24. 각 프로젝트의 공식 저장소 README와 UI 문서를 확인했다. 아래는 실제 문서에서 확인한 동작과 VibeMeters의 구현 방향이며, 별도 성능 측정 없이 다른 앱의 메모리 사용량을 비교하지 않았다.

| 앱 | 공식 자료에서 확인한 UI·기능 | VibeMeters에 적용할 방향 |
| --- | --- | --- |
| [CodexBar](https://github.com/steipete/CodexBar) | 서비스별 사용량 막대와 세션·주간 초기화 시간, 메뉴 막대의 작은 지표, 기존 CLI 인증 재사용 | 서비스별 색상을 얇은 막대에 사용하고 남은 비율과 초기화 시간을 가까이 배치. 공식 CLI 로그인으로 복구 |
| [OpenUsage](https://github.com/robinebers/openusage) | 서비스별 묶음, 중요한 지표 고정, 일반/상세 지표 구분, 밀도 설정, 캐시 표시 후 백그라운드 갱신 | 작은 창은 5시간·주간 핵심 지표에 집중. 상세 설명은 툴팁이나 확장 영역에 배치. 갱신 중 기존 수치 유지 |
| [OpenQuota](https://github.com/deviffyy/OpenQuota) | Windows 지원. 트레이 팝업과 떠 있는 패널, 남은 양/사용한 양 표시, 행 표시·순서 조정, 캐시와 자동 갱신 | 트레이와 데스크톱 위젯을 같은 창으로 제공. 수동·자동 갱신 설정 및 마지막 조회 시점 표시 |

## 참고한 상세 자료

- [CodexBar UI 문서](https://github.com/steipete/CodexBar/blob/main/docs/ui.md), [공식 README의 화면 자료](https://github.com/steipete/CodexBar/blob/main/docs/codexbar.png): 서비스별 막대, 초기화 카운트다운과 간결한 메뉴 구조.
- [OpenUsage 대시보드 문서](https://github.com/robinebers/openusage/blob/main/docs/dashboard.md): 항상 표시하는 지표와 요청 시 표시하는 지표를 분리하고, 남은 양 및 초기화 시간의 대체 표기를 툴팁으로 제공.
- [OpenUsage 설정 문서](https://github.com/robinebers/openusage/blob/main/docs/settings.md), [README 화면 자료](https://github.com/robinebers/openusage/blob/main/assets/screenshot.jpg): 밀도 설정과 서비스별 지표 구성.
- [OpenQuota README](https://github.com/deviffyy/OpenQuota#features): Windows 트레이·플로팅 대시보드와 캐시 갱신 동작.

## 이번 개선의 우선순위

1. **로그인 복구:** 새 CLI 인증을 읽는 즉시 이전 인증의 실패 캐시를 해제한다. 앱의 로그인 동작은 설치된 공식 CLI의 인증 절차를 연다.
2. **반응형 정보 밀도:** 기본 화면에서도 수직 여백을 줄인다. 매우 작은 창에서는 핵심 비율을 유지하고 보조 메타데이터를 줄인다. 크기·위치·컴팩트 선호를 저장한다.
3. **신뢰 가능한 상태:** 남은 비율의 의미를 명확히 하고 초기화 시간·최근 조회 시점을 제공한다. 실패 시 이전 수치를 현재 값으로 오인하지 않도록 안내하고 과도한 재시도를 제한한다.

사용량 이력 분석, 다중 계정 및 복잡한 소비 예측은 참고 앱의 확장 기능이다. 이번 변경에서는 작은 상주 창과 인증 복구에 집중한다. 공급자의 비공개 사용량 API는 향후 변경될 수 있다.

## 추가 개선: 서비스 선택과 스크롤 없는 위젯

- 설정에서 Codex·Claude·Gemini 표시 여부를 직접 선택한다. 숨긴 서비스는 작은 창의 공간을 차지하지 않도록 한다.
- 같은 영역을 단순히 잘라내는 대신 창의 높이·너비와 표시 서비스 수에 맞춰 여백·행 높이·표시 지표 수를 조정한다. 좁을 때는 서비스 이름과 핵심 남은 비율을 우선하고, 초기화 시각·모델별 세부 정보는 툴팁으로 제공한다.
- 설정은 위젯과 분리된 읽기 쉬운 창에 서비스 선택, 갱신, 창 동작, 알림을 묶는다. 트레이에서 설정과 표시 상태에 접근할 수 있도록 한다.
- 작업표시줄은 창 접근용으로 사용하고, 작업표시줄 진행률은 표시하지 않는다.

## Gemini 데이터 소스 조사

Google 공식 [할당량 문서](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/quota-and-pricing.md#check-usage-and-limits)는 `/stats model`로 현재 사용량과 할당량을 확인하도록 안내한다. 이 문서는 Gemini CLI·Code Assist 할당량을 다루며, Gemini 웹 앱과 API 키·Vertex AI의 사용량을 하나로 간주하면 안 된다.

공식 소스의 구현 지점:

| 소스 | 구현에 필요한 사실 |
| --- | --- |
| [statsCommand.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/ui/commands/statsCommand.ts) | `/stats` 기본 동작은 할당량·크레딧 갱신을 호출한다. `/stats model`은 이미 메모리에 있는 pooledRemaining·pooledLimit·pooledResetTime을 표시한다. |
| [types.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/code_assist/types.ts) | 내부 할당량 응답은 모델별 buckets이며 remainingFraction·resetTime 등이 선택 필드다. 누락된 비율을 0 또는 100으로 간주하지 않는다. |
| [storage.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/config/storage.ts), [oauth2.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/code_assist/oauth2.ts) | CLI의 기본 인증 파일 이름은 oauth_creds.json이며 별도 암호화 저장 옵션도 있다. 파일 존재만으로 로그인 유효성이나 할당량을 확정할 수 없다. |

현재 Google 공식 [FAQ](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/faq.md#why-cant-i-use-third-party-software-like-claude-code-openclaw-or-opencode-with-gemini-cli)와 [약관 안내](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/tos-privacy.md)는 제3자 도구가 CLI OAuth를 재사용하여 Code Assist 백엔드에 직접 접근하는 방식을 제한한다고 명시한다. 따라서 공식 CLI에서 사용량을 열거나, 사용자가 제공한 비밀 정보 없는 할당량 스냅샷을 읽는 방식을 우선 검토한다. 공식 소스에서 문서화된 외부용 할당량 JSON 내보내기는 확인되지 않았으므로 이를 이미 제공되는 기능처럼 안내하지 않는다.

로컬 토큰 수로 서버의 남은 요청 할당량을 추정해 표시하지 않는다. 측정값을 얻지 못하면 확인 필요 상태와 공식 사용량 화면 접근을 제공한다.

### 공식 CLI 사용량 창 열기

공식 [CLI 옵션 문서](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/cli-reference.md)는 `--prompt-interactive`(`-i`)로 초기 입력을 실행하고 대화형 창을 유지하는 방식을 지원한다. 소스에서 [초기 입력 처리](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/ui/AppContainer.tsx)와 [슬래시 명령 처리](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/ui/hooks/useGeminiStream.ts)를 따라가면 `gemini --prompt-interactive '/stats'`로 공식 CLI의 통계 화면을 여는 방식을 도출할 수 있다. 이는 소스에 근거한 구현 판단이며 이번 변경에서 실행 확인하지 않았다. 로그인 완료 뒤 초기 명령을 처리하므로 로그인·사용량 접근 버튼에 적합하다. CLI에서 직접 입력하는 `/stats model`도 공식 문서에 안내되어 있다.

`gemini -p '/stats' --output-format json`을 할당량 내보내기로 취급하면 안 된다. [비대화형 UI 구현](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/ui/noninteractive/nonInteractiveUi.ts)은 텍스트 정보만 출력하고, 통계 명령이 생성하는 STATS·MODEL_STATS 구조를 출력하지 않는다. [headless 문서](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/headless.md)의 JSON stats 역시 토큰·지연 통계를 설명하며 계정의 남은 구독 할당량 API를 약속하지 않는다.

### 최종 구현 방향: 앱 내부 표시를 위한 CLI 코어 브리지

앱 내부에 수치를 표시하는 요구에 따라, 공식 패키지 `@google/gemini-cli-core`를 짧게 실행되는 숨겨진 Node 프로세스에서 불러오는 방향으로 변경한다. 앞의 CLI 통계 창은 보조 접근 경로다. 브리지는 공식 런타임의 인증·갱신 처리와 `CodeAssistServer`의 할당량 메서드를 사용하고 모델 생성 요청은 호출하지 않는다. VibeMeters의 .NET 코드가 Google OAuth 토큰이나 요청 프로토콜을 재구현하는 방식과 구분된다. **공식 코드의 재사용이 제3자 모니터링용 공개 API 또는 Google의 지원 승인을 의미하지는 않는다.** 공급자 정책과 패키지 변경의 영향을 받는 통합이다.

소스를 읽어 확인한 의존 경계:

- 조사한 로컬 npm 설치는 Gemini CLI 0.17.1이며, 해당 코어에는 `retrieveUserQuota`가 없다. 전역 CLI를 바꾸지 않고 앱 전용 위치에 필요한 공식 코어 버전을 고정해 설치하는 방향을 사용한다.
- 공식 태그 [v0.19.0 server.ts](https://github.com/google-gemini/gemini-cli/blob/v0.19.0/packages/core/src/code_assist/server.ts)에는 없고 [v0.20.0 server.ts](https://github.com/google-gemini/gemini-cli/blob/v0.20.0/packages/core/src/code_assist/server.ts)에는 `retrieveUserQuota` 메서드가 있다. 버전 문자열뿐 아니라 실제 메서드 유무로 가용성을 판단한다.
- [코어 export](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/index.ts)는 `getOauthClient`, `AuthType`, `CodeAssistServer`, `Config`를 제공한다. `Config.refreshUserQuota()`는 더 높은 수준의 진입점이지만 초기화된 contentGenerator·project를 요구하고 실패를 undefined로 바꾸므로, 작은 브리지에서는 OAuth·server 메서드를 직접 조합하는 편이 오류 구분에 유리하다.
- OAuth 런타임에는 사용 가능한 로그인 정보가 없을 때 대화형 인증으로 전환하는 경로가 있다. 백그라운드 조회에서는 브라우저 실행 억제와 비대화형 설정을 제공해 이를 인증 필요 상태로 바꾸고, 로그인 버튼을 눌렀을 때만 공식 로그인 절차를 연다.
- `setupUser`는 신규 계정 온보딩을 시작할 수 있다. 조회만 하는 브리지는 `loadCodeAssist`로 계정의 기존 project·tier를 얻고, 해당 project로 `retrieveUserQuota`를 호출한다. 프로젝트가 없으면 임의 프로젝트를 만들거나 선택하지 않는다.
- 반환되는 모델별 비율·초기화 시각만 정규화하여 앱에 전달한다. 토큰을 stdout·오류 메시지·설정에 전달하지 않는다. 짧은 실행 시간 제한과 provider 캐시를 두어 Node 프로세스가 계속 상주하지 않도록 한다.

위 내용은 설치된 패키지와 공식 저장소 소스를 읽은 구현 조사다. 사용자 요청에 따라 브리지 실행·로그인 조회·빌드·테스트는 진행하지 않았다.

## 테마 구성 권고

라이트·다크와 선택 가능한 강조색은 사용량의 의미와 독립적으로 적용한다. 표면·카드·테두리·주요 글자·보조 글자·입력·포커스 색을 한 테마 사전에서 바꾸어 설정 창과 위젯의 일관성을 유지한다. 서비스 색과 부족 경고는 모든 테마에서 읽히도록 구분하고, 드롭다운·체크박스·툴팁도 함께 스타일을 적용한다. 테마 선택은 즉시 미리 보고 저장할 수 있도록 한다.

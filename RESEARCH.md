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

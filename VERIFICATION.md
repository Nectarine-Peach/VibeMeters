# 최종 검증 — 2026-10-24

Windows x64 / .NET SDK 8.0.426 / 자체 포함 Release 배포본에서 검증했습니다.

| 검증 | 결과 |
|---|---|
| Release 빌드 및 win-x64 publish | 오류 0, 경고 0 |
| HTTP·파서·인증·설정 회귀 테스트 | 57 통과, 0 실패 |
| 배포 EXE `--smoke-test` | 렌더링, 작업표시줄, 항상 위, 최소화, 숨김, 복원, 설정 통과 |
| 실제 WPF 이미지 검토 | 기본창 380×536, 설정창 380×714에서 카드·버튼 표시 확인 |
| 실제 계정 Codex 조회 | HTTP 200, 구독 한도·남은 비율·초기화 시간 표시 확인 |
| 실제 계정 Claude 조회 | 현재 로컬 인증 만료. 로그인 필요 안내와 사용량 페이지 버튼 확인 |
| Windows UI Automation | 핀 켜기/끄기, 설정 열기/닫기, 트레이 숨김, 재실행 복원 통과 |
| 중복 실행 | 두 번째 프로세스 종료, 기존 창 복원, 앱 프로세스 1개 유지 |
| 트레이 데모 유휴 측정 | 20초간 CPU 시간 증가 0ms, 자식 프로세스 0개 |

유휴 메모리 표본: 작업 집합 203.0MiB, 전용 메모리 104.4MiB, 스레드 15개. .NET/WPF/Windows Forms 트레이의 기본 비용이 포함됩니다. 이는 이 환경의 짧은 측정이며 장시간 누수 테스트나 모든 PC의 수치를 보장하지 않습니다. 별도 브라우저·웹뷰·상주 CLI 프로세스는 실행하지 않습니다.

최종 배포 EXE SHA-256:

```text
E3C10FD9C633B77285AE3684269836878A4968CE4F79C2048ACCD16B8A94DF94
```

검증에 사용한 계정 토큰은 기록·복사·수정하지 않았습니다. Claude의 정상 인증 후 HTTP 200 경로는 모의 응답으로 검증했으며, 현재 계정의 성공 응답은 확인하지 못했습니다. 실제 Windows 자동시작 후 재부팅, 절전·잠금·다중 모니터 및 다른 DPI 환경은 이 세션에서 직접 재현하지 않았습니다. 관련 설정·이벤트 처리 코드는 포함돼 있습니다.

실행 파일: `dist/VibeMeters-win-x64/VibeMeters.exe`  
전달 ZIP: `dist/VibeMeters-win-x64.zip`  
시각 검증 이미지: `dist/VibeMeters-win-x64/artifacts/window.png`, `settings.png`  
자동 UI 검증 결과: `dist/VibeMeters-win-x64/artifacts/smoke-test.txt`

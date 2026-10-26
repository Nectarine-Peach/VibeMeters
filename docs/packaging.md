# Portable 패키징

Release 구성·.NET self-contained·단일 .NET EXE는 유지합니다. Gemini는 공식 core 0.65.0의 인증·프로젝트 탐지·할당량 코드를 그대로 사용하되, esbuild 0.25.12로 필요한 모듈 그래프를 하나의 `quota.mjs`에 묶습니다. 기존 v1.4.3과 같은 Node 24.22.0 Windows x64를 동봉하므로 사용자 PC의 Node/npm은 필요하지 않습니다. 앱 설정·CLI 인증 위치와 로그인 동작은 바꾸지 않습니다.

`scripts/publish.ps1`은 잠금 파일을 매번 복원하고 새 출력 경로만 사용합니다. 빌드용 node_modules는 배포 폴더로 복사하지 않습니다. Node 버전이 다르거나 출력 경로가 이미 있으면 실패하며 `-Node`와 `-OutputDirectory`로 경로를 지정할 수 있습니다. Node를 갱신할 때는 package.json의 고정 버전과 실제 빌드 Node를 함께 갱신하고 오프라인 검증을 다시 수행하세요.

npm은 PATH의 설치 도구를 사용하며 npm 자체의 버전은 고정하지 않습니다. 번들링·오프라인 검사·배포에는 `-Node`로 선택한 고정 버전을 사용합니다. npm lifecycle scripts는 실행하지 않으며 의존성 그래프는 package-lock.json으로 고정합니다.

번들에는 WASM binary loader를 적용합니다. Windows 브라우저 실행은 공식 `open` 모듈의 Windows PowerShell 경로를 사용하므로 Linux용 xdg-open 자산은 필요하지 않습니다. optional keytar/pty는 이전 production 설치에서도 제외되었으며 공식 저장소의 파일 fallback 정책을 유지합니다. optional 터미널 색상·WebSocket 가속·문자 인코딩·macOS 온도 센서의 guarded 외부 require만 허용하며 새 외부 의존성이 생기면 빌드가 실패합니다. 공급자 내부 API 의존성은 그대로 남습니다.

패키지의 runtime-manifest.json에는 core/esbuild/Node 버전, 잠금 파일·번들·Node 해시를 기록합니다. 공식 core·Node 라이선스와 번들에 기여하는 npm 패키지의 라이선스/NOTICE를 보존합니다. 번들러 도달 그래프는 배포 ZIP 밖의 `.gemini-bundle-meta.json`에 남습니다.

```powershell
.\scripts\publish.ps1 -OutputDirectory .\dist\VibeMeters-portable-win-x64
node .\runtime\gemini-quota\verify-runtime.mjs .\dist\VibeMeters-portable-win-x64\runtime\gemini-quota
dotnet run --project tests/VibeMeters.Tests.csproj -c Release
```

패키징 과정의 runtime 검사는 저장소 밖 임시 폴더에 번들과 package.json만 복사하여 실행합니다. 빈 임시 홈을 사용하고 네트워크·브라우저/자식 프로세스 실행을 차단합니다. SDK 전체 로드, mock client를 통한 공식 loadCodeAssist/retrieveUserQuota, 로그인 없는 인증 필요 상태, manifest 해시를 검사합니다. 실제 Google 계정의 로그인/조회 성공을 입증하는 검사는 아닙니다. 최종 패키징 후 이 검사와 기존 .NET 회귀 테스트 결과를 따로 기록합니다.

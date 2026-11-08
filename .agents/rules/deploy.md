---
trigger: always_on
description: AccountUsageUI build, deploy, and auto-restart policy
---

# AccountUsageUI Build, Deploy & Restart Policy

코드나 UI를 변경/업데이트한 후에는 항상 루트의 실행 파일을 최신화하고 재실행해야 합니다:

1. **대상**: `C:\Users\joajo\AndroidStudioProjects\AccountUsageUI\AccountUsageUI.exe`
2. **동작**:
   - 이미 실행 중인 `AccountUsageUI` 프로세스가 있다면 안전하게 중지
   - `dotnet publish`로 최신 win-x64 단일 파일 바이너리 빌드
   - `AccountUsageUI.exe` 위치로 덮어쓰기 복사
   - `Start-Process "explorer.exe" -ArgumentList "C:\Users\joajo\AndroidStudioProjects\AccountUsageUI\AccountUsageUI.exe"`로 자동 재실행

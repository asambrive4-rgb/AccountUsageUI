# AccountUsageUI Workspace Rules & Guidelines

## 1. 배포 및 실행 자동화 지침 (필수 준수 사항)

코드 또는 UI를 수정한 뒤(업데이트 이후)에는 **반드시 항상** 아래 절차에 따라 루트 실행 파일(`AccountUsageUI.exe`)을 최신 빌드로 갱신하고 재실행해야 합니다.

### 대상 경로
- `C:\Users\joajo\AndroidStudioProjects\AccountUsageUI\AccountUsageUI.exe`
- 사용자가 폴더에서 바로 더블클릭하여 항상 최신 기능을 즉시 실행할 수 있도록 보장해야 합니다.

### 자동 빌드·복사·재실행 표준 절차
코드 변경/업데이트 작업이 완료되면 다음 명령을 반드시 자동으로 순차 실행합니다:

```powershell
# 1. 이미 실행 중인 경우 프로세스 종료 (파일 잠금 해제)
Get-Process AccountUsageUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. Release 단일 실행 파일 퍼블리시 빌드
dotnet publish .\src\AccountUsageUI.App\AccountUsageUI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 3. 루트 디렉터리의 AccountUsageUI.exe로 최신 빌드 덮어쓰기 복사
Copy-Item .\src\AccountUsageUI.App\bin\Release\net10.0-windows\win-x64\publish\AccountUsageUI.exe .\AccountUsageUI.exe -Force

# 4. Windows 탐색기(셸)를 통해 독립 데스크톱 프로세스로 자동 재실행
Start-Process "explorer.exe" -ArgumentList "C:\Users\joajo\AndroidStudioProjects\AccountUsageUI\AccountUsageUI.exe"
```

---

## 2. 디자인 및 UI 가이드라인
- **2열 종대 그리드 레이아웃**: 메인 창의 프로필 카드는 항상 `UniformGrid Columns="2"`를 유지하여 좌우 빈 공간을 방지하고 밀도 있는 대시보드 뷰를 제공합니다.
- **폰트 굵기 원칙**:
  - 작은 텍스트(11pt ~ 12.5pt): `FontWeight="Normal"` (작은 글자 뭉개짐 방지 및 가독성 확보)
  - 중간 텍스트 / 버튼: `FontWeight="Medium"`
  - 대제목 / 카드 이름: `FontWeight="SemiBold"`

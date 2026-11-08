# AccountUsageUI (Antigravity Account Switcher & Model Quota Monitor)

**AccountUsageUI**는 Windows 환경에서 **Google Antigravity 데스크톱 앱**의 다중 계정 프로필을 안전하게 전환하고, **모델별 남은 사용 쿼터(%) 및 리셋 시각**을 실시간으로 모니터링하는 경량 네이티브 WPF 유틸리티입니다.

---

## 주요 기능

| 영역 | 상세 기능 |
| :--- | :--- |
| **계정 캡처 및 전환** | - **현재 로그인된 계정 바로 등록**: 실행 중인 Antigravity를 재시작하지 않고 현재 로그인된 인증 세션을 즉시 프로필로 저장<br>- **새 계정으로 로그인하여 등록**: 기존 세션을 안전하게 백업(`recovery.json`)한 뒤 새 Google 계정 로그인 및 캡처<br>- **워크스페이스 데이터 보존**: `%USERPROFILE%\.gemini\antigravity`의 대화 내역, 브레인, 스킬 설정은 그대로 유지하고 인증 자격 증명(`gemini:antigravity` Keyring + `credential-user-scope`)만 원자적으로 교체 |
| **모델별 쿼터 모니터링 (`Usage`)** | - **Antigravity 네이티브 `Usage` 팝업과 100% 동일한 구조**: `RetrieveUserQuotaSummary` RPC를 호출하여 `Gemini Models` (`Weekly limit`, `5h limit`), `Claude and GPT models` (`Weekly limit`, `5h limit`), `Gemini 4 Argon` (`Weekly limit`) 버킷을 실시간 표시<br>- **활성 프로필**: 로컬 `language_server.exe` Connect RPC (`RetrieveUserQuotaSummary` / `GetUserStatus`) 호출<br>- **비활성(대기) 프로필**: 저장된 OAuth 토큰(만료 시 자동 갱신)으로 CloudCode 엔드포인트(`v1internal:retrieveUserQuotaSummary` / `v1internal:fetchAvailableModels`)를 직접 호출해 앱 전환 없이도 대기 계정의 남은 쿼터 확인<br>- **원형 도넛 링 게이지 & 조건부 리셋 시간**: 각 버킷 우측에 `XX% left` 및 원형 도넛 링 게이지 표시, 쿼터가 차감되어 서버에서 `description`이 내려올 때만 하단에 `Resets in 6d 23h` 자동 노출 |
| **동적 표시 변수 및 그룹 필터링** | - 표시 문구(`Usage`, `% left`, `Resets in `, `Weekly limit`, `5h limit` 등)와 서버 그룹/버킷 메타데이터를 하드코딩하지 않고 `UsageDisplaySettings` 변수 및 응답 필드 기반으로 동적 렌더링하여 추후 Antigravity 업데이트 시 자동 반영<br>- 상단 **`주요 모델`** 설정 창에서 표시할 쿼터 그룹을 체크/해제하거나 커스텀 그룹 이름을 직접 추가 가능 |
| **항상 위 미니 팝업 (`ProfilePopupWindow`)** | - 활성 프로필 카드의 **`미니 팝업`** 클릭 시 컴팩트한 항상 위(Topmost) 오버레이로 전환<br>- **최근 사용 모델**의 남은 쿼터(%)와 리셋 시간, 원클릭 프로필 전환 버튼 제공 |
| **디자인 시스템** | - `deckdeckdeck`의 **Bibata-Warm-Ivory** 테마 적용 (`#F2EEE7` 웜 아이보리 배경, `#FCFAF6` 크림 카드, `#1E1B18` `2.5px` 잉크 보더, `#C7D7E8` 스카이 슬레이트 포인트, 내장 `Outfit`/`SUIT`/`NanumSquare Neo` 폰트 및 순수 벡터 아이콘) |

---

## 프로젝트 구조

추후 다른 AI 코딩 도구(Codex, Claude 등)도 플러그인 형태로 추가할 수 있도록 **Clean Architecture (Ports & Adapters)** 구조로 설계되어 있습니다.

```text
AccountUsageUI/
├── AccountUsageUI.slnx
├── AccountUsageUI.exe                        # 단일 파일 배포 실행 파일 (win-x64)
├── src/
│   ├── AccountUsageUI.Core/                  # 도메인 모델, 포트 인터페이스, 유스케이스
│   │   ├── Installation/                     # 앱 설치 감지 유스케이스
│   │   ├── Profiles/                         # 프로필 등록/즉시 캡처/실행/전환/삭제 유스케이스
│   │   └── Usage/                            # 모델별 쿼터 모델, 주요 모델 필터 및 최근 사용 모델 선정 정책
│   ├── AccountUsageUI.Infrastructure/        # Windows DPAPI 저장소 및 Antigravity 전용 어댑터
│   │   ├── Profiles/                         # DPAPI 암호화 + ACL 보호 프로필 저장소 (WindowsProfileStore)
│   │   ├── Providers/Antigravity/            # Windows Credential Manager 연동, 프로세스 제어, 쿼터 리더
│   │   └── Usage/                            # 쿼터 스냅샷 캐시 및 주요 모델 설정 JSON 저장소
│   └── AccountUsageUI.App/                   # WPF UI (Bibata-Warm-Ivory 테마, 메인 창, 미니 팝업, 트레이)
└── tests/
    └── AccountUsageUI.Tests/                 # MSTest 단위 테스트 스위트
```

---

## 빌드 및 실행 명령어

### 1. 단위 테스트 실행
```powershell
dotnet test .\AccountUsageUI.slnx
```

### 2. 개발 모드 실행
```powershell
dotnet run --project .\src\AccountUsageUI.App\AccountUsageUI.App.csproj
```

### 3. 단일 실행 파일(`AccountUsageUI.exe`) 퍼블리시 & 자동 갱신 재실행
기존 실행 중인 인스턴스를 안전하게 종료하고 최신 바이너리를 루트 디렉터리에 덮어쓴 뒤 자동으로 재실행합니다:
```powershell
Get-Process AccountUsageUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
dotnet publish .\src\AccountUsageUI.App\AccountUsageUI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item .\src\AccountUsageUI.App\bin\Release\net10.0-windows\win-x64\publish\AccountUsageUI.exe .\AccountUsageUI.exe -Force
Start-Process "explorer.exe" -ArgumentList "C:\Users\joajo\AndroidStudioProjects\AccountUsageUI\AccountUsageUI.exe"
```

---

## 데이터 저장 위치

- **프로필 및 암호화 자격 증명**: `%LOCALAPPDATA%\AccountUsageUI\Profiles\v1\` (Windows DPAPI `CurrentUser` 암호화 및 사용자 전용 ACL 적용)
- **전환/로그인 크래시 복구 백업**: `%LOCALAPPDATA%\AccountUsageUI\LoginRecovery\v1\`
- **쿼터 캐시 및 주요 모델 설정**:
  - `%LOCALAPPDATA%\AccountUsageUI\usage-snapshots.json`
  - `%LOCALAPPDATA%\AccountUsageUI\user-settings.json`
  - `%LOCALAPPDATA%\AccountUsageUI\ui-settings.json`

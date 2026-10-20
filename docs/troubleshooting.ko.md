---
title: "문제 해결"
description: "빌드, 설정, 시작, 네트워크, 영속성과 SDK 장애를 순서대로 진단합니다."
updated: 2026-10-20
---

## 최초 설정에서 프록시 요청 거부 {#setup-proxy-refused}

**증상.** 관리자 생성 전에 `/api/setup/*` 요청이 HTTP 403과 `setup_proxy_refused`를 반환합니다. **원인.** 요청에 `X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto` 또는 `Forwarded` 헤더가 있습니다. 역방향 프록시는 원격 요청을 로컬 요청처럼 보이게 할 수 있습니다. **해결.** 프록시를 거치지 않고 서버에서 직접 설정 페이지를 엽니다. 관리자 등록 후에는 이 최초 설정 제한이 적용되지 않지만 일반 인증은 계속 필요합니다.

관리자 로그인 실패는 연결 출발지 IP별로 집계하며 `installation.login_attempts`와 `login_window_seconds`를 사용합니다. `login_address_capacity`의 기본값은 1024개 주소이며 가득 차면 가장 오래된 시간 창을 교체합니다. 전달 헤더는 로그인 집계 주소를 변경하지 않습니다.

## 이 페이지의 범위

이 페이지는 "서버가 동작하지 않는다"를 순서 있는 조사로 바꿉니다. 무엇을 먼저 확인하고, 증거가 어디에 있으며, 어떤 변경으로 문제를 해결하는지 다룹니다. 모든 증상은 같은 구조를 따릅니다: 증상, 확인, 가능한 원인, 해결, 정상 확인. 소스 체크아웃과 릴리스 디렉터리 모두에 적용되지만, 실제 게임 클라이언트로 수행하는 테스트를 대신하지는 않습니다.

**이 페이지의 대상.** 서버를 처음 시작하는 운영자와 빌드나 검증 게이트가 실패한 기여자입니다.

**시작하기 전에.**

- 소스 체크아웃은 저장소 루트에서, 게시된 빌드는 릴리스 디렉터리에서 작업합니다.
- 로컬 확인 시 `.env.example`을 git에서 무시되는 `.env`로 복사하고 `BFF_MYSQL_PASSWORD`를 비어 있지 않은 임시 값으로 설정합니다. MySQL을 비활성화해도 profile 보간에는 이 변수가 필요합니다.
- 이 페이지의 모든 로컬 Host 명령에는 `--patch tools/local-checks/isolated-memory.patch.yaml`이 포함됩니다. 이 패치는 `persistence.mysql`을 비활성화하고 `persistence.memory`를 활성화하므로, 개발자의 `.env`나 로컬 패치가 서버를 실제 데이터베이스로 향하게 하지 않습니다.
- 첫 번째 오류를 읽고, 시도마다 한 가지만 바꾸며, 실패 출력을 보관합니다.

## 먼저 확인할 것

| 증상 | 먼저 확인할 것 | 가능한 영역 | 참고 |
| --- | --- | --- | --- |
| 빌드가 끝나지 않음 | `dotnet build BubbleFighter.Fantasy.sln -c Release`를 다시 실행하고 첫 오류를 읽음 | 도구 모음, 패키지 복원, 경고를 오류로 처리 | 도구 모음과 빌드 |
| `error:`가 환경 변수나 profile을 지목함 | 격리 패치와 함께 `--dump-config` 실행 | profile, 패치, `.env` 보간 | 설정 |
| `error: Server startup did not converge` | 나열된 각 행 id와 상태를 읽음 | 활성화, 종속성, 클라이언트 데이터 | 시작과 활성화 |
| 행이 계속 `Pending` 상태임 | `/api/plugins`에서 `state`와 `error`를 읽음 | 누락된 서비스, 제공자 또는 정의 출처 | 시작과 활성화 |
| 행이 `Failed`, `CleanupIncomplete` 또는 `CleanupFailed`임 | 같은 JSON을 본 뒤 콘솔 로그 확인 | 활성화 또는 정리 소유권 | 시작과 활성화 |
| 클라이언트가 연결할 수 없음 | 수신 포트와 공개 주소 | 바인딩, NAT, 방화벽, relay | 포트와 네트워크 |
| 관리 API 또는 WebUI에 접속할 수 없거나 401 | `/health`, `BFF_ADMIN_HOST` | 관리 수신과 로그인 | 포트와 네트워크 |
| 데이터베이스 오류 또는 재시작 후 데이터 사라짐 | `--dump-config`; 영속성 행 | MySQL, SQLite, 마이그레이션, 메모리 저장 | 영속성 |
| `clientdata` 활성화 실패 | 오류에 나온 두 경로 | 클라이언트 데이터 파일과 `BFF_CLIENT_DATA` | 클라이언트 데이터 |
| SDK 플러그인 실패 또는 시간 초과 | 플러그인 stderr와 loader 행 설정 | JSON-RPC stdout, 프로토콜, 제한 | SDK 플러그인 |

해당하는 항목이 없으면 "문제 보고" 절로 바로 이동합니다.

## 도구 모음과 빌드 문제

### .NET SDK가 없거나 버전 10이 아님

**증상.** `dotnet`을 찾을 수 없거나, `net10.0` 대상 빌드가 거부됩니다. **확인.** `dotnet --info`를 실행합니다. 설치된 SDK와 런타임이 나오며, 모든 프로젝트는 `net10.0`을 대상으로 합니다. **가능한 원인.** .NET 10 SDK가 설치되어 있지 않거나 `PATH`가 이전 버전을 가리킵니다. **해결.** .NET 10 SDK를 설치하고 새 터미널을 엽니다. **정상 확인.** `dotnet --version`이 10.x를 보고하고 `dotnet build BubbleFighter.Fantasy.sln -c Release`가 `0 Warning(s)`와 `0 Error(s)`로 끝납니다.

### 패키지 복원 또는 컴파일 실패

**증상.** 빌드가 NuGet 복원 단계에서 네트워크나 피드 오류로 멈추거나, 컴파일러가 경고를 보고하며 실패합니다. **확인.** 첫 복원 오류를 읽고, 그 메시지가 컴파일러 경고인지 판단합니다. 모든 프로젝트에서 `TreatWarningsAsErrors`가 켜져 있고 `GenerateDocumentationFile`은 모든 공개 형식과 멤버에 실제 XML 요약을 요구합니다. **가능한 원인.** 패키지 피드 접근 불가, 프록시 문제, 손상된 패키지 캐시, 누락된 XML 주석, 사용하지 않는 변수 또는 null 허용 관련 오류입니다. **해결.** `dotnet restore`로 다시 시도하고 네트워크나 프록시 설정을 고치며, 경고의 원인을 수정합니다. 빌드를 통과시키기 위해 경고를 억제하지 마세요. **정상 확인.** 빌드가 경고 0개, 오류 0개를 보고하고 패키지나 복원 오류가 없습니다.

원인이 다른 복원 오류가 하나 있습니다. `NU1900`(`Error occurred while getting package vulnerability data`)은 NuGet 패키지 감사가 nuget.org에 연결하지 못했다는 뜻입니다. 감사는 복원할 때마다 실행되며, 이 프로젝트에서는 그 경고가 오류로 처리됩니다. 컴퓨터가 nuget.org에 연결할 수 없지만 필요한 패키지가 이미 로컬 캐시에 있다면 `dotnet build BubbleFighter.Fantasy.sln -c Release -p:NuGetAudit=false`로 빌드합니다. 이 옵션은 온라인 취약성 조회만 건너뛰며 컴파일러 경고는 건너뛰지 않습니다. 릴리스를 게시하기 전에는 감사를 켠 상태로 일반 빌드를 한 번 실행하세요.

### 다른 머신에서 릴리스 빌드가 시작되지 않음

**증상.** 깨끗하게 컴파일된 빌드가 대상 머신에서 즉시 종료됩니다. **확인.** 그 머신에서 `dotnet --list-runtimes`를 실행해 `Microsoft.AspNetCore.App 10.x`가 있는지 확인합니다. Host는 ASP.NET Core 공유 프레임워크를 사용합니다. **가능한 원인.** 머신에 ASP.NET Core Runtime 10이 없습니다. 프레임워크 종속 게시는 런타임을 포함하지 않습니다. **해결.** 대상 플랫폼용 ASP.NET Core Runtime 10을 설치합니다. 게시 스크립트의 설명은 `The deployment server requires the x64 ASP.NET Core Runtime 10.0, which includes the .NET runtime.`입니다. **정상 확인.** 릴리스 디렉터리에서 시작하면 콘솔이 `Server started with <N> active row(s).`를 보고합니다.

### 검증 도구 모음이 불완전함

**증상.** `python tools/ci/serial-gates.py`가 테스트 전에 멈추거나 SDK 적합성 게이트가 건너뛰기로 보고됩니다. **확인.** 게이트는 `PATH`에 `dotnet`, `node`, `npm`, `go`, `cargo`, `rustup`이 필요하며, 없으면 `Missing validation toolchains: <names>`를 출력합니다. **가능한 원인.** .NET 빌드만 준비된 개발 머신입니다. **해결.** 누락된 도구 모음을 설치하고, Rust SDK와 WebAssembly 테스트를 위해 실패 메시지에 나온 대상을 추가합니다: `rustup target add wasm32-wasip1`. **정상 확인.** 다시 실행했을 때 `An SDK conformance gate was skipped.`가 출력되지 않고 `PASS public gates:` 또는 `PASS complete gates:` 한 줄로 끝납니다.

## 설정과 보간 문제

### 환경 변수에 값이 없음

**증상.** `error: File '<file>', row '<row id>': environment variable '<NAME>' is not set and has no default.` **확인.** 오류에 나온 profile, bundle 또는 패치에서 `:-default`가 없는 `${NAME}`을 찾고, `.env`와 프로세스 환경을 확인합니다. `start`를 제외한 모든 명령은 profile을 조합하기 전에 저장소 루트의 `.env`를 설정되지 않은 프로세스 변수에 적용하며, 이미 있는 환경 변수가 우선합니다. **가능한 원인.** 변수가 내보내지지도 `.env`에 있지도 않은데, `BFF_MYSQL_PASSWORD`에는 기본값이 없으므로 모든 게임 또는 관리 profile이 이 변수 없이는 조합을 완료하지 못합니다. **해결.** 해당 명령에 변수를 설정하거나 git에서 무시되는 `.env`에 추가합니다. 실제 자격 증명을 커밋하지 마세요. **정상 확인.** 조합이 성공하고 `--dump-config`가 적용된 행을 출력합니다.

### MySQL 비밀번호가 비어 있음

**증상.** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Invalid configuration for row 'persistence.mysql': connection_string is missing Password (BFF_MYSQL_PASSWORD is required and must not be empty).` **확인.** 복사한 `.env.example`에는 `BFF_MYSQL_PASSWORD=`가 들어 있어 보간은 빈 문자열로 성공하고, 실패는 행이 활성화될 때만 나타납니다. **가능한 원인.** `.env`에 빈 자리 표시자가 남아 있습니다. **해결.** 로컬 확인에는 비어 있지 않은 임시 값을 사용하고 격리 패치를 추가해 데이터베이스에 접속하지 않도록 합니다. 실제 자격 증명은 의도적으로 구성한 영속성 배포에만 사용합니다. **정상 확인.** 격리 패치를 사용하면 `persistence.memory`가 활성화되고 로그가 메모리 영속성이 활성화되었음을 알립니다.

### profile, 패치 또는 옵션 이름이 잘못됨

**증상.** 다음 중 하나: `error: Profile '<id>' not found: expected '<path>'.`, `error: Patch file '<path>' does not exist.`, `error: Unknown option '<option>'.`, `error: Unknown command '<command>'.`, `error: Option '<option>' requires a value.` **확인.** `bff profiles list`와 `bff --help`를 실행합니다. profile id는 `profiles/` 아래의 디렉터리 이름이고, 도움말이 허용되는 옵션을 나열합니다. **가능한 원인.** 오타, 존재하지 않는 profile, 또는 해당 옵션을 받지 않는 명령 뒤에 놓인 옵션입니다. **해결.** id나 옵션을 고칩니다. 로컬 실행에는 `--patch tools/local-checks/isolated-memory.patch.yaml`을 유지합니다. **정상 확인.** `bff profiles list`가 설명과 함께 profile을 출력하고 수정한 명령이 조합을 통과합니다.

### 설정 키가 알 수 없거나 유효하지 않음

**증상.** `error: Invalid configuration for row '<row id>': unknown key '<key>'` 또는 같은 행의 형식 변환·검증 실패입니다. **확인.** 플러그인 옵션은 PascalCase 속성에 매핑되는 snake_case YAML 키이며, 플러그인의 옵션 클래스가 허용하는 키를 나열합니다. 메시지의 행 id와 그에 대응하는 `plugin` 필드를 확인합니다. **가능한 원인.** 키 오타, 오래된 안내서의 키, 또는 잘못된 형식·범위의 값입니다. **해결.** 키나 값을 고칩니다. 한 행의 모든 실패는 한 메시지에 모이므로 전부 수정합니다. **정상 확인.** `--dump-config`가 성공하고 수정된 값을 보여 줍니다.

### 적용된 설정이 예상과 다름

**증상.** 서버가 마치 어떤 행이 활성화되었거나 직접 수정한 것과 다른 포트를 쓰는 것처럼 동작합니다. **확인.** 계층은 bundle, profile 패치, 마지막으로 명령줄 패치 순서로 적용됩니다. Host를 시작하지 않고 결과를 출력합니다:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

**가능한 원인.** 뒤의 계층이 해당 행이나 `disabled` 플래그를 대체했습니다. 격리 패치가 마지막 계층이며 메모리 영속성을 활성화합니다. **해결.** 값을 소유해야 하는 계층을 수정합니다. 같은 재정의를 두 계층에 추가하지 마세요. **정상 확인.** 덤프가 예상 플래그와 값을 보여 줍니다. 덤프에는 보간된 MySQL 연결 문자열 같은 비밀이 들어 있을 수 있으므로 공유 전에 가립니다.

## 시작, 활성화 및 플러그인 상태

시작 시도를 마치면 모든 행이 상태를 게시합니다. 콘솔 로그나 관리 API에서 읽습니다:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins`는 런타임 인스턴스마다 `rowId`, `plugin`, `source`, `state`, `error`, `codeRefreshPolicy`, `codeLifetime`, `restartRequiredForReclamation`, `isRetiring`, `overrides`, `version`, `contractVersions`, `supportedBuilds` 필드를 가진 객체를 반환합니다. `Active`만이 정상 실행 상태입니다.

| 상태 | 의미 | 처리 방법 |
| --- | --- | --- |
| `Active` | 활성화되어 effect가 살아 있음 | 조치 없음 |
| `Pending` | 원하는 상태이지만 서비스, 제공자 또는 정의 출처가 없음 | `error`를 읽고 종속성을 고침 |
| `Failed` | 활성화 또는 소유 작업 실패 | `error`를 읽고 원인을 고친 뒤 재로드 또는 재시작 |
| `Unloading` | effect가 닫히고 정리 중 | 기다림. 같은 행을 다시 시작하지 않음 |
| `Unloaded` | 비활성화되었거나 효과 없이 언로드됨 | 비활성화된 행의 정상 상태 |
| `CleanupIncomplete` | 정리가 제한 시간을 넘겨 진행 중이거나 취소됨 | 소유권이 유지됨. 재시도 전에 조사 |
| `CleanupFailed` | 정리가 오류를 보고함 | 리소스가 해제되었다고 가정할 수 없음. 재시작 거부됨 |
| `WorkFailed` | 소유 백그라운드 작업 실패 | 정리가 끝날 때까지 리소스 유지 |

### 서버 시작이 수렴하지 않음

**증상.** 프로세스가 종료 코드 1로 끝나며 `error: Server startup did not converge (WaitingForDependencies): <row id>: Pending (<diagnostic>); <row id>: Failed (<diagnostic>).`를 출력합니다. 콘솔은 또한 `Lifecycle revision <N> for '<row id>' did not converge.`를 기록합니다. **확인.** 나열된 모든 행을 읽고 `/api/plugins`를 `--dump-config` 덤프와 비교합니다. **가능한 원인.** 비활성화되지 않은 하나 이상의 행이 `Active`에 도달하지 못했습니다. 제공자 누락, 클라이언트 데이터 누락, 잘못된 포트 또는 바인딩에 실패한 값입니다. **해결.** 목록의 마지막이 아니라 첫 번째 행을 고칩니다. 의도적으로 비활성화한 행이라면 적용된 덤프가 실제로 그 행을 비활성화하는지 확인합니다. **정상 확인.** 콘솔이 `Server started with <N> active row(s).`를 보고하고 `GET /health`가 `{"status":"ok", ...}`를 반환합니다.

### 행이 계속 Pending 상태임

**증상.** 행이 활성화되지 않고 `error`가 이유를 설명합니다. 보통 `Waiting for services: <service types>; provider rows: <rows>; override rows: <rows>.` 또는 `Definition source for plugin '<plugin>' is unavailable.`입니다. **확인.** 메시지에 나온 제공자나 정의 출처를 찾아 그 행 상태를 확인합니다. **가능한 원인.** 필수 서비스, 선택적 제공자 또는 핸들러 대체 대상이 비활성화·실패·누락되었거나, 플러그인을 게시하는 SDK loader 행이 아직 활성화되지 않았습니다. **해결.** 메시지에 나온 제공자 행을 활성화하거나 고치고, 해당 기능이 필요 없다면 소비자를 제거합니다. **정상 확인.** 재로드나 재시작 후 행이 `Active`가 되고 시작이 수렴합니다.

### 행이 Failed 상태임

**증상.** `error: Activation of row '<row id>' (plugin '<plugin>') failed: <cause>.` **확인.** `error` 필드와 콘솔의 전체 원인을 읽습니다. 바깥 메시지는 행과 플러그인을, 안쪽 메시지는 실제 문제를 알려 줍니다. 클라이언트 데이터, MySQL, SQLite, 포트, 알 수 없는 키 또는 알 수 없는 플러그인입니다. **가능한 원인.** 플러그인이 활성화 중 검증하는 모든 것입니다. **해결.** 안쪽 원인을 고칩니다. `Automatic activation attempt limit <N> reached for row '<row id>'.`가 나오면 자동 재시도가 멈춘 것이므로 원인을 고친 뒤 명시적으로 재로드합니다. **정상 확인.** 행이 `Active`가 됩니다.

### CleanupIncomplete 또는 CleanupFailed

**증상.** 언로드 중이던 행이 `/api/plugins`에서 `CleanupIncomplete` 또는 `CleanupFailed`로 표시되고, 해당 행의 `error` 필드와 콘솔 로그에 정리 실패 원인이 남습니다. 정리를 시작한 언로드 또는 재로드 호출도 `Row '<row id>' teardown is <state>; ownership and dependencies are retained.`로 실패할 수 있으며, 여기서 `<state>`는 그 행이 도달한 상태입니다. **확인.** 해당 행의 `error`와 콘솔 로그를 읽습니다. `CleanupIncomplete`는 정리가 제한 시간을 넘겨 진행 중이거나 호출자가 사라진 것이고, `CleanupFailed`는 소유 disposer가 예외를 던진 것입니다. **가능한 원인.** 해제 시 블록되는 리소스, 끝나지 않는 소유 백그라운드 작업, 실패하는 disposer입니다. **해결.** 행을 강제로 재시작하지 마세요. 소유권이 남아 있으면 재시작이 거부됩니다(`Activation is refused while failed work or cleanup ownership remains: <row ids>.`). 정리가 끝나게 두거나 서버를 중지합니다. 끝나지 않으면 프로세스를 재시작하고 플러그인을 고칩니다. **정상 확인.** 상태가 `Unloaded`가 되고 서버가 깨끗하게 종료됩니다.

### 플러그인을 알 수 없거나 행 id가 중복됨

**증상.** `error: Row '<row id>' references unknown plugin '<plugin>'.` 또는 `error: Duplicate row id '<row id>' in profile '<profile id>'.` **확인.** `bff plugins list`를 실행해 플러그인 id를 찾고, 해당 어셈블리가 플러그인 루트(Host 옆의 `plugins/` 또는 `--plugin-root`로 지정한 디렉터리) 아래에 있는지 확인합니다. 중복 id라면 두 번째 행을 제공하는 bundle이나 패치를 찾습니다. **가능한 원인.** 오래되었거나 누락된 플러그인 빌드, 복사되지 않은 플러그인 디렉터리, `plugin` 필드 오타, 또는 기존 id를 다시 삽입한 패치입니다. **해결.** 솔루션을 다시 빌드하고(Host 빌드가 `artifacts/plugins`로 `plugins` 디렉터리를 교체합니다), 오래된 사본이 의심되면 `--no-incremental`을 사용하며, 중복 행을 제거하거나 이름을 바꿉니다. **정상 확인.** `bff plugins list`가 id, 버전, 지원 빌드, 출처를 출력하고 `--dump-config`가 해당 행을 한 번만 나열합니다.

### 활성화 시 필수 서비스 누락

**증상.** 실패한 행이 `Row '<row id>' cannot <verb>; missing service(s): <service types>.`를 보고합니다. **확인.** 해당 서비스를 게시해야 하는 행을 찾습니다. 종속성은 행 id가 아니라 선언된 인터페이스입니다. **가능한 원인.** 제공자 행이 비활성화·실패·누락되었거나 `isolated_services`를 통해 별도 서비스 realm에 있습니다. **해결.** 제공자를 활성화하거나 소비자 선언에서 해당 서비스를 제거합니다. 제공자와 소비자가 다른 트리에 있어야 한다면 `parent_id`와 `isolated_services`를 확인합니다. **정상 확인.** 제공자가 먼저 `Active`가 되고, 다음 조정에서 소비자가 활성화됩니다.

## 포트, 바인딩 주소, NAT 및 관리 인증

### 포트가 이미 사용 중임

**증상.** 수신 행이 `error: Activation of row 'game-listener' (plugin 'net.tcp') failed: <operating system socket error>`처럼 활성화에 실패합니다. **확인.** 변경 전에 소유자를 찾습니다. Windows에서는 `Get-NetTCPConnection -LocalPort 7110 -State Listen | Select-Object LocalAddress, LocalPort, OwningProcess`를 실행한 뒤 `Get-Process -Id <owningProcess>`를, Linux에서는 `sudo ss -ltnp | grep :7110`을 실행합니다. **가능한 원인.** 다른 서버 인스턴스, 두 번째 터미널의 같은 profile, 또는 무관한 프로세스입니다. **해결.** 확인한 프로세스만 중지하거나 해당 profile의 변수(예: `KR_812_GAME_PORT`)로 포트를 바꿉니다. 머신의 모든 .NET 프로세스를 종료하지 마세요. **정상 확인.** `Row 'game-listener' listening on <address> (protocol <profile>/game).` 로그가 나오고 포트가 수신 상태입니다.

### NAT나 방화벽을 통해 클라이언트가 연결할 수 없음

**증상.** 서버는 수신하지만 다른 머신의 클라이언트가 연결하지 못합니다. **확인.** 수신은 `${BFF_BIND_HOST:-0.0.0.0}`에 바인딩되고, 클라이언트에는 center gate 주소와 relay 광고에서 `${BFF_PUBLIC_HOST:-127.0.0.1}`이 전달됩니다. 공개 주소의 기본값은 루프백이라 같은 머신에서만 동작합니다. `--dump-config`에서 두 값을 확인하고 Windows에서는 `Test-NetConnection <public-host> -Port 7110`, Linux에서는 `nc -vz <public-host> 7110`으로 경로를 시험합니다. **가능한 원인.** `BFF_PUBLIC_HOST`가 기본값으로 남음, 방화벽이 포트를 막음, 또는 포트 포워딩이 광고 포트와 다름입니다. **해결.** `BFF_BIND_HOST`와 `BFF_PUBLIC_HOST`를 클라이언트가 도달할 수 있는 주소로 설정하고, 선택한 profile에 필요한 포트만 열며, 광고한 번호를 그대로 포워딩합니다. **정상 확인.** 클라이언트가 로그인 또는 center 연결에 도달하고 연결 중 `/api/sessions`에 세션이 나타납니다.

### relay 연결 실패

**증상.** modern 빌드에서 로그인은 되지만 대전 연결이 실패하거나 relay 포트가 수신하지 않습니다. **확인.** `kr-1256` profile에는 `${KR_1256_GAME_PORT:-7100}`과 `${KR_1256_RELAY_PORT:-7101}` 수신이 있고, `relay_advertise_ipv4`와 `relay_advertise_port`(기본값 `${KR_1256_RELAY_PUBLIC_PORT:-7101}`)를 광고합니다. 수신과 광고 값을 비교합니다. **가능한 원인.** NAT 뒤에서 광고 포트와 수신 포트가 다름, 패치가 relay 행을 비활성화함, 또는 클라이언트가 legacy 빌드임입니다. **해결.** 광고한 relay 주소와 포트에 도달할 수 있게 하고 두 relay 값을 일치시킵니다. legacy 빌드에는 아직 relay 수신이 없으므로(legacy bundle의 `No relay listener on the legacy builds yet.`) relay 연결을 기대하지 마세요. **정상 확인.** relay 포트가 수신하고 대전 연결이 `matchBound`가 설정된 채 `/api/sessions`에 나타납니다.

### 관리 API 또는 WebUI에 접속할 수 없거나 인증되지 않음

**증상.** 브라우저가 WebUI에 접속하지 못하거나 `/api/...`가 401과 `{"error":"unauthorized"}`를 반환합니다. **확인.** 관리 행은 `${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`에 바인딩하며 기본값은 루프백입니다. `start`로 시작한 서버는 보호된 모든 `/api` 요청에 로그인한 관리자 세션이 필요하며, `/health`와 WebUI 파일은 열려 있습니다. 최초 설정 중에는 관리자 계정이 생기기 전까지 `/api/plugins`가 401을 반환합니다. 이는 인증 모델이며 서버 오류가 아닙니다. `/health`와 `/api/setup/status`는 계속 접근할 수 있습니다. **가능한 원인.** 리스너가 루프백에 있고 브라우저가 다른 곳에 있거나, 세션이 만료되었거나, `run --profile` 서버에 루프백이 아닌 주소를 지정했거나(계정이 없어 거부합니다), 루프백이 아닌 설치 리스너에 부트스트랩 토큰을 설정하지 않았습니다. **해결.** 다시 로그인하십시오. `start`로 시작한 서버에서는 `BFF_ADMIN_HOST`를 필요한 인터페이스로 설정하고, 루프백이 아닌 설치 리스너에는 `bootstrap_token`(`BFF_SETUP_TOKEN`)을 32자 이상으로 설정하십시오. 그렇지 않으면 행이 `A non-loopback installation listener requires an explicit bootstrap_token of at least 32 characters.`와 함께 실패합니다. **복구 확인.** `/health`가 `{"status":"ok", ...}`를 반환하고 로그인한 WebUI에 플러그인 목록이 표시됩니다.

## 영속성과 마이그레이션

### MySQL 연결 또는 인증 실패

**증상.** `error: Activation of row 'persistence.mysql' (plugin 'persistence.mysql') failed: Failed to open MySQL at <host>:<port> database '<database>' as '<user>'.` **확인.** `BFF_MYSQL_HOST`, `BFF_MYSQL_PORT`, `BFF_MYSQL_USER`, `BFF_MYSQL_PASSWORD`, `BFF_MYSQL_DATABASE`를 확인하고 서버에 도달할 수 있는지 봅니다. **가능한 원인.** 호스트, 포트, 사용자, 비밀번호 오류, 도달할 수 없는 서버, 또는 존재하지 않는 데이터베이스입니다. **해결.** 의도적으로 구성한 영속성 배포를 위해 변수를 바로잡습니다. 로컬 확인을 실제 데이터베이스로 향하게 하지 마세요. **정상 확인.** 로그가 `MySQL persistence connected to <host>:<port>/<database> as <user>.`를 보고합니다.

### 마이그레이션 실패

**증상.** 내장 마이그레이션을 읽거나 적용하는 중 활성화가 실패하며 `Embedded SQL resource '<resource>' is not named NNNN_description.sql.`, `Duplicate embedded migration version <version>.`, `No embedded SQL migrations were found.`, `Embedded migration '<resource>' is empty.` 중 하나가 나옵니다. **확인.** 마이그레이션은 플러그인이 `auto_migrate: true`로 활성화될 때 파일 이름 순서로 적용되는 내장 리소스입니다. **가능한 원인.** 손상된 플러그인 빌드, 또는 이름이 바뀌거나 비워진 마이그레이션 리소스입니다. **해결.** 실행 중인 `schema_migrations` 테이블을 직접 수정하지 말고, 깨끗한 체크아웃에서 플러그인을 다시 빌드합니다. 다운그레이드 마이그레이션 경로가 없으므로 이전 빌드로 돌아가야 하면 스키마 변경 전에 만든 백업을 복원합니다. **정상 확인.** 활성화 시 대기 중인 마이그레이션마다 `Applied migration <version> (<name>).` 로그가 한 줄씩 나온 뒤 연결 성공 로그가 나옵니다.

### 영속성 제공자가 두 개 활성화됨

**증상.** 영속성 행이 `Service '<contract>' is already registered in realm <id>.`로 실패합니다. **확인.** `persistence.memory`, `persistence.sqlite`, `persistence.mysql` 중 하나만 활성화할 수 있습니다. `--dump-config`의 `disabled` 플래그를 확인합니다. 격리 패치는 memory를 활성화하고 MySQL을 비활성화하지만 SQLite는 비활성화된 채로 둡니다. **가능한 원인.** 패치가 첫 번째 제공자를 비활성화하지 않고 두 번째를 활성화했습니다. **해결.** 같은 패치에서 사용하지 않는 제공자를 비활성화합니다. **정상 확인.** 영속성 제공자가 정확히 하나만 `Active`이고 나머지는 `Unloaded`입니다.

### SQLite 경로 또는 잠금 문제

**증상.** SQLite 행 활성화가 실패하거나 쓰기가 `SQLite transaction committed but WAL truncation failed; further operations are refused.`로 실패합니다. **확인.** `database_path`는 파일 이름을 포함한 절대 경로여야 하며, 상대 경로는 `database_path must be an absolute file path.`로 실패합니다. 저장 파일을 릴리스 및 로그 디렉터리 밖에 두고 저장 파일마다 서버 하나만 실행합니다. 잠금 대기는 `busy_timeout_ms`이며 기본값은 5000입니다. **가능한 원인.** 상대 경로, 읽기 전용 디렉터리, 또는 같은 저장 파일을 여는 두 프로세스입니다. **해결.** 경로와 권한을 고치고 두 번째 프로세스를 중지합니다. **정상 확인.** 로그가 `SQLite save active; capacity <players> players, <accounts> accounts, <bytes> main-file bytes.`를 보고합니다.

### 재시작 후 데이터가 비어 있음

**증상.** 서버를 중지했다가 시작하면 계정, 아이템 또는 진행도가 사라집니다. **확인.** `--dump-config`가 어떤 영속성 행이 활성화되었는지 보여 줍니다. 격리 패치를 쓰거나 `persistence.memory`를 활성화한 설정에서는 상태가 프로세스 안에만 있으며, 활성화 로그가 이를 알려 줍니다: `In-memory persistence active for up to <players> players and <movement actors> movement actor(s); state is process-local and resets on restart.` **가능한 원인.** 메모리 영속성을 영구 저장으로 기대했습니다. **해결.** 영구 상태가 필요하면 `persistence.sqlite` 또는 `persistence.mysql`을 활성화하고 다른 제공자를 비활성화합니다. 로컬 실험에서는 격리 패치를 유지하고 초기화를 예상합니다. **정상 확인.** 영속성 제공자로 재시작한 뒤 같은 계정으로 로그인할 수 있습니다.

### 백업 복원

**증상.** 데이터베이스 변경을 되돌려야 합니다. **확인.** 서버에는 데이터베이스를 복원하는 명령이 없습니다. 복원은 데이터베이스 수준 작업이며, 작업 전에 서버를 중지해야 합니다. **가능한 원인.** 잘못된 마이그레이션, 실수로 인한 쓰기, 실패한 업그레이드입니다. **해결.** 변경 전에 만든 백업을 복원한 뒤 복원된 스키마와 맞는 설정으로 서버를 시작합니다. 롤백을 가장하려고 `schema_migrations`를 편집하거나 행을 삭제하지 마세요. **정상 확인.** `/health`가 정상이고, 영속성 행이 `Active`이며, 예상한 계정과 데이터가 존재합니다.

## 클라이언트 데이터

### clientdata 행 활성화 실패

**증상.** `error: Activation of row 'clientdata' (plugin 'clientdata') failed: clientdata: neither data_directory '<base>/client-data/<profile>/data' nor raw_directory '<base>/client-data/<profile>/dataraw' exists. Provision the configured client-data directories before enabling this row.` **확인.** 게임 profile은 작업 디렉터리를 기준으로 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data`와 `.../dataraw`를 해석합니다. 오류의 두 경로를 디스크의 파일 및 `--dump-config`의 `BFF_CLIENT_DATA`와 비교합니다. **가능한 원인.** 해당 빌드의 클라이언트 데이터를 제공하지 않음, `BFF_CLIENT_DATA`가 다른 곳을 가리킴, 또는 서버를 다른 디렉터리에서 시작함입니다. **해결.** 일치하는 로컬 클라이언트 데이터를 제공하고 `BFF_CLIENT_DATA`를 그 루트로 설정하거나, 관리와 저장만 필요하면 `management` profile을 사용합니다. 저장소는 클라이언트나 그 독점 데이터를 포함하지 않습니다. **정상 확인.** `clientdata` 행이 `Active`가 되고 게임 profile이 `Server started with <N> active row(s).`로 시작합니다.

### 클라이언트 파일 누락 또는 버전 불일치

**증상.** 기능이 `clientdata: no client file '<path>' in the archives or the loose files.`로 실패하거나 `/api/logs`에 `Session <id> reported client version <reported>, expected <expected>.`가 기록됩니다. **확인.** 파일이 없으면 `data/`의 상대 경로나 `dataraw/`의 느슨한 파일이 클라이언트 표기와 같은지 확인합니다. 느슨한 파일이 아카이브보다 우선합니다. 버전이 다르면 클라이언트의 `ClientVersion_Major`를 profile의 `client_build` 및 profile id와 비교합니다. **가능한 원인.** 불완전한 클라이언트 데이터, 복사되지 않은 아카이브, 또는 선택한 profile과 맞지 않는 클라이언트 빌드입니다. **해결.** 해당 빌드의 전체 데이터를 복사하고 클라이언트 빌드와 맞는 profile을 시작합니다. 코드가 지원하지 않는 빌드를 받아들이도록 profile을 편집하지 마세요. **정상 확인.** 기능이 파일을 읽고 세션이 불일치 경고 없이 연결됩니다.

### 설치 도우미의 클라이언트 데이터 가져오기 실패

**증상.** 설치 도우미가 가져오기 실패를 보고하거나 API가 400, 413, 409와 함께 `{"error":"client_data_invalid"}`, `{"error":"client_data_limit"}`, `{"error":"client_data_busy"}`를 반환합니다. **확인.** 가져오기는 준비된 폴더 또는 클래식 ZIP을 받습니다. 기본 한도는 한 사본당 업로드 8 GiB, 압축 해제 16 GiB, 파일 또는 ZIP 항목 32,768개, 보관 사본 64개, 보관 저장 64 GiB, 시간 20분입니다. ZIP64와 분할 아카이브는 지원하지 않으며, `client_data_busy`는 이미 실행 중인 가져오기가 있다는 뜻입니다. **가능한 원인.** 파일이 너무 많거나 큰 경우, 가져오기가 인식하지 못하는 레이아웃, 동시에 실행된 두 번째 가져오기입니다. **해결.** 한도 안에서 `<build>/data`와 `<build>/dataraw` 레이아웃을 가져오고, 배포에 필요하면 `installation` 행에서 허용 범위 안에서 한도를 조정하며, 실행 중인 가져오기가 끝날 때까지 기다렸다가 다시 시도합니다. **정상 확인.** 가져오기가 루트 경로를 반환하고 일치하는 profile의 `clientdata` 행이 활성화됩니다.

## SDK 플러그인 문제

### SDK 플러그인 프로세스가 시작되지 않음

**증상.** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process '<command>' could not start in '<directory>': <reason>` 또는 `SDK plugin process '<command>' did not start.` **확인.** loader 행의 `plugins[].command`, `args`, `cwd`, `env`를 확인합니다. 디렉터리 부분이 있는 명령은 `base_dir` 기준으로 해석되고 나머지는 `PATH`에서 찾으며, `cwd` 디렉터리가 없으면 `plugins[<n>].cwd: directory '<path>' does not exist`로 설정에 실패합니다. **가능한 원인.** 인터프리터나 실행 파일이 설치되지 않음, 작업 디렉터리 오류, 또는 `PATH`에 명령이 없음입니다. **해결.** 런타임을 설치하고 `command`/`cwd`를 고치거나 절대 경로를 전달합니다. **정상 확인.** 행이 활성화되고 플러그인 id가 `state` `Active`로 `/api/plugins`에 나타납니다.

### 플러그인이 stdout에 씀

**증상.** `error: Activation of row '<row id>' (plugin 'loader.process') failed: SDK plugin process broke the protocol: stdout carried a line that is not JSON (<detail>); stdout is reserved for the protocol, use stderr for diagnostics.` **확인.** stdout에 쓰는 출력, 배너, 진행 표시를 찾습니다. **가능한 원인.** 디버그 출력, 라이브러리 배너, 또는 stdout의 진행 표시줄입니다. **해결.** 모든 진단을 stderr로 보내고 stdout은 JSON-RPC 줄에만 사용합니다. **정상 확인.** 플러그인이 핸드셰이크를 마치고 활성화됩니다.

### 프로토콜 또는 기능 불일치

**증상.** `SDK plugin source '<source>' answered initialize without a supported protocol_version (1 or 2).`, `protocol v1 allows only IMessageRouter in requires and optional.`, `requires_many and optional_many require protocol v2.` 같은 선언 또는 협상 실패가 납니다. **확인.** 플러그인 명세를 호스트가 지원하는 프로토콜 버전과 각 버전이 허용하는 선언에 비교합니다. 형식화된 메시지, 이벤트, 제공자, 데코레이터에는 프로토콜 v2가 필요합니다. **가능한 원인.** 오래된 SDK나 명세가 프로토콜 v1에 고정된 채 v2 선언을 사용합니다. **해결.** SDK나 명세를 업데이트하거나 선언을 v1이 지원하는 범위로 제한합니다. **정상 확인.** 출처가 정의를 게시하고 종속 행이 활성화됩니다.

### 호출이 시간 초과되거나 거부됨

**증상.** `SDK plugin <label> did not answer '<method>' within <n> ms (request_timeout_ms).` 또는 `SDK plugin <label> already has <n> requests in flight (max_in_flight <n>).` **확인.** loader 행의 `request_timeout_ms`와 `max_in_flight`를 확인하고, 플러그인이 그 호출에서 무엇을 하는지 관찰합니다. 이벤트 루프가 막히면 여기서 드러납니다. **가능한 원인.** 느리거나 막힌 핸들러, 또는 진행 중 한도를 넘는 폭주입니다. **해결.** 핸들러가 빨리 반환하게 하고, 측정 근거가 있을 때만 한도를 높이며, 멱등 작업만 재시도합니다. 시간 초과는 플러그인이 이미 커밋한 변경을 되돌리지 않습니다. **정상 확인.** 호출이 설정된 시간 안에 끝나고 진행 중 개수가 한도 아래로 유지됩니다.

### WebAssembly 모듈 로드 실패

**증상.** `error: Activation of row '<row id>' (plugin 'loader.wasm') failed: WebAssembly plugin '<module>' exports no '_start'; build it as a WASI command (wasm32-wasip1 or GOOS=wasip1).` 또는 `WebAssembly plugin '<module>' could not be loaded: <reason>`. **확인.** `loader.wasm` 행의 `plugins[].module`과 모듈의 빌드 대상을 확인합니다. **가능한 원인.** 잘못된 대상으로 빌드한 모듈, 또는 존재하지 않는 경로입니다. **해결.** Rust SDK(`wasm32-wasip1`) 또는 Go SDK(`GOOS=wasip1`)로 WASI 명령 모듈을 빌드한 뒤 모듈 경로를 바로잡습니다. **정상 확인.** 모듈이 승인되고 정의가 활성화됩니다.

## 문제 보고

다른 사람이 실패를 재현할 수 있을 만큼 포함합니다. 문제마다 보고서 하나를 사용하며, 서식은 다음과 같습니다:

```text
Source revision: <git rev-parse --short HEAD 또는 릴리스/패키지 버전>
Operating system: <Windows 11 x64 / Debian 13 x64 / 컨테이너 이미지>
Profile: <profile id 또는 bff profiles list의 해당 줄>
Command: <정확한 명령. 로컬 확인에는 --patch tools/local-checks/isolated-memory.patch.yaml 포함>
Expected: <원래 일어나야 했던 일>
Actual: <실제로 일어난 일과 종료 코드>
First error: <첫 error: 줄>
Affected rows: <rowId, state, error (/api/plugins)>
Evidence: <콘솔 발췌, TRX 결과, 덤프 경로>
Source of the observation: <실제 게임 클라이언트, 자동 테스트, 또는 둘 다>
```

무엇이든 첨부하기 전에 토큰, 비밀번호, 계정 이름, 플레이어 신원, 공개 주소, 개인 데이터가 포함된 packet 본문을 제거합니다. 클라이언트 데이터, 독점 파일, 비공개 조사 자료를 첨부하지 마세요. `--dump-config` 출력에는 보간된 자격 증명이 들어 있을 수 있으므로 가립니다.

문제가 실제 게임 클라이언트에서 관찰되었는지, 자동 검사에서만 나타났는지 분명히 밝히세요. 자동 검사의 통과나 실패는 클라이언트 호환성을 보증하지 않습니다.

## 다음

- [설정](configuration.md): profile, 패치, 환경 변수, 포트와 영속성.
- [배포](deployment.md): 다른 사람을 위한 서버 실행, 업그레이드와 롤백.
- [플러그인](plugins.md)과 [SDK 프로토콜](sdk-protocol.md): 활성화와 수명 주기 세부 사항.
- [런처](launcher.md): 런처 계정, 세션 티켓, 런처 API 버전 게이트와 동반 런처 자체의 오류.

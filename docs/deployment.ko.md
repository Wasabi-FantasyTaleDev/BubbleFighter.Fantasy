---
title: "배포"
description: "릴리스 빌드, Docker Compose, 백업과 롤백으로 서버를 재현 가능하게 실행합니다."
updated: 2026-10-18
---

이 페이지는 다른 사람을 위해 Bubble Fighter Fantasy를 실행하려는 경우를 다룹니다. 릴리스 빌드, 이 저장소의 Docker Compose 구성, Linux와 Windows 수동 설치, 영속 데이터와 백업, 로그, 네트워크 보안, 안전한 업그레이드와 롤백 방법을 설명합니다.

**대상 독자:** 노트북에서 한 번 시작하는 것이 아니라 서버 프로세스를 배포하고 유지하는 운영자입니다.

**시작하기 전에:**

- [설정](configuration.md)을 읽으십시오. 배포는 프로필 선택과 환경 변수로 이루어지며, 이 페이지는 구성 방식 자체를 반복하지 않습니다.
- 배포하려는 정확한 리비전을 빌드하고 검증하십시오. 릴리스 디렉터리는 경고 0개로 끝난 `dotnet build BubbleFighter.Fantasy.sln -c Release`에서 만들어집니다.
- 호스트를 계획하십시오. 지원되는 운영 체제와 아키텍처, 프레임워크 종속 빌드에 필요한 ASP.NET Core Runtime 10(또는 Docker), 전용 서비스 계정, 직접 소유한 데이터베이스가 필요합니다.
- 서비스할 모든 빌드의 클라이언트 데이터를 준비하십시오. 게임 프로필은 데이터가 없으면 활성화 단계에서 실패합니다.
- 백업 위치를 정하십시오. 백업은 릴리스 디렉터리 안에 두면 안 됩니다.

## 배포 전에

이 프로젝트는 프리릴리스입니다. 지원되는 모든 클라이언트에 대해 검증되지 않았으며, 나열된 프로필이나 통과한 자동 점검은 실제 클라이언트 호환성을 입증하지 않습니다. 프리릴리스 서비스로 운영하고, 실제 클라이언트로 테스트하며, 복원 경로를 마련해 두십시오.

이 릴리스의 지원 대상은 Windows x64, Windows ARM64, Linux x64, Linux ARM64입니다. 알려진 배포 환경은 Debian 13이며 다른 Linux 배포판은 테스트되었다고 주장하지 않습니다. 게시 스크립트는 Windows x64와 Linux x64/ARM64를 다루며, Windows ARM64는 나열된 대상으로 동일한 Host 게시 명령에 `-r win-arm64`를 지정해 게시합니다.

공개 Windows 패키지, Linux 설치 스크립트, 원클릭 Docker 설치은 아직 게시되지 않았습니다. 이 저장소에 있는 것은 소스, `tools/`의 게시 스크립트 두 개, `deploy/docker/`의 Docker 파일, Compose 구성입니다. 다른 곳의 설치 프로그램이나 이미지 레지스트리로 대체하지 마십시오.

배포에서는 네 가지 네트워크와 저장소 사항을 계획해야 합니다.

| 사항 | 계획할 내용 | 이 릴리스의 기본값 |
| --- | --- | --- |
| 게임 포트 | 서비스하는 빌드마다 TCP 리스너 하나입니다. | `kr-1256`은 7100, 레거시 `kr-812`는 7110을 사용합니다. |
| 릴레이 | 프로필이 릴레이 리스너를 여는 빌드만 해당합니다. | `kr-1256`은 7101을 사용하며, 레거시 빌드는 열지 않습니다. |
| 관리 / WebUI | 루프백이나 통제된 네트워크에 두십시오. 원격 관리는 `start`로 시작한 서버가 필요하며 관리자 계정으로 로그인합니다. | `127.0.0.1:8080`. |
| MySQL | 서버만 접근할 수 있게 하고 절대 공개하지 않습니다. | `127.0.0.1:3306`, 데이터베이스 `bf_game_db`. |

클라이언트 데이터는 로컬 저장소에 준비하고 가능하면 읽기 전용으로 마운트하거나 복사하십시오. 서버는 그것을 읽기만 합니다.

## 릴리스 디렉터리 빌드

`tools/`의 두 스크립트는 Host와 모든 플러그인을 대상 머신으로 복사할 수 있는 디렉터리에 게시합니다. 둘 다 **프레임워크 종속** 빌드를 만들므로 .NET 런타임을 포함하지 않으며, 대상에는 일치하는 ASP.NET Core Runtime 10이 필요합니다.

| 스크립트 | 대상 | 설명 |
| --- | --- | --- |
| `tools/publish-windows-x64.ps1` | `win-x64` | apphost가 Windows x64이고 포함된 런타임이 없는지 확인합니다. |
| `tools/publish-linux.ps1` | `linux-x64`(기본) 또는 `linux-arm64` | apphost가 요청한 아키텍처의 ELF인지 확인합니다. |

두 스크립트 모두 PowerShell 세션이 필요하며 필수 `-OutputPath`를 받습니다. 디렉터리는 없어도 되지만, 있으면 **비어 있어야** 합니다. 이전 릴리스가 들어 있는 디렉터리에 게시하면 그 릴리스의 런타임 파일이 남습니다. 스크립트는 덮어쓰지 않고 실패합니다.

### Windows

저장소 루트의 PowerShell 프롬프트에서:

```powershell
.\tools\publish-windows-x64.ps1 -OutputPath C:\BFF\release-2026-10-18
```

성공하면 `Windows x64 framework-dependent publish ready: <path>`를 출력하고 대상에 x64 ASP.NET Core Runtime 10.0이 필요하다는 점을 알려 줍니다.

### Linux

어느 머신이든 PowerShell 7 세션에서 게시할 수 있으며 출력은 Linux 바이너리입니다.

```powershell
pwsh -File tools/publish-linux.ps1 -OutputPath /srv/bff-release/server
```

ARM64 대상에는 `-Runtime linux-arm64`를 추가하십시오. 성공하면 `Linux framework-dependent publish ready: <path> (<runtime>)`를 출력하고 InGame 플러그인이 게시되었는지 확인합니다. PowerShell은 스크립트 호스트일 뿐이며 대상 머신에는 PowerShell이 필요하지 않습니다.

### 게시 내용

- `BubbleFighter.Fantasy.Host`(apphost)와 `BubbleFighter.Fantasy.Host.dll`, Host의 종속성 및 `runtimeconfig.json`.
- `plugins/<ProjectName>/`, 플러그인 프로젝트마다 하나의 디렉터리이며 Host 옆의 기본 플러그인 루트입니다.
- `profiles/`, 프로필마다 하나의 디렉터리이며 `local.patch.yaml`은 포함되지 않습니다.
- `packets/`, 패킷 표와 `builds.yaml`.
- `bootstrap/`, 로더 프로필과 `data/level-experience.csv`, `data/locales/*.json`.

스크립트 대신 `dotnet publish src/BubbleFighter.Fantasy.Host -c Release -o <directory>`를 사용할 수도 있습니다. 이는 플러그인 게시 대상과 콘텐츠 복사를 포함한 동일한 Host 게시를 수행합니다. 다만 스크립트의 빈 디렉터리 및 apphost 검사를 생략하므로 새 디렉터리를 사용하고 결과를 직접 검증하십시오. Windows ARM64에는 이 형식에 `-r win-arm64`를 붙이십시오.

복사 전에 결과를 확인하십시오. 디렉터리에는 Host 실행 파일, 플러그인마다 디렉터리가 있는 `plugins/`, 그리고 `profiles/`, `packets/`, `bootstrap/`, `data/` 디렉터리가 있어야 합니다. `coreclr.dll`, `libcoreclr.so` 같은 런타임 파일이 있으면 안 됩니다. 발견된다면 게시가 런타임을 포함한 것이므로 의도한 릴리스가 아닙니다.

## 이 저장소의 Docker Compose

`deploy/docker/compose.yaml`은 이 저장소의 소스로 서버와 MySQL 데이터베이스를 시작합니다. 실행 가능한 컨테이너 구성을 얻는 가장 짧은 경로입니다.

1. 환경 파일을 복사하고 비어 있지 않은 데이터베이스 암호와 설정 토큰을 지정하십시오. Compose 파일은 `BFF_MYSQL_PASSWORD`와 `BFF_SETUP_TOKEN`(무작위 32자 이상)이 필요하며 없으면 시작을 거부합니다.

   ```sh
   cp .env.example .env
   ```

2. Compose 파일이 찾는 위치에 클라이언트 데이터를 준비하십시오. 선택할 모든 빌드에 대해 저장소 옆에 `client-data/<build>/data`와 `client-data/<build>/dataraw`를 두거나 `BFF_CLIENT_DATA_HOST`로 다른 위치를 지정하십시오. 컨테이너에서는 `/app/client-data`에 마운트됩니다.

3. 저장소 루트에서 두 서비스를 빌드하고 시작하십시오.

   ```sh
   docker compose -f deploy/docker/compose.yaml up --build
   ```

시작되는 내용:

- `bff` 서비스는 `deploy/docker/Dockerfile`에서 빌드됩니다. .NET SDK와 ASP.NET 10 이미지를 쓰는 다단계 빌드이며 권한 없는 `bff` 사용자(uid 1001)로 실행됩니다. 명령 없이 호스트를 실행하므로 최초 설정이 시작됩니다. `http://<docker-host>:8080`을 열고, 페이지가 요청하면 `BFF_SETUP_TOKEN`을 입력하고, 관리자 계정을 만든 뒤 MySQL을 선택해 호스트에 `mysql`, 데이터베이스 이름·사용자·암호는 `.env` 값을 입력하고, 데이터 폴더 `/app/client-data`로 클라이언트를 선택하십시오. 이후에는 저장된 설치가 실행되고 관리자는 그 계정으로 로그인합니다. 7100(게임), 7101(릴레이), 8080(관리와 WebUI)을 공개하며, 공개된 관리 포트가 컨테이너에 닿도록 Compose가 `BFF_ADMIN_HOST=0.0.0.0`을 설정합니다.
- `mysql` 서비스는 MySQL 8.4를 실행하고 `BFF_MYSQL_DATABASE`, `BFF_MYSQL_USER`, `BFF_MYSQL_PASSWORD`로 데이터베이스와 계정을 만들며, 호스트에 `${BFF_MYSQL_PORT:-3306}`을 게시하고 데이터를 `mysql-data` 볼륨에 보관합니다. 서버는 상태 확인이 통과한 뒤 시작하며 컨테이너 네트워크 안에서 `BFF_MYSQL_HOST=mysql`을 받습니다.
- 클라이언트 데이터는 `/app/client-data`에 읽기 전용으로 마운트되고, 저장된 설치는 `bff-state` 볼륨에, 데이터베이스 상태는 `mysql-data` 볼륨에 있습니다. 서버 자체 로그는 컨테이너 안에 기록되며 이 Compose 파일은 이를 호스트에 보존하지 않습니다.

동작 확인: Docker 호스트에서 `curl http://127.0.0.1:8080/health`가 `status` 필드가 있는 JSON을 반환하고 콘솔에 설정 또는 설치 진행 상황이 표시됩니다. 다음 명령으로 콘솔을 따라가십시오.

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

`docker compose -f deploy/docker/compose.yaml down`으로 두 서비스를 중지하십시오. 데이터베이스 볼륨은 유지됩니다. `down -v`는 볼륨도 삭제하므로 데이터베이스를 버리려는 것이 아니라면 `-v`를 사용하지 마십시오.

이 Compose 파일을 외부에 노출하기 전에 검토하십시오. MySQL을 호스트에 게시하고, Docker 기본 로그 처리(크기 제한 없음)를 그대로 두며, 기본 게임 프로필을 실행합니다. 개발 스타일 구성이며 강화된 운영 템플릿이 아닙니다.

## Linux 릴리스 루트에서 외부 데이터베이스와 함께 Docker 사용

`deploy/docker/compose.external-db.yaml`은 다른 산출물입니다. 이미 데이터베이스가 실행 중인 Linux 호스트의 릴리스 루트를 위한 템플릿입니다. 대상 머신에서 다음 배치를 만드십시오.

```text
<release-root>/
  compose.yaml     deploy/docker/compose.external-db.yaml 복사본
  Dockerfile       deploy/docker/Dockerfile.published 복사본
  .env             BFF_RELEASE를 포함한 배포 변수
  server/          프레임워크 종속 Linux 게시 출력
  logs/            컨테이너가 기록
  client-data/     선택 사항. 또는 BFF_CLIENT_DATA_HOST로 다른 위치 지정
```

1. `tools/publish-linux.ps1`로 릴리스 출력을 `server/`에 게시하십시오. 게시 디렉터리는 비어 있어야 하므로 다른 내용을 복사하기 전에 `server/`에 게시하십시오.
2. `deploy/docker/compose.external-db.yaml`을 `<release-root>/compose.yaml`로, `deploy/docker/Dockerfile.published`를 `<release-root>/Dockerfile`로 복사하십시오.
3. `<release-root>/.env`를 만드십시오. `BFF_RELEASE`(이미지 태그)와 로컬 데이터베이스의 MySQL 값을 설정해야 합니다. 주소와 토큰을 검토하십시오. `BFF_BIND_HOST`, `BFF_MYSQL_HOST`, 관리 주소는 템플릿이 강제로 설정합니다.
4. `<release-root>`에서 시작하십시오.

   ```sh
   docker compose up -d --build
   ```

이 템플릿이 제공하는 것: `server/`에서 빌드한 `bf-fantasy:<release>` 이미지, 호스트 네트워킹(따라서 `.env`에 구성된 게임, 릴레이, 관리 포트가 그대로 사용되고 `127.0.0.1`의 데이터베이스에 접근 가능), 작은 쓰기 가능 `/tmp`가 있는 읽기 전용 컨테이너 파일 시스템, 모든 capability 제거, `no-new-privileges`, 30초 중지 유예 기간, 중지하지 않는 한 재시작, 패킷 로그 파일을 위한 `./logs`를 `/app/logs`에 마운트, 클라이언트 데이터를 `/app/client-data`에 읽기 전용 마운트, 10 MB 파일 5개로 제한된 json-file 로깅.

이 Dockerfile 변형은 이미지를 `run --profile all`로 시작하므로 릴리스는 기동된 모든 빌드를 각자의 포트로 서비스하며 해당 빌드들의 클라이언트 데이터가 필요합니다. 하나의 빌드만 서비스하려면 운영자가 관리하는 Dockerfile 복사본에서 `CMD`를 바꾸십시오.

`docker compose down`은 서비스를 중지하고 `logs/` 디렉터리를 유지합니다. 데이터베이스는 이 템플릿이 관리하지 않으므로 자체 도구로 백업하고 업그레이드하십시오. `deploy/docker/Dockerfile.published`는 이 배치가 빌드에 사용하는 저장소 파일이며, 어떤 이미지도 레지스트리에 게시되지 않으므로 이미지는 항상 대상 머신에서 빌드됩니다.

## Linux 수동 배포

컨테이너를 원하지 않을 때 사용하는 경로입니다. Linux 게시 출력을 대상으로 복사하고 Host를 직접 실행하십시오.

1. `tools/publish-linux.ps1`로 게시하고 출력을 `<release-root>`로 복사하십시오. 게시의 디렉터리 배치는 그대로 유지하십시오.
2. 전용 서비스 계정(예: `bff`)을 만들고 릴리스 디렉터리를 소유하게 하되 대화형 로그인은 허용하지 마십시오. Host는 `profiles/`, `packets/`, `bootstrap/`, `plugins/`, `data/`만 읽으면 되고, `logs/`를 쓸 수 있는 작업 디렉터리와 SQLite를 사용할 경우 저장 파일을 둘 쓰기 가능한 위치가 필요합니다.
3. 복사 과정에서 실행 권한이 사라졌다면 apphost를 실행 가능하게 만드십시오: `chmod +x BubbleFighter.Fantasy.Host`.
4. 프로세스 환경에 배포 변수를 제공하십시오. 릴리스 디렉터리에는 저장소 표식이 없으므로 그 옆에 둔 `.env`는 Host가 **읽지 않습니다**. `.env` 로드 규칙은 체크아웃 안에서만 적용됩니다. 서비스 관리자나 셸이 읽는 환경 파일을 사용하십시오. 예:

   ```sh
   set -a
   . <release-root>/deployment.env
   set +a
   ```

   이 파일에는 `.env.example`과 같은 `KEY=VALUE` 항목이 들어가며 실제 MySQL 암호, 공개 주소, 관리 토큰을 채웁니다.

5. 릴리스 루트로 이동해 Host를 포그라운드로 시작하십시오.

   ```sh
   cd <release-root>
   ./BubbleFighter.Fantasy.Host run --profile kr-1256
   ```

   작업 디렉터리가 중요합니다. `profiles`, `packets`, `client-data`, `logs`가 이를 기준으로 확인됩니다. `Ctrl+C` 또는 SIGTERM은 정상 종료를 요청하며 콘솔은 `Shutdown complete.`로 끝납니다.

6. 저장소는 서비스 유닛을 제공하지 않습니다. 장기 운영 시에는 직접 관리하는 서비스 관리자(예: systemd 서비스) 아래에서 Host를 실행하고, 작업 디렉터리를 `<release-root>`로 설정하며 환경 파일로 변수를 제공하십시오. 서비스 계정은 비특권으로 유지하고 릴리스, 로그 디렉터리, 사용하는 경우 SQLite 파일에만 접근 권한을 주십시오.

호스트에서 `curl http://127.0.0.1:8080/health`로 확인하고 아래의 리스닝 포트 점검을 사용하십시오. 서비스 관리자의 SIGTERM 또는 포그라운드의 `Ctrl+C`로 중지하십시오.

## Windows 수동 배포

1. `tools/publish-windows-x64.ps1`로 게시하고 출력을 배포 디렉터리로 복사하십시오.
2. 시작 전에 프로세스에 배포 변수를 설정하십시오. 예를 들어 프로세스를 소유할 PowerShell 세션에서:

   ```powershell
   $env:BFF_MYSQL_HOST = '127.0.0.1'
   $env:BFF_MYSQL_PASSWORD = '<your-database-password>'
   $env:BFF_PUBLIC_HOST = '<public-address-clients-use>'
   ```

   Linux와 마찬가지로 릴리스 디렉터리에 복사한 `.env`는 읽히지 않습니다. Host는 저장소 루트를 찾을 수 있을 때만 저장소 `.env`를 로드합니다.

3. 상대 경로 `profiles`, `packets`, `client-data`, `logs`가 그곳에서 확인되도록 릴리스 디렉터리에서 Host를 시작하십시오.

   ```powershell
   Set-Location C:\BFF\<release>
   .\BubbleFighter.Fantasy.Host.exe run --profile kr-1256
   ```

4. 방화벽에서 계획한 게임, 릴레이, 관리 포트만 허용하십시오. 관리 포트는 신뢰할 수 있는 네트워크에서만 접근 가능하게 하고 MySQL은 공용 인터페이스에서 멀리 두십시오.
5. 이 저장소에는 Windows 서비스 래퍼가 없습니다. 머신과 함께 시작해야 한다면 직접 관리하는 서비스 관리자 아래에서 실행 파일을 실행하고 서비스 계정은 비특권으로 유지하십시오.

`Invoke-RestMethod http://127.0.0.1:8080/health`로 확인하십시오. `status` 필드가 있는 JSON을 반환합니다. `Ctrl+C`로 중지하며, 플랫폼이 신호를 제공하는 경우 Host는 SIGTERM과 SIGINT도 처리합니다.

## 영속 데이터와 백업

| 데이터 | 위치 | 업그레이드 전 백업 |
| --- | --- | --- |
| 게임 및 계정 데이터 | MySQL 데이터베이스 `bf_game_db`, 또는 SQLite 저장 파일 하나. | 예; 모든 업그레이드마다. |
| 클라이언트 데이터 | `client-data/<build>/` 아래의 읽기 전용 파일. | 한 번, 또는 클라이언트 복사본을 바꿀 때. |
| 구성과 비밀 정보 | Compose `.env` 또는 서비스 관리자의 환경 파일. | 예; 안전하게 보관. |
| 로그와 패킷 로그 | 작업 디렉터리의 `logs/<date>/`, 또는 컨테이너 경로. | 선택; 정책에 따라 회전 및 보존. |
| 이전 릴리스 | 별도 디렉터리. | 롤백 기간이 끝날 때까지 보관. |

데이터베이스, 그 백업, SQLite 저장 파일은 릴리스 디렉터리 밖에 두십시오. 릴리스는 교체되기 때문입니다. MySQL은 서버가 중지되었거나 쓰기가 없을 때 데이터베이스 자체 백업 도구(예: 논리 덤프 `mysqldump` 또는 일관된 스토리지 스냅샷)를 사용하십시오. SQLite는 먼저 서버를 중지한 뒤 단일 저장 파일을 복사하십시오. 서버가 쓰는 중인 SQLite 파일을 복사하지 마십시오.

백업에 의존하기 전에 별도 인스턴스에서 복원을 테스트하십시오. 데이터베이스를 복원하거나 저장 파일을 복사하고, 그것으로 릴리스를 시작해 `/health`, 플러그인 행 상태, 클라이언트 흐름을 확인하십시오. 한 번도 복원해 보지 않은 백업은 검증된 백업이 아닙니다.

클라이언트 데이터는 상태가 아니라 입력입니다. 서버는 읽기만 하고 Docker 구성은 읽기 전용으로 마운트하므로 릴리스를 교체해도 바뀌지 않습니다.

## 로그

서버는 콘솔에 로그를 기록합니다. 각 줄은 한 줄이며 UTC 타임스탬프를 가집니다. 수준은 `BFF_LOG_LEVEL` 또는 `--log-level`에서 오고 기본값은 `Information`입니다. 정상 시작은 활성화된 각 행, 각 리스너의 수신 주소, 준비 후 `Server started with <n> active row(s).`를 보고합니다. 정상 종료는 `Shutdown complete.`로 끝납니다.

진단 행은 패킷 로그 파일도 기록합니다. 이는 콘솔과 별개이며 패킷과 연결 이벤트마다 한 줄입니다.

- 기본 경로는 작업 디렉터리 기준 `logs/{date}/packets_{timestamp}.log`입니다. 프로필이 이름을 덮어씁니다. `kr-812`는 `packets-kr-812_{timestamp}.log`, `all` 프로필은 `packets-all_{timestamp}.log`를 씁니다.
- 각 파일은 10 MiB에서 `.1`로 회전하며 `{date}`가 바뀌면 새 파일을 시작합니다. 기본적으로 설정된 파일 이름 패턴당 최대 100개 파일을 유지하고, 싱크가 시작할 때 더 오래된 파일을 삭제합니다. 파일 이름 시간대는 기본값이 China Standard Time입니다.
- 쓰기는 경계가 정해진 큐(기본 32768개 레코드)를 거치며 종료 시 최대 10초 동안 플러시합니다. 오버플로는 `packet-log-overflow`로 보고되며 게임플레이 패킷을 절대 버리지 않습니다.
- 전체 패킷 기록기는 기본적으로 꺼져 있습니다. `BFF_PACKET_CAPTURE=true`를 설정하면 활성화되며 같은 `logs/` 트리 아래에 별도 JSONL 패킷 파일을 씁니다.

패킷 로그 줄과 캡처에는 플레이어와 연결 데이터가 포함될 수 있습니다. `logs` 디렉터리를 민감 정보로 취급하고 평소의 보존 정책을 적용하십시오. Docker 구성에서 릴리스 루트 템플릿은 `./logs`를 마운트해 패킷 파일을 호스트에 보존하며, 콘솔 출력은 컨테이너의 json-file 로그로 가서 10 MB 파일 5개로 제한됩니다.

## 관리 및 네트워크 보안

- 관리 리스너는 기본적으로 `127.0.0.1:8080`에 바인딩합니다. `start`로 시작한 서버(실행 파일을 인수 없이 실행하면 이렇게 됩니다)는 관리자가 계정과 비밀번호로 로그인하며, 브라우저가 로그인 페이지를 불러올 수 있도록 `/health`와 WebUI 파일은 열려 있습니다. `run --profile`로 시작한 서버는 계정이 없어 로그인이 필요 없고 루프백에서만 수신합니다.
- 관리 포트를 루프백 밖으로 노출하기 전에 원본 네트워크를 제한하십시오. 제한할 수 없다면 루프백에 두고 직접 관리하는 터널이나 리버스 프록시를 사용하십시오.
- 방화벽에서는 선택한 프로필이 서비스하는 게임 및 릴레이 포트만 여십시오. 관리 포트나 MySQL을 공용 인터페이스에 게시하지 마십시오.
- 전용 데이터베이스 계정을 사용하고 MySQL 포트를 루프백이나 사설 네트워크에 두며, 데이터베이스 관리자 계정을 서버에 재사용하지 마십시오.
- 비밀 정보는 배포 로컬 `.env`(Compose) 또는 서비스 관리자 환경 파일(수동)에 보관하십시오. 권한을 제한하고 절대 커밋하지 마십시오. `--dump-config`는 보간된 비밀 정보를 보여 주므로 그 출력을 공개 보고서에 첨부하지 마십시오.
- 클라이언트 데이터, 패킷 로그, 캡처를 공개하지 마십시오. 전용 클라이언트 파일과 플레이어 데이터가 포함될 수 있습니다.

## 업그레이드와 롤백

업그레이드는 나란히 설치하는 방식입니다. 새 릴리스를 새 디렉터리에 설치하고 이전 릴리스를 유지한 뒤, 한 프로세스를 중지하고 다른 프로세스를 시작해 전환합니다.

1. 유지 관리 시간을 공지하고 새 작업 수락을 중단하십시오. 경기를 끝내거나 의도적으로 종료한 뒤 SIGTERM 또는 `Ctrl+C`로 서버를 정상 중지하고 `Shutdown complete.`를 기다리십시오.
2. 위에서 설명한 대로 데이터베이스를 백업하고 실행 중인 정확한 릴리스 디렉터리와 구성을 기록하십시오.
3. 새 릴리스를 새 빈 디렉터리에 게시하십시오. 이전 릴리스 위에 게시하지 마십시오.
4. 배포 변수와 클라이언트 데이터를 복사하거나 재사용하십시오. 클라이언트를 업데이트하지 않는 한 클라이언트 데이터는 릴리스 간에 바뀌지 않습니다.
5. 새 디렉터리에서 새 릴리스를 시작하고 검증하십시오. `/health`, 플러그인 행 상태, 리스닝 포트, 저장소 연결 줄, 콘솔 오류 없음.
6. 롤백이 더 이상 필요하지 않을 때까지 이전 릴리스 디렉터리와 업그레이드 전 백업을 보관하십시오.

롤백하려면 새 릴리스를 중지하고 이전 릴리스 디렉터리를 같은 구성으로 시작하며, 새 릴리스가 스키마를 바꿨다면 업그레이드 전 데이터베이스 백업을 복원하십시오. 바이너리만 롤백하는 것은 보장되지 않습니다. MySQL 마이그레이션은 영속성 행이 활성화될 때 자동으로 적용되며 순방향 전용입니다. 서버에는 다운그레이드 경로가 없으므로 이전 바이너리는 새 릴리스가 이미 마이그레이션한 데이터베이스에서 실행되지 못할 수 있습니다. 안전한 롤백 조합은 이전 릴리스와 업그레이드 전에 받은 백업입니다.

## 배포 후 점검

- [ ] `GET /health`가 `status`가 `ok`인 JSON을 반환합니다.
- [ ] WebUI 플러그인 목록(또는 로그인한 세션의 `GET /api/plugins`)에 예상한 모든 행이 실패 상태 없이 표시됩니다.
- [ ] 호스트의 리스닝 포트가 계획과 일치합니다. Linux는 `ss -ltn`, PowerShell은 `Get-NetTCPConnection -State Listen`으로 확인합니다.
- [ ] 콘솔에 저장소 연결 줄이 표시됩니다(MySQL 연결됨 또는 SQLite 활성).
- [ ] 서비스하는 모든 게임 빌드의 `clientdata` 행이 실패가 아니라 활성 상태입니다.
- [ ] 로그 수준이 운영에 적절하고, 패킷 로그가 예상된다면 패킷 로그 디렉터리가 쓰기 가능하며 증가합니다.
- [ ] 백업을 받았고 테스트 인스턴스에서 최소 한 번 복원했습니다.
- [ ] 소유자가 실제 클라이언트로 테스트했습니다. 자동 점검과 나열된 프로필은 클라이언트 수용이 아닙니다.

## 문제가 생기면

- **컨테이너가 즉시 종료됩니다.** 기본 프로필이 게임 프로필이고 클라이언트 데이터가 없습니다. `client-data/<build>/data` 또는 `dataraw`를 추가하거나 클라이언트 데이터가 필요 없는 프로필을 실행하십시오.
- **서버가 MySQL에 연결하지 못합니다.** 주소, 포트, 계정, 데이터베이스 이름을 확인하십시오. 릴리스 디렉터리는 로컬 `.env`를 읽지 않으므로 프로세스 환경의 변수를 확인하십시오.
- **`/api`가 401을 반환합니다.** 요청에 로그인한 관리자 세션이 없습니다. 먼저 WebUI에서 로그인하십시오.
- **포트가 이미 사용 중입니다.** 충돌 프로세스를 중지하거나 빌드별 포트 변수를 바꾼 뒤 다시 시작하십시오.

순서대로 확인하는 목록은 [문제 해결](troubleshooting.md)을 참고하십시오.

## 다음 단계

[설정](configuration.md)에서 위에 사용된 프로필, 포트, 저장소 키를, [플러그인](plugins.md)에서 행의 활성화와 재로드 방식을 확인하십시오.

## 설치 자격 증명

`state/administration/installation.json`에는 서버가 재시작 후 다시 연결할 수 있도록 MySQL 비밀번호가 평문으로 저장됩니다. 상태 디렉터리는 소유자만 접근하도록 제한하십시오. Windows에서는 파일 보안 속성으로 서비스 계정만 허용하고, Linux에서는 서비스 사용자로 `chown`한 뒤 `chmod 700 state/administration` 및 `chmod 600 state/administration/installation.json`을 실행합니다. 원격 MySQL에서 `SslMode=None`을 사용하면 서버가 경고하므로 로컬이 아닌 데이터베이스에는 TLS를 활성화하십시오.

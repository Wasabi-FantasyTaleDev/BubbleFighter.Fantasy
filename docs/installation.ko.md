---
title: "설치"
description: "도구를 준비하고 소스에서 서버를 빌드하거나 Docker로 실행합니다."
updated: 2026-10-20
---

Bubble Fighter Fantasy는 Fantasy Tale Studio가 유지 관리하는 게임 *버블파이터*(크레이지슈팅 버블파이터) 커뮤니티 서버이며 C#과 .NET 10으로 작성되었습니다. 서버 기능은 YAML 프로필의 플러그인 행으로 구성됩니다. 프로필은 클라이언트 빌드에 필요한 플러그인 행을 선택하고 Host가 이를 활성화합니다. 이 페이지는 빈 머신에서 확인된 설치까지 안내합니다. 준비 사항, 현재 제공되는 설치 방법, Windows와 Linux에서의 소스 빌드, Docker Compose 구성 실행, 첫 실행 설치 도우미, 그리고 각 단계를 검증하는 확인 방법을 다룹니다.

**이 페이지의 대상:** 서버를 처음 설치하고 정상 실행까지 확인하려는 운영자입니다. 프로젝트에 대한 사전 지식은 필요하지 않습니다.

## 설치하는 항목

서버 Host와 Host가 시작할 때 읽는 데이터를 설치합니다.

- Host 실행 파일(`BubbleFighter.Fantasy.Host`), 플러그인 어셈블리, 패키지에 포함된 `profiles`, `packets`, `data`, `bootstrap` 디렉터리
- 첫 실행 도우미가 실행 파일 옆에 만드는 설치 상태 디렉터리
- 선택 사항: 실행하려는 클라이언트 빌드에 맞춰 직접 준비하는 클라이언트 데이터

저장소에는 게임 클라이언트와 클라이언트의 독점 데이터가 포함되어 있지 않습니다. 일부 프로필 행은 일치하는 버전의 로컬 파일을 필요로 하며, 파일이 없으면 해당 행은 활성화 단계에서 실패합니다. 이 프로젝트는 현재 시험판(prerelease)입니다. 프로필마다 사용 가능한 기능과 필요한 데이터가 다르며, 프로필이 목록에 있다거나 자동 검사가 통과했다는 사실이 실제 클라이언트 호환성을 검증했다는 뜻은 아닙니다.

모든 릴리스에는 Windows x64, Windows ARM64, Linux x64, Linux arm64용 사전 빌드 패키지가 포함됩니다. Linux에서는 설치 스크립트로 네이티브 또는 Docker 방식으로 설치할 수 있습니다. 아래 "사전 빌드 패키지 설치"를 참고하세요. 소스 빌드와 이 저장소의 Docker 파일도 계속 사용할 수 있습니다.

## 시작하기 전에

| 요구 사항 | 사용 용도 | 확인 방법 |
| --- | --- | --- |
| Windows x64, Windows ARM64, Linux x64 또는 Linux arm64 | 서버 실행 | 운영체제 설정, Linux에서는 `uname -m` |
| .NET 10 SDK | 소스 빌드 | `dotnet --version`이 10.x 표시 |
| Node.js 20.19 이상 | 소스 빌드(빌드 중 웹 인터페이스를 컴파일) | `node --version`이 20.19 이상 표시 |
| ASP.NET Core Runtime 10 | 프레임워크 종속 빌드 실행 | `dotnet --list-runtimes`에 `Microsoft.AspNetCore.App 10.x` 표시 |
| Git | 소스 빌드 | `git --version` |
| Docker Engine과 Compose | Docker 방식 전용 | `docker compose version` |
| 실행할 빌드의 클라이언트 데이터 | 게임 프로필 | `client-data/<build>/data` 존재 |
| 저장소 루트의 터미널 | 컨테이너 내부를 제외한 이 페이지의 모든 명령 | `Get-Location` 또는 `pwd` |

저장소는 최소 Windows 또는 Linux 버전을 규정하지 않습니다. .NET 10 SDK가 지원하는 운영체제를 사용하세요. 선택형 플러그인 SDK(Python, TypeScript, Go, Rust)는 서버 설치나 실행에 필요하지 않습니다.

## 설치 방법 선택

| 방법 | 상태 | 사용할 때 |
| --- | --- | --- |
| Windows x64 빌드 패키지 | 사용 가능 | Windows x64에서 준비된 빌드를 사용할 때 |
| Windows ARM64 빌드 패키지 | 사용 가능 | Windows on ARM에서 준비된 빌드를 사용할 때 |
| Linux 설치 스크립트 | 사용 가능 | Docker 없이 준비된 Linux 배포를 사용할 때 |
| Docker 원클릭 설치 | 사용 가능 | 저장소 없이 서버를 컨테이너로 실행할 때(MySQL은 선택) |
| 이 저장소에서 빌드한 Docker | 사용 가능 | 저장소와 Docker가 있고 서버와 MySQL을 컨테이너로 실행할 때 |
| 소스 빌드 | 사용 가능 | 이 머신에서 빌드 출력과 첫 실행 도우미를 사용할 때 |

## 사전 빌드 패키지 설치

모든 릴리스에는 패키지 네 개와 `SHA256SUMS` 체크섬 목록이 포함됩니다. 패키지는 프레임워크 종속형이므로 실행하는 컴퓨터에 ASP.NET Core Runtime 10이 필요합니다. 런타임이 없으면 Linux 설치 스크립트가 설치합니다.

| 패키지 | 대상 |
| --- | --- |
| `bubblefighter-fantasy-win-x64.zip` | Intel 또는 AMD 64비트 프로세서의 Windows |
| `bubblefighter-fantasy-win-arm64.zip` | Windows on ARM |
| `bubblefighter-fantasy-linux-x64.tar.gz` | x86_64의 Linux |
| `bubblefighter-fantasy-linux-arm64.tar.gz` | aarch64의 Linux |

최신 릴리스의 파일은 항상 `https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/<package>`에 있습니다.

### Windows

1. 프로세서(x64 또는 Arm64)에 맞는 [ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)이나 이를 포함하는 .NET 10 SDK를 설치하세요.
2. 프로세서에 맞는 ZIP을 내려받아 `C:\BubbleFighterFantasy`처럼 짧은 폴더에 압축을 푸세요. 전체 경로가 약 259자에 이르는 플러그인 파일은 Windows에서 불러올 수 없습니다. 그렇게 깊은 폴더에서 실행하면 서버는 가장 긴 파일을 알려 주고 시작하지 않습니다.
3. `BubbleFighter.Fantasy.Host.exe`를 더블클릭하세요. 설치 도우미가 시작되고 기본 브라우저에서 WebUI가 열립니다. 이어서 아래 "첫 실행 설정"을 따르세요. 콘솔 창을 닫으면 서버가 멈춥니다. 시작에 실패하면 창이 열린 채로 오류를 보여 주며, 아무 키나 누르면 닫힙니다.

### Linux 설치 스크립트

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash
```

스크립트는 x86_64 또는 aarch64를 감지하고, 최신 릴리스에서 맞는 패키지와 `SHA256SUMS`를 내려받아 체크섬을 확인합니다. 시스템에 ASP.NET Core Runtime 10이 없으면 Microsoft의 `dotnet-install.sh`로 설치 디렉터리 안에 런타임을 설치하며, 시스템 전체의 .NET은 건드리지 않습니다. 설치 위치는 `~/bubblefighter-fantasy`이며, root로 실행하면 `/opt/bubblefighter-fantasy`입니다.

| 경로 | 내용 |
| --- | --- |
| `releases/<tag>/` | 설치한 릴리스별로 압축을 푼 패키지 |
| `current` | 사용 중인 릴리스를 가리키는 링크 |
| `state/` | 설치 도우미가 저장한 설치 상태, 업그레이드해도 유지 |
| `dotnet/` | 런타임, 설치 스크립트가 설치해야 했을 때만 존재 |
| `bff` | 실행기, 인수 없이 실행하면 설치 도우미 시작 |

`~/bubblefighter-fantasy/bff`로 서버를 시작하고 `http://127.0.0.1:8080/`을 여세요. 도우미는 루프백 주소에서만 대기합니다. 원격 서버라면 먼저 자신의 컴퓨터에서 `ssh -L 8080:127.0.0.1:8080 <user>@<server>`로 포트를 전달하세요.

옵션은 `bash -s --` 뒤에 씁니다. 예: `curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --service`

| 옵션 | 효과 |
| --- | --- |
| `--version <tag>` | 최신 대신 해당 릴리스를 설치 |
| `--dir <path>` | 설치 디렉터리 |
| `--admin-port <n>` | 설치 도우미와 WebUI 포트(기본값 8080) |
| `--service` | root로 실행 시 `bubblefighter-fantasy` systemd 서비스를 만들고 `bff` 시스템 사용자로 시작 |
| `--start` | 설치가 끝나면 서버 시작 |
| `--no-runtime` | 런타임을 설치하지 않고 실패 |
| `--from <dir>` | 내려받지 않고 릴리스 파일이 있는 디렉터리에서 설치 |

업그레이드하려면 같은 명령을 다시 실행하세요. 새 릴리스는 기존 릴리스 옆에 압축이 풀리고, 프로필 목록을 출력해 확인을 통과한 뒤에야 `current`가 됩니다. `state/`는 건드리지 않습니다.

### Docker 원클릭 설치

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker
```

Docker Engine과 Compose 플러그인이 필요합니다. 설치 스크립트는 Linux 패키지를 `~/bubblefighter-fantasy/docker`에 풀고, 이를 바탕으로 로컬 이미지를 빌드한 뒤 Docker Compose로 서버 컨테이너를 시작합니다. 컨테이너는 호스트 네트워크를 사용하므로 선택한 클라이언트의 게임 포트에 접속할 수 있습니다. 기본적으로 데이터베이스 컨테이너는 시작하지 않습니다. 설치 도우미에서 `bff-state` 볼륨에 파일을 저장하는 SQLite를 선택하거나, 기존 MySQL 서버를 입력하세요.

내장 MySQL 8.4도 함께 실행하려면 `--with-mysql`을 추가하세요.

```bash
curl -fsSL https://wasabi-fantasytaledev.github.io/install.sh | bash -s -- --docker --with-mysql
```

이 경우 MySQL은 `127.0.0.1:3306`에서만 대기하며(`--mysql-port`로 변경), 생성된 `.env`에는 소유자만 읽을 수 있는 무작위 데이터베이스 비밀번호가 들어 있습니다. 설치 도우미에서 MySQL을 선택하고 호스트 `127.0.0.1`, 설치 스크립트가 출력한 포트, 데이터베이스 `bf_game_db`, 사용자 `bff`, 그리고 `~/bubblefighter-fantasy/docker/.env`의 `BFF_MYSQL_PASSWORD` 값을 입력하세요.

같은 명령을 다시 실행하면 업그레이드됩니다. `.env`와 볼륨은 유지되며, MySQL과 함께 만든 스택은 업그레이드 후에도 MySQL을 유지합니다. 로그는 해당 디렉터리에서 `docker compose logs -f bff`로 볼 수 있습니다.

## Windows에서 소스 빌드

Windows PowerShell에서 다음 단계를 실행하세요. 저장소는 원하는 폴더에 받을 수 있으며, 이후 명령은 모두 저장소 루트에서 실행합니다.

### 1. 저장소 복제

```powershell
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

`<repository-url>`은 프로젝트가 공개한 저장소 주소입니다. 이미 저장소나 소스 압축 파일이 있다면 해당 디렉터리에서 PowerShell을 여세요.

### 2. .NET SDK 확인

```powershell
dotnet --version
```

10.x 버전이 출력되어야 합니다. 다른 버전이 나오거나 명령을 찾을 수 없다면 먼저 .NET 10 SDK를 설치하세요.

### 3. 모든 프로젝트 빌드 {#build-all-projects-windows}

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

첫 빌드는 패키지를 내려받으므로 몇 분 걸릴 수 있습니다. 이 저장소는 경고를 오류로 처리하므로 성공한 빌드는 다음 출력으로 끝납니다.

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 4. 빌드 출력 위치 찾기

| 항목 | 위치 |
| --- | --- |
| Host 실행 파일과 라이브러리 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 플러그인 빌드 출력 | `artifacts/plugins/<AssemblyName>/` |
| 런타임에 사용하는 사본 | `<host output>/plugins/` |
| Host 옆에 복사되는 데이터 | `<host output>/profiles/`, `packets/`, `data/`, `bootstrap/` |

Host 빌드는 플러그인 디렉터리를 `<host output>/plugins/`로 복사하며 빌드할 때마다 해당 폴더를 교체합니다. 따라서 삭제된 플러그인이 남지 않습니다. 출력에 복사되는 `profiles/`에는 `local.patch.yaml`이 제외됩니다. 이 파일은 Git에서 제외된 개발자 재정의 파일이며, 저장소에 있을 때만 적용됩니다.

### 5. 빌드한 Host 실행

기본 `./profiles` 디렉터리가 해석되도록 저장소 루트에서 실행하세요.

```powershell
.\src\BubbleFighter.Fantasy.Host\bin\Release\net10.0\BubbleFighter.Fantasy.Host.exe profiles list
```

각 패키지 프로필마다 한 줄이 출력되어 실행 파일과 프로필 데이터가 준비되었음을 보여 줍니다. 설치 도우미를 시작하는 패키지 첫 실행 방법은 아래 "첫 실행 설정"에서 설명합니다.

**확인:** 빌드가 `0 Warning(s)`와 `0 Error(s)`로 끝나고 위 명령이 오류 대신 프로필 줄을 출력합니다.

## Linux에서 소스 빌드

POSIX 셸에서 다음 단계를 실행하세요. 저장소는 원하는 폴더에 받을 수 있으며, 이후 명령은 모두 저장소 루트에서 실행합니다.

### 1. 저장소 복제

```sh
git clone <repository-url> BubbleFighter.Fantasy
cd BubbleFighter.Fantasy
```

### 2. .NET SDK 확인

```sh
dotnet --version
```

10.x 버전이 출력되어야 합니다.

### 3. 모든 프로젝트 빌드 {#build-all-projects-linux}

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

성공한 빌드는 `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`로 끝납니다.

### 4. 빌드한 Host 실행

```sh
dotnet src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll profiles list
```

기본 `./profiles` 디렉터리가 해석되도록 저장소 루트에서 실행하세요. DLL 옆의 apphost 실행 파일도 같은 방식으로 동작합니다.

### 5. 런타임 요구 사항 확인

빌드에는 .NET 10 SDK와 Node.js 20.19 이상이 필요합니다. WebUi 프로젝트는 처음 빌드할 때 npm으로 웹 인터페이스를 컴파일하고, 이후에는 소스가 바뀔 때만 다시 컴파일합니다. 프레임워크 종속 출력(예: `tools/publish-linux.ps1`이 만드는 배포 디렉터리나 컨테이너 이미지에서 복사한 파일)은 실행하는 머신에 ASP.NET Core Runtime 10이 필요합니다. `dotnet --list-runtimes`로 `Microsoft.AspNetCore.App 10.x`가 있는지 확인하세요. 아래 Docker 방식은 이미지 안에서 런타임을 제공합니다.

**확인:** 빌드에 경고와 오류가 없고 `profiles list`가 프로필 줄을 출력합니다.

## 이 저장소의 Docker로 실행

Docker Engine과 Compose 플러그인이 설치된 상태에서 저장소 루트에 위치한 뒤 다음 단계를 실행하세요.

### 1. 환경 변수 파일 만들기

PowerShell:

```powershell
Copy-Item .env.example .env
```

Linux 셸:

```sh
cp .env.example .env
```

시작 전에 `.env`의 `BFF_MYSQL_PASSWORD`를 비어 있지 않은 임시 값으로 설정하세요. Compose가 이 값으로 MySQL 컨테이너를 구성하며, `.env`는 Git에서 제외되므로 절대 커밋하지 마세요. MySQL 연결 변수는 `.env.example`에 설명되어 있습니다.

### 2. 서버와 데이터베이스 시작

```sh
docker compose -f deploy/docker/compose.yaml --env-file .env up --build
```

compose 파일은 두 서비스를 시작합니다.

- `bff`는 `deploy/docker/Dockerfile`로 빌드하며 빌드 컨텍스트는 저장소 루트입니다. Dockerfile은 런타임을 포함하지 않은 Host를 게시한 뒤 ASP.NET Core Runtime 10 이미지에서 비 root 시스템 사용자로 실행하며, 진입점은 `dotnet BubbleFighter.Fantasy.Host.dll run --profile kr-1256`입니다. 이미지는 7100, 7101, 8080 포트를 노출합니다.
- `mysql`은 `mysql:8.4` 이미지를 사용하며 데이터베이스 이름은 `bf_game_db`, 사용자는 `bff`, 문자 집합은 utf8mb4입니다. 데이터는 `mysql-data` 볼륨에 저장되고 서버가 시작되기 전에 상태 검사를 통과합니다.

게시되는 포트는 서버의 7100, 7101, 8080과 MySQL의 `${BFF_MYSQL_PORT:-3306}`입니다. 서버는 `${BFF_CLIENT_DATA_HOST:-../../client-data}`(저장소의 `client-data` 디렉터리)를 `/app/client-data`에 읽기 전용으로 마운트합니다. 컨테이너에는 `client-data/kr-1256/data` 또는 `client-data/kr-1256/dataraw`가 필요하며, 없으면 clientdata 행이 실패합니다. 두 서비스 모두 `restart: unless-stopped`를 사용합니다.

컨테이너 방식은 의도적으로 MySQL을 시작합니다. 격리된 로컬 검사가 아니며 로컬 검사 패치를 사용하지 않습니다. 관리 리스너는 컨테이너 안에서 `127.0.0.1:8080`에 바인딩되므로, 게시된 8080 포트로 WebUI에 접근하려면 `.env`에 `BFF_ADMIN_HOST=0.0.0.0`을 추가하세요.

### 3. 로그 확인

```sh
docker compose -f deploy/docker/compose.yaml logs -f bff
```

시작 로그는 아래 "첫 실행 설정"에서 설명하는 줄과 일치해야 합니다. Ctrl+C는 로그 추적만 중지하며 `up`으로 시작한 서비스는 계속 실행됩니다.

### 4. 컨테이너 중지

compose 명령을 실행한 터미널에서 Ctrl+C를 누르거나 다른 터미널에서 실행하세요.

```sh
docker compose -f deploy/docker/compose.yaml down
```

`down`은 `mysql-data` 볼륨을 유지하므로 나중에 `up`을 다시 실행하면 같은 데이터베이스를 사용합니다. `docker compose -f deploy/docker/compose.yaml down -v`는 해당 볼륨과 그 안의 모든 게임 데이터도 삭제하므로 데이터를 버려도 될 때만 사용하세요.

### 수동 단일 컨테이너 방식

compose 조합 대신 컨테이너 하나를 직접 실행하려면 저장소 루트에서 이미지를 빌드하고 접근 가능한 MySQL과 클라이언트 데이터 마운트를 전달하세요.

```sh
docker build -f deploy/docker/Dockerfile -t bff-fantasy .
docker run --rm --env-file .env -e BFF_MYSQL_HOST=<mysql-host> -e BFF_ADMIN_HOST=0.0.0.0 -p 7100:7100 -p 7101:7101 -p 8080:8080 -v "<client-data-directory>:/app/client-data:ro" bff-fantasy
```

이미지는 게시된 Host와 `profiles` 디렉터리만 복사하며 데이터베이스와 클라이언트 데이터는 포함하지 않습니다. `BFF_MYSQL_HOST`는 컨테이너가 접근할 수 있는 주소로 지정하세요.

**확인:** `docker compose -f deploy/docker/compose.yaml ps`가 서비스가 실행 중임을 보여 주고, 서버 로그에 다음 절의 시작 줄이 나타납니다. 첫 빌드는 몇 분 걸릴 수 있습니다.

## 첫 실행 설정

패키지 첫 실행 환경은 내장 `setup` 프로필을 시작합니다. 설치 도우미와 관리 WebUI이며 게임 리스너는 없습니다. 클라이언트 데이터와 데이터베이스가 없는 새 머신에서도 시작할 수 있으며, 적용 전에 데이터베이스를 선택하고 테스트합니다.

### 1. 인수 없이 Host 시작

Windows에서는 빌드 출력 디렉터리에서 터미널을 열고 실행 파일을 실행하세요.

```powershell
.\BubbleFighter.Fantasy.Host.exe
```

패키지의 `profiles`, `packets`, `plugins` 디렉터리를 실행 파일 옆에 두세요. 빌드 출력에는 이미 포함되어 있습니다. 프로세스는 작업 디렉터리를 실행 파일이 있는 디렉터리로 바꾸므로 어디서 실행해도 결과가 같습니다. `start` 명령은 저장소 루트의 `.env`를 읽지 않으며, 도우미는 설정을 상태 디렉터리에 저장합니다.

상태 디렉터리는 기본적으로 실행 파일 옆의 `state/`입니다. `BFF_STATE_DIR` 또는 `--state-dir <directory>`로 변경할 수 있으며, 상대 경로는 프로세스를 시작한 디렉터리를 기준으로 해석됩니다.

### 2. 출력된 주소 확인

콘솔에 관리 리스너와 WebUI 주소가 출력됩니다.

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
```

브라우저에서 `http://127.0.0.1:8080/`을 여세요. 관리 리스너는 기본적으로 `127.0.0.1:8080`이며, 변경하려면 프로세스 환경에서 `BFF_ADMIN_HOST` 또는 `BFF_ADMIN_PORT`를 설정하세요(`start`는 `.env`를 읽지 않습니다).

### 3. 도우미 완료

1. 인터페이스 언어를 선택합니다. 영어, 중국어 간체, 중국어 번체, 한국어 중 하나입니다.
2. 서버 이름을 지정합니다(1~80자).
3. 활성화할 클라이언트 버전을 하나 이상 선택합니다. 선택한 버전마다 클라이언트 데이터 선택이 따로 있으며, 지금 데이터를 넣지 않고 나중에 서버 설정에서 가져올 수도 있습니다.
4. 선택 사항: 지금 클라이언트 데이터를 가져옵니다. 로컬 폴더나 준비된 ZIP 패키지를 선택하고 필터링된 파일 수와 크기를 확인한 뒤 "가져오기 시작"을 누르세요. 브라우저 자체의 폴더 확인은 폴더의 모든 파일을 셀 수 있지만, 도우미는 선택 항목을 클라이언트 아카이브와 `data`/`dataraw` 파일로 먼저 필터링한 뒤 요약을 표시합니다. 가져오기는 상태 디렉터리에 사본을 만들며, 실패하거나 취소된 가져오기는 자신의 임시 파일만 정리합니다.
5. 데이터베이스를 선택합니다. 기본값은 MySQL이며 호스트 `127.0.0.1`, 포트 `3306`, 데이터베이스 `bf_game_db`, 사용자 `bff`입니다. SQLite는 기본적으로 상태 디렉터리에 데이터베이스를 저장합니다. "연결 테스트"를 실행하세요. 테스트가 성공하기 전에는 도우미가 다음으로 진행하지 않습니다.
6. 관리자 계정을 만듭니다. 사용자 이름은 3~64자, 비밀번호는 12자 이상입니다. 이 계정은 서버 관리용이며 설치 정보와 함께 저장되고 게임 계정과는 별개입니다.
7. 설정을 검토하고 적용합니다. Host가 선택한 프로필 행을 시작하며, 활성화에 실패하면 재시도할 수 있는 오류 코드를 표시합니다.

### 4. 적용 성공 후 디스크에 남는 항목

상태 디렉터리에 다음이 남습니다.

- `installation.json`: 저장된 설정과 관리자 자격 증명 해시
- `installation.lock`: 두 설치 프로세스가 같은 상태를 동시에 관리하지 못하게 하는 잠금 파일
- `runtime.generated.patch.yaml`: 파생된 지속성 재정의 파일(자리 표시자만 있으며 데이터베이스 자격 증명은 없음)
- `administration/client-imports/<token>/`: 가져온 클라이언트 데이터 사본마다 하나씩 생성되는 디렉터리
- SQLite를 선택한 경우 `saves/fantasy.db`

### 5. 가져오기 규칙과 제한

폴더 가져오기는 클라이언트 원래 상대 경로의 아카이브 파일(클라이언트의 문자 대소문자 유지)과 경로에 `data` 또는 `dataraw`가 포함된 느슨한 파일을 허용합니다. ZIP 가져오기는 항목이 `<build>/data` 또는 `<build>/dataraw`인 표준 단일 볼륨 ZIP 하나를 허용하며, `client-data/<build>/data`처럼 최대 두 단계의 래퍼 폴더 아래에 있어도 됩니다. 여러 빌드의 폴더가 들어 있는 패키지에서는 선택한 버전만 가져옵니다. ZIP64와 분할 압축 파일은 지원하지 않으므로 로컬에서 압축을 풀고 폴더 선택을 사용하세요.

설치된 게임 폴더도 선택할 수 있습니다. 서버가 Python 없이 필요한 데이터를 생성합니다. 서버 데이터만 포함된 아카이브는 `data/`에 복사하고, 혼합 아카이브에서 필요한 작은 파일은 `dataraw/`에 추출하며 모델과 텍스처는 제외합니다. 필수 파일을 확인하고 누락된 이름을 알린 후 `<state-dir>/client-data/<build>`에 결과를 게시합니다. 기존 생성 데이터는 덮어쓰지 않습니다. 설정의 데이터 원본을 바꾸려면 준비된 데이터를 선택하세요. 준비된 폴더 및 ZIP은 계속 `administration/client-imports/<token>/` 아래의 독립 복사본으로 저장됩니다. 생성 과정에도 업로드, 추출, 저장 용량, 파일 수 및 시간 제한이 적용됩니다.

가져오기는 파일을 설치 상태로 복사할 뿐이며, 필요한 데이터 표와 아카이브 내용은 게임 서비스가 시작될 때 검사됩니다. 가져오기 성공은 게임 플레이나 클라이언트 호환성이 검증되었다는 뜻이 아닙니다.

기본 제한은 업로드 8 GiB, 가져오기당 압축 해제 데이터 16 GiB, 가져오기당 파일 32,768개, 보관 사본 64개, 전체 가져오기 저장 공간 64 GiB, 가져오기당 20분입니다. `installation` 행에서 `client_data_upload_bytes`, `client_data_extracted_bytes`, `client_data_file_limit`, `client_data_copy_limit`, `client_data_storage_bytes`, `client_data_import_seconds`로 조정할 수 있습니다. 다시 가져와도 이전에 성공한 사본은 삭제되지 않습니다.

**확인:** 도우미가 설치 완료를 보고하고 WebUI가 관리 인터페이스로 전환됩니다. 관리자가 아직 없어도 아래 `/health` 검사는 도우미가 실행되는 동안 정상 응답합니다.

## 설치 확인

소스 빌드의 저장소 루트에서 다음 명령을 실행하세요. 서로 다른 계층을 확인하므로 세 명령을 모두 실행하는 것이 좋습니다.

### 1. 패키지 프로필 나열

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

각 패키지 프로필마다 id와 설명 한 줄이 출력됩니다. 이 버전에는 `setup`("Database-independent first-run installation and administration.")과 `management`("Administration and durable storage only; no game or relay ports.")을 포함해 19개가 있습니다. 프로필 파일을 읽고 구성할 수 있음을 보여 주지만 어떤 플러그인이 활성화되는지는 증명하지 않습니다.

### 2. 검색된 플러그인 나열

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

검색된 플러그인 정의마다 플러그인 id, 버전, 지원 클라이언트 빌드, 로드된 위치가 한 줄씩 출력됩니다. Host 옆의 플러그인 디렉터리를 검색할 수 있음을 보여 주지만 특정 프로필의 행이 활성화되는지는 증명하지 않습니다.

### 3. 실행하지 않고 프로필 구성

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

이 명령은 `BFF_MYSQL_PASSWORD`가 설정되어 있어야 합니다. MySQL 행을 구성할 때 `${BFF_MYSQL_PASSWORD}`를 치환하며, 마지막 패치가 그 행을 이후에 비활성화하기 때문입니다. 아직 준비하지 않았다면 [빠른 시작](quickstart.md)에 따라 `.env`를 만드세요.

명령은 적용되는 행 목록을 YAML로 출력하며 각 행은 `- id:`로 시작하는 블록이고, 종료 코드 0으로 끝납니다. 출력에서 다음을 확인하세요.

- `persistence.mysql` 행에 `disabled: true`가 있음
- `persistence.memory` 행에 `disabled: true`가 없으므로 활성화됨
- `clientdata` 행에 `kr-812`의 해석된 `data_directory`와 `raw_directory`가 표시됨

이 덤프는 구성, 치환, 패치 순서를 증명합니다. 명령줄 패치는 마지막에 적용되므로 실제 데이터베이스를 가리키는 개발자 `.env`나 로컬 패치를 덮어씁니다. 리스너를 시작하지 않으므로 활성화나 클라이언트 데이터는 증명하지 않습니다.

::: warning
패치가 MySQL을 활성화하면 구성 덤프에 자격 증명을 포함한 치환된 값이 들어갈 수 있습니다. 덤프를 민감한 정보로 취급하세요.
:::

### 설치 도우미 경로의 브라우저 검사

도우미가 기본 주소에서 실행 중일 때:

| 검사 | 명령 | 표시되어야 하는 내용 |
| --- | --- | --- |
| 상태 확인 | `Invoke-RestMethod http://127.0.0.1:8080/health` | `status` = `ok`와 UTC 타임스탬프 |
| 설치 상태 | `Invoke-RestMethod http://127.0.0.1:8080/api/setup/status` | `phase`, `installed`, `profiles`와 가져오기 제한이 담긴 JSON |

Linux에서는 `curl -s http://127.0.0.1:8080/health`와 `curl -s http://127.0.0.1:8080/api/setup/status`를 사용하세요. `/health`와 WebUI 파일은 항상 열려 있으며, 관리자가 아직 없으면 `/api/plugins`는 설계상 401을 반환하므로 먼저 도우미에서 관리자를 만드세요.

**확인:** 세 소스 빌드 명령이 모두 성공적으로 종료되고 브라우저 검사가 위에 설명한 JSON을 반환합니다.

## 각 항목의 위치

| 항목 | 위치 |
| --- | --- |
| 저장소 | 복제한 디렉터리 |
| Host 빌드 출력 | `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` |
| 플러그인 빌드 출력 | `artifacts/plugins/<AssemblyName>/`(Git 제외) |
| Host 옆의 파일 | `<host output>/profiles/`, `packets/`, `data/`, `bootstrap/`, `plugins/` |
| 설치 상태 | 실행 파일 옆 `state/`; `BFF_STATE_DIR` 또는 `--state-dir`로 변경 |
| 환경 변수 파일 | 저장소 루트의 `.env`; Git 제외; `start`는 읽지 않음 |
| 로그 | 콘솔 출력; 패킷 로그는 작업 디렉터리의 `logs/{date}/` 아래에 기록 |
| 클라이언트 데이터 | `client-data/<build>/data` 또는 `dataraw`; `BFF_CLIENT_DATA`로 변경 가능 |
| Docker 데이터 | `mysql-data` 볼륨; 클라이언트 데이터는 읽기 전용 마운트 |

`start`를 제외한 모든 명령은 저장소 루트의 `.env`를 아직 설정되지 않은 프로세스 환경 변수에 먼저 적용합니다. 이미 존재하는 환경 변수가 우선하며 값은 로그에 기록되지 않습니다.

## 중지와 삭제

- **소스 빌드 또는 도우미 중지:** 터미널에서 Ctrl+C를 누릅니다. 콘솔에 `Shutdown complete.`가 출력되고 프로세스가 종료됩니다.
- **Docker 중지:** 포그라운드 compose 명령에서 Ctrl+C를 누르거나 `docker compose -f deploy/docker/compose.yaml down`을 실행합니다. 데이터베이스 볼륨도 삭제하려는 경우에만 `-v`를 추가하세요.
- **설치 상태 삭제:** 저장된 설치 설정, 관리자 계정, 가져온 클라이언트 데이터 사본, 기본 SQLite 데이터베이스가 제거됩니다. 되돌릴 수 없으므로 먼저 서버를 중지하세요.
- **데이터베이스 삭제:** 지속적으로 저장된 게임 데이터가 제거됩니다. 먼저 백업하고, 플레이어가 사용 중인 라이브 데이터베이스는 절대 삭제하지 마세요. 백업과 업그레이드는 [배포](deployment.md) 페이지에서 다룹니다.
- **빌드 출력 삭제:** 안전하며 빌드 명령을 다시 실행하면 복구됩니다.

## 문제가 발생하면

- **빌드 실패:** 이 저장소는 경고를 오류로 처리합니다. 첫 번째 `error` 줄이 프로젝트와 원인을 알려 줍니다. 수정한 뒤 다시 빌드하세요. [문제 해결](troubleshooting.md)을 참고하세요.
- **프레임워크 종속 빌드가 시작되지 않음:** ASP.NET Core Runtime 10이 없습니다. `dotnet --list-runtimes`로 확인한 뒤 같은 아키텍처의 런타임을 설치하세요.
- **포트가 이미 사용 중:** 도우미는 8080 포트를 사용하고 게임 프로필은 자체 게임 포트(예: `kr-812`의 7110)를 사용합니다. `.env`에서 `BFF_ADMIN_PORT` 또는 해당 빌드의 포트 변수를 설정하거나 포트를 점유한 프로세스를 중지하세요.
- **게임 프로필이 클라이언트 데이터 때문에 실패:** `clientdata` 행이 `data_directory`와 `raw_directory`가 모두 없다고 보고합니다. 일치하는 클라이언트 데이터를 제공하거나 설치 도우미로 가져온 뒤 프로필을 다시 시작하세요. [문제 해결](troubleshooting.md)을 참고하세요.

## 다음 단계

[빠른 시작](quickstart.md)으로 이동해 격리된 로컬 서버를 시작하고 정상 실행을 확인한 뒤, [서버 설정](configuration.md)에서 지속성, 포트, 프로필 변경을 살펴보세요. 장기 실행 서비스와 백업, 업그레이드는 [배포](deployment.md)에서 다룹니다. 클라이언트 데이터는 항상 별도로 제공해야 합니다. 이 저장소에는 게임 클라이언트와 그 데이터가 포함되어 있지 않습니다. 동반 Fantasy Tale Launcher 가이드는 [런처](launcher.md)를 참고하십시오.

---
title: "빠른 시작"
description: "빌드한 저장소에서 격리된 로컬 서버를 시작하고 실행 여부를 확인합니다."
updated: 2026-10-18
---

이 페이지는 [설치](installation.md)에서 빌드한 저장소를 사용해 메모리 지속성을 가진 로컬 격리 서버를 시작합니다. 게임 리스너가 없는 `management` 프로필로 첫 성공 확인을 수행한 뒤, `kr-812` 같은 게임 프로필이 어떻게 다른지, 그리고 이 검사가 무엇을 증명하고 무엇을 증명하지 않는지 설명합니다.

**이 페이지의 대상:** 소스 빌드를 마쳤고 몇 분 안에 로컬 서버를 실행하려는 사용자입니다.

## 얻게 되는 것

안전하게 시작하고 중지할 수 있는 로컬 서버 프로세스이며, 메모리 지속성을 사용하고 실제 데이터베이스에는 연결하지 않습니다. 프로세스가 종료되면 계정과 진행 상황이 초기화됩니다. 완전히 플레이할 수 있는 서버가 아니며, 게임 클라이언트가 동작한다는 증거도 아닙니다.

`management` 프로필은 관리 API와 WebUI만 시작하고 게임 또는 릴레이 리스너가 없으므로 클라이언트 데이터 없이도 실행됩니다. `kr-812` 같은 게임 프로필은 해당 빌드의 로컬 클라이언트 데이터가 추가로 필요하며, 저장소에는 그 데이터가 포함되어 있지 않습니다.

## 시작하기 전에

- [설치](installation.md)를 마쳤습니다. 솔루션이 경고 없이 빌드되었고 빌드 출력이 존재합니다.
- 터미널이 저장소 루트에 있습니다. 이 페이지의 모든 명령은 여기서 실행합니다.
- .NET 10 SDK를 사용할 수 있습니다. `dotnet --version`이 10.x를 출력합니다.
- 로컬 검사 패치가 있습니다. PowerShell에서는 `Test-Path tools/local-checks/isolated-memory.patch.yaml`, POSIX 셸에서는 `test -f tools/local-checks/isolated-memory.patch.yaml`로 확인하세요.
- 다음 단계에서 `.env` 파일을 만듭니다.
- 선택 사항: 이번에 `kr-812`를 시작하려면 일치하는 클라이언트 데이터가 `client-data/kr-812/data` 또는 `client-data/kr-812/dataraw` 아래에 있어야 합니다.

## 로컬 환경 변수 파일 준비

프로필은 구성되는 시점에 환경 변수를 치환하며, 이때는 아직 어떤 패치도 서비스를 비활성화하지 않았습니다. 따라서 메모리 검사에서도 로컬 MySQL 비밀번호 변수가 있어야 합니다.

### 1. 예제 파일 복사

PowerShell:

```powershell
Copy-Item .env.example .env
```

Linux 셸:

```sh
cp .env.example .env
```

`.env`는 Git에서 제외되므로 절대 커밋하지 마세요.

### 2. 임시 비밀번호 설정

`.env`를 열고 `BFF_MYSQL_PASSWORD`를 비어 있지 않은 임시 값(예: `isolated-check-only`)으로 설정하세요. `persistence.mysql` 행은 기본값이 없는 `${BFF_MYSQL_PASSWORD}`를 읽으므로 변수가 없으면 구성이 실패합니다. 격리 메모리 패치는 구성 이후에만 해당 행을 비활성화합니다. 로컬 검사에는 실제 데이터베이스 자격 증명을 사용하지 말고, 실제 자격 증명은 의도적으로 구성한 배포에서만 사용하세요.

**확인:** `.env`가 존재하고(`Test-Path .env`가 `True` 출력) `BFF_MYSQL_PASSWORD` 줄이 비어 있지 않습니다. 이 파일이나 값을 채팅이나 이슈에 붙여 넣지 마세요.

## 프로필 찾기

프로필은 클라이언트 빌드를 지정하고 그 빌드가 사용하는 플러그인 행을 구성합니다. id는 `<region>-<wire version>` 형식이며, 숫자는 실행 파일의 파일 버전이 아니라 클라이언트의 `ClientVersion_Major`입니다. `management`는 관리 전용 프로필이고 `setup`은 첫 실행 도우미입니다.

### 1. 패키지 프로필 나열

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
```

각 프로필마다 id와 설명 한 줄이 출력됩니다. 이 버전에는 19개가 있습니다. 그중 유용한 두 줄은 `management`("Administration and durable storage only; no game or relay ports.")와 `kr-812`("Korean 2015 client build, ClientVersion_Major 812, resource patch 458.")입니다.

프로필이 목록에 있다고 해서 해당 빌드의 모든 게임 기능이 구현되었다는 뜻은 아닙니다. 프로필마다 사용 가능한 기능과 필요한 데이터가 다릅니다.

### 2. 검색된 플러그인 나열

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

각 플러그인 정의마다 플러그인 id, 버전, 지원 클라이언트 빌드, 로드된 위치가 한 줄씩 출력됩니다. 이것이 Host가 프로필에 마운트할 수 있는 구현 집합입니다.

**확인:** 두 명령이 모두 성공적으로 종료되고 내용을 출력하며, 프로필이나 플러그인 스캔 오류가 없습니다.

## 적용되는 구성 확인

로컬 검사 패치로 `kr-812`를 구성하고 아무것도 시작하지 않은 채 결과를 출력합니다.

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

명령은 적용되는 행 목록을 YAML로 출력하고 종료 코드 0으로 끝납니다. 출력에서 다음을 찾으세요.

- `id: persistence.mysql` 뒤에 `disabled: true`가 있음
- `id: persistence.memory`에 `disabled: true`가 없으므로 활성화됨
- `id: clientdata`에 `kr-812`의 해석된 `data_directory`와 `raw_directory`가 표시됨

격리 패치는 마지막에 적용되므로 MySQL을 실제 데이터베이스로 지정한 개발자 `.env`나 `local.patch.yaml`을 덮어씁니다. 덤프는 어떤 플러그인도 활성화되기 전에 끝나므로 구성과 유효성 검사만 증명하며 프로필이 시작된다는 것은 증명하지 않습니다.

::: warning
패치가 MySQL을 활성화하면 덤프에 자격 증명을 포함한 치환된 값이 들어갈 수 있습니다. 민감한 정보로 취급하세요.
:::

## 서버 시작

### 1. 게임 없는 로컬 서버 시작

저장소 루트에서 실행하세요.

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

저장소 루트에 `.env`가 있으면 첫 줄이 `Loaded unset variables from <path>.`입니다. 이어서 서버가 관리 리스너, WebUI 주소, 활성 행 수를 출력합니다.

```text
Row 'admin.http' admin API listening on 127.0.0.1:8080.
WebUI: http://127.0.0.1:8080/
Server started with <count> active row(s).
```

`<count>`는 Host가 출력하는 활성 행 수이며 고정된 숫자가 아닙니다. `management`에는 게임 또는 릴레이 포트가 없습니다. 관리 API 기본 주소는 `http://127.0.0.1:8080`이며, `profiles/bundles/core.yaml`의 `admin.http` 행(`${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}`)에서 옵니다. 프로세스는 중지할 때까지 포그라운드에서 유지됩니다.

### 2. 게임 프로필 시작(클라이언트 데이터 필요)

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml
```

게임 프로필에는 일치하는 클라이언트 데이터가 필요합니다. 없으면 `clientdata` 행이 실패하고 시작이 다음 메시지와 함께 중단됩니다.

```text
clientdata: neither data_directory '<working-directory>/client-data/kr-812/data' nor raw_directory '<working-directory>/client-data/kr-812/dataraw' exists. Provision the configured client-data directories before enabling this row.
```

데이터를 제공하거나 [설치](installation.md)에서 설명한 설치 도우미로 가져온 뒤 프로필을 다시 시작하세요. 데이터가 준비되면 `kr-812`는 `${BFF_BIND_HOST:-0.0.0.0}:${LEGACY_PORT}`에 게임 리스너를 바인딩합니다. `profiles/kr-812/profile.yaml`이 `LEGACY_PORT`를 `${KR_812_GAME_PORT:-7110}`로 설정하므로 실제 값은 `0.0.0.0:7110`입니다. 레거시 빌드에는 릴레이 리스너가 없습니다. 관리 API는 계속 `127.0.0.1:8080`입니다.

**확인:** 프로세스가 포그라운드에 유지되고 위의 시작 줄 세 개를 출력하며 활성화 오류로 종료되지 않습니다.

## 실행 여부 확인

### 1. 상태 확인 엔드포인트

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/health
```

Linux 셸:

```sh
curl -s http://127.0.0.1:8080/health
```

응답에는 `status` = `ok`와 UTC 타임스탬프가 들어 있습니다. `/health`는 항상 로그인 없이 열려 있습니다.

### 2. 런타임 인스턴스 확인

PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Format-Table rowId, state
```

Linux 셸:

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

`/api/plugins`는 실행 중인 플러그인 행을 반환합니다. 각 항목에는 `rowId`, `plugin`, `source`, `state`, `error`가 있습니다. `state` = `Active`이고 `error`가 비어 있는 행을 찾으세요. `Failed` 또는 `Pending` 행은 `error`에 진단 정보를 담고 있습니다.

`run --profile`로 시작한 서버는 `/api`에 로그인이 필요 없고 루프백에서만 수신합니다. 인수 없이 시작한 서버(최초 설정)는 만든 관리자 계정을 요구하며, 세션이 브라우저 쿠키이므로 이러한 작업은 WebUI에서 하십시오.

### 3. WebUI 열기

브라우저에서 `http://127.0.0.1:8080/`을 여세요. management 프로필은 관리 인터페이스를 제공합니다.

**확인:** 상태 확인이 `ok`를 반환하고, 인스턴스 목록에 활성 행이 보이며, WebUI가 열립니다. 이 검사는 Host와 관리 API가 실행 중임을 증명하며, 게임 클라이언트가 연결할 수 있음을 증명하지는 않습니다.

## 서버 중지

서버를 실행 중인 터미널에서 Ctrl+C를 누릅니다. 콘솔에 `Shutdown complete.`가 출력되고 프로세스가 종료됩니다. 메모리의 계정과 진행 상황은 프로세스와 함께 사라집니다. 로컬 검사에서는 지속 데이터를 건드리지 않으므로 중지는 항상 안전합니다.

재시작 후에도 데이터를 유지하려면 [서버 설정](configuration.md)에 따라 지속성을 SQLite 또는 MySQL로 전환하세요. 격리 메모리 패치는 로컬 검사 전용입니다.

## 이것이 증명하지 않는 것

- 게임 클라이언트 로그인도, 게임 플레이나 패킷 호환성 검증도 없습니다.
- management 프로필에는 게임 또는 릴레이 리스너가 없으므로 클라이언트 트래픽이 전혀 없습니다.
- 구성 검사나 자동 검사 통과는 실제 클라이언트 수용 테스트가 아닙니다. 실제 클라이언트로 로그인과 대상 게임 흐름을 별도로 테스트하세요.

## 문제가 발생하면

- **`environment variable 'BFF_MYSQL_PASSWORD' is not set and has no default`:** `.env` 파일이 없거나 저장소 루트에서 찾지 못했거나 변수를 설정하지 않았습니다. `.env.example`을 복사하고 임시 값을 설정하세요.
- **관리 포트가 이미 사용 중:** 리스너 행에서 시작이 실패합니다. `.env`에서 `BFF_ADMIN_PORT`를 설정하거나 8080을 점유한 프로세스를 중지하세요.
- **게임 포트가 이미 사용 중:** `.env`에서 `KR_812_GAME_PORT`를 설정하거나 7110을 점유한 프로세스를 중지하세요.
- **`kr-812`가 `clientdata` 때문에 실패:** 위의 클라이언트 데이터 메시지를 참고하세요. 해당 빌드의 데이터가 있어야 프로필이 시작됩니다.
- **`Server startup did not converge`:** 메시지가 비활성 행마다 상태와 진단을 나열합니다. 첫 번째 `Failed` 또는 `Pending` 행을 읽고 원인을 고치세요.

더 많은 사례는 [문제 해결](troubleshooting.md)에서 다룹니다.

## 다음 단계

[서버 설정](configuration.md)에서 지속성, 포트, 프로필, 환경 변수를 살펴보고, [플러그인](plugins.md)에서 플러그인 행의 동작 방식과 직접 만든 플러그인을 마운트하는 방법을 확인하세요. 클라이언트를 설치하고 서버에 연결하는 동반 런처는 [런처](launcher.md)를 참고하십시오.

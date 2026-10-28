---
title: "서버 설정"
description: "플러그인 프로필을 구성하고 환경 변수, 포트, 영속성을 설정합니다."
updated: 2026-10-18
---

Bubble Fighter Fantasy에는 고정된 서버 구성 요소 집합이 없습니다. `profiles/` 아래의 **프로필**이 실행할 플러그인 행, 제공할 클라이언트 빌드, 열 포트, 사용할 저장소를 결정합니다. 이 페이지는 프로필이 실행 중인 서버가 되는 과정, 구성 파일의 위치, 환경 변수와 포트의 동작 방식, 영속성과 클라이언트 데이터를 전환하는 방법을 설명합니다.

**대상 독자:** 서버가 실행하는 내용을 바꿔야 하는 운영자와, 시작 전에 실제 구성을 확인해야 하는 개발자입니다.

**시작하기 전에:**

- 서버를 한 번 빌드하십시오. `dotnet build BubbleFighter.Fantasy.sln -c Release`가 경고 0개로 끝나야 합니다. [설치](installation.md)를 참고하십시오.
- 저장소 루트에서 터미널을 여십시오. 이 페이지에서 `dotnet run`을 사용하는 명령은 현재 디렉터리가 저장소 루트라고 가정합니다. 프로필, 패킷, 클라이언트 데이터가 작업 디렉터리를 기준으로 확인되기 때문입니다.
- `.env.example`을 `.env`로 복사하고 비어 있지 않은 `BFF_MYSQL_PASSWORD`를 설정하십시오. MySQL을 비활성화하는 점검에서도 프로필 보간에는 이 값이 필요합니다. 실제 자격 증명을 커밋할 파일에 넣지 마십시오.
- 게임 빌드를 시작할 계획이라면 클라이언트 데이터를 준비하십시오. 저장소에는 포함되어 있지 않습니다. 이 페이지의 "클라이언트 데이터" 절을 참고하십시오.

## 설정 작동 방식

서버를 시작하는 방법은 두 가지입니다.

- `bff run --profile <id>`는 YAML 파일에서 프로필을 구성하고 그 행을 시작합니다. 아래의 모든 절은 이 경로를 설명합니다.
- `bff start`(실행 파일을 인수 없이 시작할 때도 사용)는 설치 도우미와 관리 행이 포함된 패키지된 최초 설치 프로필을 실행합니다. 이 경로는 저장소 `.env`를 의도적으로 읽지 않습니다. 이 페이지의 "환경 변수" 절을 참고하십시오.

### 구성 순서

프로필은 계층으로 구성되며, 뒤 계층이 앞 계층의 결과를 바꿉니다.

1. `profiles/<id>/profile.yaml`은 프로필 ID, 제공할 클라이언트 빌드, 사용할 번들, 자체 `vars`, 적용할 패치 파일을 선언합니다.
2. `profiles/bundles/<name>.yaml`의 각 번들이 파일 순서대로 플러그인 행을 제공합니다.
3. `patches:`에 나열된 순서대로 프로필의 패치 파일을 적용합니다. 빌드 프로필은 보통 자체 `patch.yaml`을 적용한 뒤 `profiles/bundles/shop-supplement-lucci.yaml` 같은 공용 보충 패치를 적용합니다.
4. 프로필이 `profiles/<id>/local.patch.yaml`을 나열하면 그 파일을 그다음에 적용합니다. 이 파일은 Git에서 제외되며 없으면 조용히 건너뛰므로, 개발자 재정의를 두기에 적합합니다.
5. 명령줄의 각 `--patch <file>`은 모든 프로필 패치 뒤에 주어진 순서대로 적용됩니다.
6. 문자열 값은 프로필의 `vars`와 프로세스 환경을 기준으로 보간됩니다. `--dump-config`가 최종 결과를 보여 줍니다.

명령줄 패치가 마지막에 적용되므로, 로컬 재정의 파일이 `persistence.mysql`을 특정 데이터베이스로 가리키더라도 명령줄 패치로 한 번의 실행에서 끌 수 있습니다.

### 플러그인 행

실행되는 모든 것은 **플러그인 행**입니다. 행은 플러그인 구현을 지정하고 설정을 전달합니다.

```yaml
- id: admin.http
  plugin: admin.http
  config:
    listen: "${BFF_ADMIN_HOST:-127.0.0.1}:${BFF_ADMIN_PORT:-8080}"
```

| 키 | 필수 | 의미 |
| --- | --- | --- |
| `id` | 예 | 패치와 관리 API가 사용하는 안정적인 행 식별자입니다. 한 프로필 안에서 고유합니다. |
| `plugin` | 예 | 플러그인 디렉터리에서 검색된 플러그인 구현 id입니다. |
| `config` | 아니오 | 플러그인의 옵션 형식에 바인딩되는 매핑입니다. 알 수 없는 키는 바인딩에 실패합니다. |
| `disabled` | 아니오 | `true`이면 행은 구성에 남지만 활성화되지 않습니다. 기본값은 `false`입니다. |

고급 행은 `parent_id`, `isolated_services`, `version`, `lifecycle`도 선언할 수 있습니다. 이 키들은 중첩, 빌드별 서비스 격리, 리소스 예산을 제어하며, 제공되는 프로필은 필요한 곳에 이미 설정해 두었습니다. 배포 설정이 아니라 프로필 정의의 일부로 취급하십시오.

서버는 목록 순서가 아니라 "필요한 서비스가 언제 사용 가능한지"에 따라 행을 활성화합니다. 활성화된 두 행이 같은 서비스 인터페이스를 게시할 수는 없습니다. 레지스트리가 두 번째 등록을 거부하기 때문입니다. 이 때문에 한 번에 하나의 영속성 공급자만 활성화되며, 저장소 전환은 런타임 스위치가 아니라 구성 변경입니다.

### 패치 작업

패치 파일은 단일 키 작업으로 이루어진 YAML 목록입니다.

| 작업 | 형식 | 효과 |
| --- | --- | --- |
| `replace` | `{id, plugin?, disabled?, config}` | 지정한 행을 교체합니다. 주어진 `config`는 행 구성 전체를 교체합니다. |
| `set` | `{id, config: {...}}` | 지정한 구성 키만 바꾸고 나머지는 유지합니다. `null` 값은 키를 제거합니다. |
| `insert` | 행 목록 | 새 행을 추가합니다. |
| `remove` | 행 id | 해당 행을 제거합니다. |

행이 없으면 `replace`와 `set`이 실패하고, 알 수 없는 작업은 구성을 실패시킵니다. 로컬 점검에 사용하는 메모리 격리 패치는 아주 작은 `replace` 패치입니다.

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: false
```

### 보간과 검증

`${VAR}`는 변수 값을 삽입하고 `${VAR:-default}`는 기본값을 제공합니다. 프로필의 `vars`와 프로세스 환경 모두 변수를 정의하지 않았고 기본값도 없으면 구성이 실패하며 파일, 행, 변수를 알려 줍니다. 프로필 자체 `vars`가 같은 이름의 환경 변수보다 우선하며, 비활성화된 행은 보간되지 않습니다.

구성 바인딩은 `snake_case` 키를 플러그인의 형식화된 옵션에 매핑합니다. 알 수 없는 키, 잘못된 형식, 범위를 벗어난 숫자, 옵션 검증 실패는 하나의 `Invalid configuration for row '<id>': ...` 오류로 함께 보고됩니다.

## 파일 위치

| 경로 | 내용 |
| --- | --- |
| `profiles/<id>/profile.yaml` | 프로필 ID, 클라이언트 빌드, `parent`, 번들 목록, `vars`, 패치 목록입니다. |
| `profiles/<id>/patch.yaml` | `profile.yaml`에 나열된 프로필 자체 패치입니다. |
| `profiles/<id>/opcodes.yaml` | 해당 클라이언트 빌드의 이름 기반 opcode 표입니다. |
| `profiles/<id>/local.patch.yaml` | 선택 사항이며 Git에서 제외되는 개발자 재정의입니다. 있으면 자동으로 적용됩니다. |
| `profiles/bundles/*.yaml` | 프로필이 참조하는 공용 행 집합입니다. |
| `packets/*.yaml` | 패킷 레이아웃과 `builds.yaml`의 빌드 상속입니다. 시작 시 읽으며 컴파일되지 않습니다. |
| `.env` | 배포 변수입니다. Git에서 제외됩니다. |

Host 프로젝트는 `bootstrap/`, `profiles/`(`local.patch.yaml` 제외), `packets/`, 두 `data/` 파일(`data/level-experience.csv`, `data/locales/*.json`)을 빌드 및 게시 출력으로 복사합니다. 플러그인 어셈블리는 `plugins/<ProjectName>/` 아래에 게시되며, 이는 Host 옆의 기본 플러그인 루트입니다. 따라서 릴리스는 런타임, 클라이언트 데이터, MySQL 공급자를 사용할 때의 데이터베이스를 제외하면 자체 포함입니다.

행 안의 경로는 프로세스 작업 디렉터리를 기준으로 확인됩니다. `dotnet run`을 사용할 때는 저장소 루트에서, 게시된 Host를 사용할 때는 릴리스 디렉터리에서 실행하십시오. 다른 위치에서 시작하면 `--profiles-dir`를 전달해야 하며, `packets`와 `client-data` 경로는 여전히 작업 디렉터리를 기준으로 확인된다는 점에 유의하십시오.

## 실행 전 확인

현재 체크아웃이 제공하는 프로필과 검색된 플러그인 구현을 나열합니다.

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- profiles list
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list
```

`profiles list`는 각 줄에 프로필 id, 탭, 설명을 출력합니다. 이 릴리스에는 총 19개 프로필이 있습니다(클라이언트 빌드 프로필과 `all`, `management`, `setup`). `plugins list`는 플러그인 id, 버전, 지원하는 클라이언트 빌드, 출처를 출력합니다. `--plugin-root <dir>`(반복 가능)로 검색 위치를 다른 디렉터리로 지정할 수 있습니다.

서버를 시작하지 않고 실제 구성을 출력합니다. 실제 데이터베이스를 가리키는 개발자 `.env`나 `local.patch.yaml`에 접속하지 않도록 항상 메모리 격리 패치를 추가하십시오.

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

덤프는 각 행의 `id`, `plugin`, 행을 제공한 `source` 파일, 설정된 경우 `disabled`, 수명 주기 예산, 보간된 `config`를 나열합니다. 포트나 저장소를 바꾸기 전에 읽어 보십시오. 서버가 실제로 실행할 구성 그대로입니다. 구성이 실패하면 Host는 stderr에 `error: <message>`를 출력하고 종료 코드 1로 끝납니다. 메시지는 실패한 파일, 행, 키를 알려 줍니다.

## 환경 변수

### 로드 규칙

- `start`를 제외한 모든 명령은 먼저 작업 디렉터리에서, 다음으로 Host 디렉터리에서 위로 올라가며 `BubbleFighter.Fantasy.sln` 또는 `.git` 항목이 있는 저장소 루트를 찾습니다. 그곳에 `.env`가 있으면 아직 설정되지 않은 변수에만 값을 적용합니다.
- 이미 존재하는 프로세스 환경 변수가 우선합니다. `.env`는 빈 곳을 채울 뿐 셸이나 서비스 관리자를 덮어쓰지 않습니다.
- `start`는 저장소 `.env`를 절대 읽지 않습니다. 최초 설치 프로필은 프로세스 환경과 `--state-dir`에서 자체 변수를 받습니다.
- 파일은 UTF-8로 읽으며 크기 제한은 64 KiB입니다. 각 줄은 `KEY=VALUE`이고 `export KEY=VALUE`도 허용됩니다. 값은 따옴표로 감쌀 수 있고, 공백 뒤의 인용되지 않은 `#`은 주석을 시작하며, 형식이 잘못된 줄은 파일 경로와 줄 번호를 포함해 실패합니다. 값은 절대 로그에 남지 않습니다. 파일을 읽으면 콘솔에 `Loaded unset variables from <path>.`가 출력됩니다.
- 릴리스 디렉터리에는 솔루션 파일이나 `.git` 항목이 없으므로 게시된 Host 옆에 복사한 `.env`는 **읽지 않습니다**. 배포에서는 프로세스 환경, 서비스 관리자 또는 컨테이너 환경으로 변수를 설정하십시오.

### 자주 설정하는 변수

| 변수 | 용도 | 기본값 |
| --- | --- | --- |
| `BFF_BIND_HOST` | 게임 및 릴레이 리스너가 바인딩할 주소입니다. | `0.0.0.0` |
| `BFF_PUBLIC_HOST` | 클라이언트에 연결 및 릴레이용으로 알려 줄 주소입니다. | `127.0.0.1` |
| `BFF_ADMIN_HOST` / `BFF_ADMIN_PORT` | 관리 HTTP 바인딩 주소입니다. | `127.0.0.1` / `8080` |
| `BFF_LOG_LEVEL` | 콘솔 로그 수준: Trace, Debug, Information, Warning, Error, Critical, None. | `Information` |
| `BFF_CLIENT_DATA` | 빌드별 클라이언트 데이터의 루트 디렉터리입니다. | `client-data` |
| `BFF_PACKET_CAPTURE` | 선택적 전체 패킷 기록기를 활성화합니다. | `false` |
| `BFF_STATE_DIR` | 최초 설치 흐름(`start`)의 상태 디렉터리입니다. | 실행 파일 옆의 `state` |
| `BFF_SQLITE_DATABASE_PATH` | 패치가 SQLite 공급자를 활성화할 때 사용하는 절대 저장 파일 경로입니다. | 없음 |
| `BFF_LEVEL_EXP_TABLE` | 라운드 보상에 사용하는 누적 경험치 표입니다. | `data/level-experience.csv` |

기본 구성에는 다음 MySQL 변수도 필요합니다.

| 변수 | 용도 | 기본값 |
| --- | --- | --- |
| `BFF_MYSQL_HOST` | 데이터베이스 호스트입니다. Docker Compose는 이를 `mysql` 서비스로 덮어씁니다. | `127.0.0.1` |
| `BFF_MYSQL_PORT` | 데이터베이스 포트입니다. | `3306` |
| `BFF_MYSQL_USER` | 데이터베이스 계정입니다. | `bff` |
| `BFF_MYSQL_PASSWORD` | 암호입니다. 보간이 비활성화 패치보다 먼저 일어나므로 반드시 비어 있지 않아야 합니다. | 비어 있음, 설정 필요 |
| `BFF_MYSQL_DATABASE` | 데이터베이스 이름입니다. 기존 데이터베이스라면 어떤 이름이든 사용할 수 있습니다. | `bf_game_db` |

포트와 게임플레이 값은 아래 절에서 설명합니다. `.env.example`이 주석이 달린 전체 목록이므로 변수 이름을 임의로 만들지 말고 복사하십시오.

## 포트와 주소

`BFF_BIND_HOST`는 서버가 수신 대기하는 인터페이스이고, `BFF_PUBLIC_HOST`는 서버가 클라이언트에 알려 주는 주소입니다. NAT 뒤에서는 두 값이 다를 수 있습니다. 클라이언트에 닿는 인터페이스에 바인딩하고, 클라이언트가 실제로 접근할 수 있는 공용 주소를 알리십시오. `BFF_PUBLIC_HOST`를 루프백 기본값으로 두는 것은 클라이언트가 같은 머신에서 실행될 때만 유효합니다.

리스너 포트는 보통 빌드별 변수와 `vars`를 통해 프로필에서 옵니다. 이 릴리스의 두 기준선은 다음과 같습니다.

| 엔드포인트 | kr-1256(모던) | kr-812(레거시) |
| --- | --- | --- |
| 게임 TCP | `7100`(`KR_1256_GAME_PORT`) | `7110`(`KR_812_GAME_PORT`) |
| 릴레이 TCP | `7101`(`KR_1256_RELAY_PORT`) | `7111`(`KR_812_RELAY_PORT`) |
| 관리 HTTP | `8080`(`BFF_ADMIN_PORT`) | `8081`, 프로필 패치가 이동 |
| 선택적 네이티브 선택기 | 사용하지 않음 | `center-selector.yaml`을 마운트하면 `7112` |

`all` 프로필은 기동된 클라이언트 빌드를 하나의 프로세스로 구성하며, 각 빌드는 자체 게임 리스너를 사용합니다. 기본 게임 포트는 `7100`에서 10씩 증가합니다(`kr-1256` 7100, `kr-812` 7110, `id-400` 7120, `cn-408` 7250까지). 각 빌드의 릴레이는 게임 포트보다 1 큰 포트에서 수신합니다(`<BUILD>_RELAY_PORT`, 클라이언트에는 `<BUILD>_PUBLIC_RELAY_PORT`로 안내). 레거시 빌드는 릴레이가 연결되면 릴레이로 라운드를 진행하고, 실패하면 게임 연결로 돌아갑니다. `cn-706`과 `cn-705`는 릴레이 패킷이 감사되지 않아 라운드가 게임 연결에 남습니다. 방화벽 규칙을 열기 전에 `--dump-config`로 선택한 프로필의 정확한 `listen` 문자열을 확인하십시오.

관리 포트는 루프백이나 통제된 네트워크에 두십시오. 선택한 프로필이 제공하는 게임 및 릴레이 포트만 열고, MySQL은 공용 인터페이스에 노출하지 마십시오.

## 영속성

핵심 번들은 세 가지 영속성 행을 제공합니다.

| 행 | 지속성 | 사용 시점 |
| --- | --- | --- |
| `persistence.memory` | 프로세스 수명 동안만 유지되며 재시작 시 초기화됩니다. | 일회성 로컬 점검과 테스트. |
| `persistence.sqlite` | 로컬 저장 파일 하나입니다. | 데이터베이스 서버 없이 영속 데이터가 필요한 단일 호스트. |
| `persistence.mysql` | 외부 MySQL 데이터베이스입니다. | 실제 배포, 특히 여러 프로세스나 호스트를 사용할 때. |

한 번에 하나의 공급자만 활성화할 수 있습니다. 레지스트리가 같은 저장소 인터페이스의 두 번째 등록을 거부하므로, 같은 패치에서 하나만 활성화하고 나머지 둘을 비활성화해야 합니다. 핵심 번들에서는 MySQL 행이 활성화되어 있고 나머지 둘은 비활성화되어 있으며, 패치가 이를 전환합니다.

### 패치로 공급자 전환

기본 MySQL 구성에서 SQLite로 전환합니다.

```yaml
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

MySQL로 돌아가려면 `persistence.mysql`을 `disabled: false`로 바꾸고 활성화한 다른 공급자를 비활성화하십시오.

### MySQL

기본 연결 문자열은 `BFF_MYSQL_*` 변수를 보간하고 `SslMode=none`을 고정합니다. TLS가 필요한 배포는 번들을 수정하지 말고 패치에서 `connection_string` 전체를 덮어쓰십시오.

행이 활성화되면 연결을 열고 `SELECT 1`을 실행합니다. 암호 누락, 잘못된 연결 문자열, 서버 접속 불가는 행을 실패시키며, 시작은 저장소 없이 계속 실행되는 대신 실패를 보고합니다. `auto_migrate`의 기본값은 `true`이며, 이때 플러그인이 내장 마이그레이션을 적용합니다.

- 마이그레이션 스크립트는 `NNNN_description.sql` 이름의 내장 리소스이며 파일 이름 순서로 적용됩니다. 이 릴리스의 최고 버전은 `0032`입니다.
- `schema_migrations`를 먼저 만들고(없는 경우), 이후 각 대기 버전이 자체 트랜잭션에서 실행되고 기록됩니다. 콘솔에는 `Applied migration <version> (<name>).`이 출력됩니다.
- 이름이 잘못되었거나, 버전이 중복되었거나, 내용이 빈 스크립트는 건너뛰지 않고 실행을 실패시킵니다.
- 마이그레이션은 **순방향 전용**입니다. 다운그레이드 경로가 없으므로, 이전 서버 바이너리는 새 릴리스가 마이그레이션한 데이터베이스에서 실행되지 못할 수 있습니다. 백업과 롤백 절차는 [배포](deployment.md)를 참고하십시오.

### SQLite

SQLite 행은 절대 `database_path`를 요구하며, 이 옵션은 상대 경로를 거부합니다. 릴리스를 교체해도 사라지지 않도록 파일을 릴리스 및 로그 디렉터리 밖에 두고, 실행 중인 두 서버가 하나의 저장 파일을 공유하지 않도록 하십시오. 이 행에는 플레이어 수, 계정 수, 데이터베이스 크기, 잠금 대기 한도도 있으며 같은 구성 매핑에서 설정할 수 있습니다.

메모리 공급자는 같은 저장소 인터페이스를 제공하는 경계가 정해진 프로세스 내 저장소입니다. 상태는 종료 시 사라지며, 이 행은 의도적으로 데이터베이스 팩토리를 게시하지 않습니다. 메모리와 MySQL은 상호 배타적입니다.

### 로컬 격리 점검

`tools/local-checks/isolated-memory.patch.yaml`은 `persistence.mysql`을 비활성화하고 `persistence.memory`를 활성화합니다. 명령줄 패치가 마지막에 적용되므로 실제 데이터베이스를 가리키는 개발자 `.env`나 `local.patch.yaml`보다 우선합니다. 모든 로컬 `run`(`--dump-config` 점검 포함)에 추가하십시오. 아래 "격리된 로컬 점검" 레시피에 전체 명령이 있습니다. 프로세스가 멈추면 메모리 데이터는 사라지며, 이 패치는 운영 영속성 구성을 대신할 수 없습니다.

## 클라이언트 데이터

게임 프로필은 선택한 클라이언트 빌드 자체의 데이터를 읽습니다. 저장소에는 게임 클라이언트와 그 파일이 포함되지 않습니다. 데이터 루트의 기본값은 `client-data`이며 `BFF_CLIENT_DATA`로 옮길 수 있습니다. 구조는 다음과 같습니다.

```text
client-data/
  <build>/
    data/      클라이언트 원래 경로를 유지한 클라이언트 패킷(예: Packs/, Config/)
    dataraw/   같은 클라이언트 경로의 압축 해제된 개별 파일, 먼저 읽음
```

`<build>`는 프로필 id입니다(예: `kr-812`, `kr-1256`). 레거시 프로필은 `${BFF_CLIENT_DATA:-client-data}/${BFF_PROFILE_ID}/data`와 `.../dataraw`를 읽고, 모던 `kr-1256` 프로필은 `kr-1256`에 대해 같은 구조를 사용합니다.

두 디렉터리가 모두 없으면 `clientdata` 행이 활성화 단계에서 실패하며, 오류 메시지는 찾은 두 경로를 알려 주고 서버는 시작을 완료하지 못합니다. 게임 프로필을 시작하기 전에 데이터를 준비하십시오. 이 파일은 읽기 전용 입력이므로 Docker 구성은 읽기 전용으로 마운트합니다.

`management`와 `setup`은 클라이언트 데이터 행을 마운트하지 않습니다. 최초 설치 흐름은 준비된 데이터를 상태 디렉터리로 가져올 수 있습니다. [설치](installation.md)를 참고하십시오.

## 일반 구성

첫 번째 레시피는 격리 점검입니다. 나머지는 의도적으로 영속 저장소를 향하는 구성입니다. 즉, 직접 소유한 저장소를 가리키며 구성 중인 공급자를 비활성화하므로 격리 패치를 포함하지 않습니다. 영속 레시피를 변경할 의도가 없는 데이터베이스에 겨냥하지 마십시오.

### 격리된 로컬 점검

```sh
cp .env.example .env
# .env를 편집해 BFF_MYSQL_PASSWORD를 일회용 비어 있지 않은 값으로 설정하십시오.
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

콘솔은 활성화된 행을 보고하고, Host가 준비되면 `Server started with <n> active row(s).`를 출력합니다. `http://127.0.0.1:8080/`의 `GET /health`를 확인하십시오. `status` 필드가 있는 JSON을 반환합니다. `Ctrl+C`로 중지합니다.

### SQLite 서버

저장소 루트에 `sqlite.patch.yaml`을 만듭니다.

```yaml
- replace:
    id: persistence.mysql
    disabled: true
- replace:
    id: persistence.memory
    disabled: true
- replace:
    id: persistence.sqlite
    disabled: false
    config:
      database_path: "${BFF_SQLITE_DATABASE_PATH}"
```

`.env`에서 `BFF_SQLITE_DATABASE_PATH`를 절대 경로로 설정한 뒤 대상 프로필을 시작하십시오(게임 빌드는 클라이언트 데이터가 필요합니다).

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --patch sqlite.patch.yaml
```

콘솔에서 SQLite 활성화 줄을 찾고 저장 파일이 설정한 경로에 생겼는지 확인하십시오. `Ctrl+C`로 중지합니다. 저장 파일은 남습니다.

### MySQL 서버

`.env`에 실제 연결 값을 설정하고 기본 MySQL 행을 활성화된 상태로 두십시오.

```sh
BFF_MYSQL_HOST=127.0.0.1
BFF_MYSQL_PORT=3306
BFF_MYSQL_USER=bff
BFF_MYSQL_PASSWORD=<your-database-password>
BFF_MYSQL_DATABASE=bf_game_db
```

로컬 설정을 확인할 때는 이 데이터베이스에 연결하지 말고 격리된 메모리 저장소로 프로필을 검사하십시오.

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile kr-1256 --patch tools/local-checks/isolated-memory.patch.yaml --dump-config
```

이 덤프는 MySQL을 명시적으로 비활성화하며 리스너를 시작하지 않습니다. 데이터베이스 연결을 검증하거나 마이그레이션을 실행하지 않습니다. 영구 저장소로 운영하려면 데이터베이스 설정을 명시적으로 검토한 후 [배포](deployment.md)를 따르십시오. 실제 MySQL 활성화 시에는 `MySQL persistence connected to <host>:<port>/<database> as <user>.`가 기록되고 대기 중인 마이그레이션이 적용됩니다.

### NAT: 바인드 및 공개 주소

리스너가 모든 인터페이스에 바인딩되고 클라이언트에는 공용 주소가 알려지도록 두 주소를 `.env`에 설정하십시오.

```sh
BFF_BIND_HOST=0.0.0.0
BFF_PUBLIC_HOST=<public-address-clients-use>
```

포트 변수도 같은 방식으로 바꿀 수 있습니다(예: `KR_1256_GAME_PORT`, `KR_1256_RELAY_PORT`). 방화벽에서는 해당 포트만 열고, 시작 전에 `--dump-config`로 주소를 확인하십시오. 특정 리스너의 주소가 프로필 기본값과 달라야 한다면 그 리스너 행에 `set` 패치를 쓰는 방법도 있습니다.

```yaml
- set:
    id: game-listener
    config:
      listen: "0.0.0.0:7100"
```

## 다중 클라이언트 프로필

프로필은 서로 다른 역사적 클라이언트를 제공합니다. 빌드 id는 `<region>-<wire version>`이며, 여기서 wire version은 파일 버전이 아니라 클라이언트의 `ClientVersion_Major`입니다(`kr-812`, `cn-324`, `id-400`, `tw-454`, `kr-1256`). 프로필 디렉터리 이름이 패킷 표 전체에서 사용하는 빌드 id입니다.

`kr-812`(한국 2015)는 **레거시 기준선**입니다. `profiles/bundles/legacy.yaml`이 그 행 집합이며, 레거시 빌드는 자체 `vars`와 `patch.yaml`에 차이(포트, 암호 표, 좌석 수, 좌석 관련 설정, 패킷 스위치)만 선언합니다. `kr-1256`(한국 최종 빌드)은 **모던 기준선**입니다. 자체 `kr-1256`, `ingame`, `shop` 번들을 가지며, 그 패킷 레이아웃이 `cn-774`와 `tw-454`의 기준입니다.

| 프로필 | 클라이언트 빌드 | 역할 |
| --- | --- | --- |
| `kr-812` | `kr:812:458` | 레거시 계열의 기준선입니다. |
| `kr-1256` | `kr:1256:1304` | 모던 기준선이며 Dockerfile의 기본 프로필입니다. |
| `all` | 16개 빌드 | 기동된 모든 빌드를 하나의 프로세스에서 각자의 리스너 포트로 실행합니다. |
| `management` | 없음 | 관리 HTTP와 저장소만 제공하며 게임/릴레이 포트가 없습니다. |
| `setup` | 없음 | 데이터베이스와 무관한 최초 설치 및 관리입니다. |

나머지 클라이언트 빌드 프로필은 각각 하나의 빌드를 제공합니다. 패킷 레이아웃은 `packets/builds.yaml`과 프로필 자체 `parent:` 계보를 통해 상속됩니다. `cn-279`는 `cn-324`를 기반으로 하고 `cn-324`는 `kr-812`를 기반으로 합니다. `cn-738`과 `cn-742`는 `cn-735`의 레이아웃을 따르며, `tw-454`는 `cn-774`를 기반으로 하고 `cn-774`는 `kr-1256`을 기반으로 합니다. 차이가 작은 빌드는 레이아웃을 다시 쓰지 않고 차이만 표기합니다.

빌드는 기능(capability)에서도 다릅니다. 기능은 `packets/builds.yaml`에 빌드별로 기록되고 기준선을 통해 상속됩니다. 예를 들어 만료된 아이템을 버릴 때 범주 6을 제외하는 제한은 `kr-812`에서 켜져 있고 `cn-324`에서 꺼져 있으며, 모던 `kr-1256`은 해당 빌드에서 소비자가 감사될 때까지 여러 레거시 기능을 꺼 둡니다. 레거시 방의 좌석은 기준선에서 12개, `cn-324`에서 8개입니다. 기능 스위치는 빌드의 프로토콜 계층이 무엇을 보내고 활성화할지 결정하며, 이는 추측이 아니라 데이터입니다.

연결하려는 클라이언트 빌드로 프로필을 선택하십시오. 클라이언트 버전을 읽고 일치하는 프로필 id를 찾은 뒤 그 프로필의 포트를 사용하고 해당 빌드의 클라이언트 데이터를 준비하십시오. `all`은 여러 빌드를 함께 실행할 때 사용하며, 각 빌드의 리스너를 시작하고 선택한 모든 빌드의 데이터가 필요합니다.

나열된 프로필이 의미하는 것은 저장소가 이 행들을 구성하고 이 빌드의 패킷 데이터와 기능 기록을 포함한다는 것입니다. 의미하지 않는 것은 모든 게임플레이 기능이 구현되었거나 실제 클라이언트가 검증되었다는 것입니다. 자동화 점검 통과, 기능 활성화, 프로필 목록 등재는 실제 클라이언트 호환성을 입증하지 않습니다. 기능 기록은 감사된 네이티브 지원을 설명하며, 네이티브 수용 테스트는 별도로 수행해야 합니다.

## 선택 기능 번들

일부 번들은 명시적으로 활성화해야 하며, 클라이언트 감사가 뒷받침하는 프로필에서만 나열됩니다.

**`player-settings`**는 `player.settings`를 마운트하며, 이는 플레이어 사용자 지정 키의 형식화된 저장소입니다. 패킷 자체는 `dialect.messages` 행의 `customized_keys` 옵션이 묶으며 `kr-812`, `kr-1256`, `cn-774`, `tw-454`가 선언합니다. 다른 프로필은 먼저 네이티브 컨트롤 이름과 패킷 경로를 감사해야 합니다. 각 `player.settings` 행은 `protocol`이 필요하고 프로필별 `scope`를 사용해 서로 다른 빌드의 이름이 섞이지 않게 합니다. 영속성 공급자는 계정별로 매핑을 저장하며, MySQL은 마이그레이션 `0032_customized_keys.sql`을 통해 저장합니다. 계정당 예산은 각 공급자의 `customized_keys`로 설정합니다(`max_scopes` 1–64, `max_mappings` 1–512, `max_name_characters` 1–1024이며, 세 값의 조합이 패킷 본문에 들어가야 합니다). 예산을 넘기면 다른 저장 매핑을 밀어내지 않고 실패합니다.

만료된 아이템 버리기도 같은 방식으로 선언합니다. `dialect.messages` 행의 `inventory_actions` 옵션이 `gcpReqInventoryThrowItem`/`gspRepInventoryThrowItem`을 묶고(모든 legacy 빌드와 `kr-1256`에서 켬), 빌드 자신의 bundle 안 `inventory.throw` 행이 저장 장비 서비스를 통해 해당 아이템을 제거합니다. 동작은 의도적으로 좁습니다. 인증된 한 플레이어의 두 가방에서 해당 만료 아이템만 제거하고, 일치하는 이용 권한이 유효하면 제거 전체를 거부하며, 빌드의 범주 6 제외 규칙을 따릅니다. 경기 중 요청은 거부됩니다. 실제 클라이언트 수용은 아직 남아 있으며, 패킷 표 지원만으로 다른 빌드에서 이 옵션을 선언할 수는 없습니다.

**`launcher.http`**는 선택적 공용 런처 API입니다. 기본적으로 비활성화되며 관리와 분리됩니다. `minimum_launcher_version`(비우면 검사 안 함)과 런처가 적용하는 플러그인별 유형 규칙인 `client_policy`를 받습니다. 규칙은 스토어 ID와 선언된 스토어 소스만 가리키며 실행 파일 경로는 절대 포함하지 않습니다. 최소 버전이 설정되면 구버전 런처는 HTTP 426을 받습니다. 각 로비 행의 `authentication` 옵션(`BFF_LOGIN_AUTHENTICATION`, 기본값 `ticket`)은 런처 티켓을 교환하는 로그인만 허용하며 이때 이 행이 활성 상태여야 합니다. `open`은 모든 계정 이름을 허용하므로 신뢰할 수 있는 LAN에서만 사용하십시오. [런처](launcher.md) 페이지에서 API, 클라이언트 프리셋, 계정과 티켓 흐름, 운영자 설정을 설명합니다.

런처 요청 및 로그인 세션 제한은 `launcher.http` 행에서 설정합니다.

| 키 | 기본값 | 동작 |
|---|---|---|
| `requests_per_minute` | `120` | 전송 연결의 원본 IP별 1분 요청 수입니다. 전달 헤더로 원본 IP가 바뀌지 않습니다. |
| `max_rate_limit_addresses` | `4096` | 보관할 IP별 시간 창 수(1–100000)이며, 가득 차면 가장 오래된 창을 제거합니다. |
| `max_sessions_per_account` | `8` | 계정별 유효한 영구 로그인 세션 수(1–1024)이며, 새 로그인 시 생성 시간이 가장 오래된 세션을 취소합니다. |
| `max_sessions` | `1024` | 기존 프로세스 내 접근 토큰 및 대기 중 게임 티켓 수 제한입니다. |

오래된 세션을 취소하면 이 프로세스의 접근 토큰과 대기 중 게임 티켓도 제거됩니다. 만료된 갱신 세션은 리스너 시작 전에 삭제하며, 이후 로그인 과정에서는 최대 1시간에 한 번 삭제합니다. 계정 제한은 세션 저장소에서 적용되므로 서버를 다시 시작해도 유지됩니다.

## 검증과 안전

- 알 수 없는 구성 키와 잘못된 값은 구성 또는 활성화를 실패시킵니다. 무시되지 않습니다. 활성화에 실패한 행은 조용히 건너뛰지 않고 상태와 진단과 함께 보고됩니다.
- `--dump-config`는 자격 증명을 포함한 보간된 값을 출력합니다. 버그 보고서에 공유하기 전에 민감 정보를 제거하십시오.
- `.env`나 `local.patch.yaml`을 커밋하지 마십시오. 둘 다 Git에서 제외되며 비밀 정보나 머신별 경로를 담을 수 있습니다.
- 모든 로컬 `run`에 `--patch tools/local-checks/isolated-memory.patch.yaml`을 추가하십시오. 개발자의 `.env`가 실제 데이터베이스에 닿지 않게 하는 유일한 신뢰할 수 있는 방법입니다.
- 프로필이나 패치 파일 변경은 다음 시작부터 적용됩니다. 프로필은 시작할 때 한 번만 구성됩니다. 관리 API로 개별 행을 재로드할 수 있지만 이는 플러그인 수명 주기 작업이며 새 프로필을 적용하는 방법이 아닙니다. 재로드 동작은 [플러그인](plugins.md)을 참고하십시오.

## 문제가 생기면

- **환경 변수 누락으로 구성이 실패합니다.** 오류 메시지가 변수 이름을 알려 줍니다. `.env`(또는 프로세스 환경)에 추가하거나, 빈 값이 유효한 곳에서는 `${VAR:-default}` 형식을 사용하십시오.
- **패치가 알 수 없는 행을 가리킵니다.** 프로필과 패치 목록이 어긋난 것입니다. 패치를 적용하지 않은 프로필이나 해당 행을 제공한 번들에서 `--dump-config`로 행 id를 확인하십시오.
- **게임 프로필이 클라이언트 데이터 메시지와 함께 활성화에 실패합니다.** 메시지에 나온 `data`/`dataraw` 디렉터리가 없습니다. 해당 빌드 id의 디렉터리를 준비하거나 `management`를 사용하십시오.
- **리스너가 바인딩할 수 없습니다.** 다른 프로세스가 포트를 점유하고 있습니다. 빌드별 포트 변수를 바꾸거나 해당 프로세스를 중지하십시오.

순서대로 확인하는 목록은 [문제 해결](troubleshooting.md)을 참고하십시오.

## 다음 단계

[배포](deployment.md)에서 릴리스 빌드와 실행을 이어서 살펴보거나, [플러그인](plugins.md)에서 각 행이 실제로 하는 일을 확인하십시오.

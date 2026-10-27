---
title: "자체 호스팅 스토어"
description: "도구의 실제 제한에 맞춰 런처 클라이언트, 프로필, 플러그인을 준비하고 게시합니다."
updated: 2026-10-22
---

# 자체 호스팅 스토어 {#self-hosted-stores}

## 범위와 준비 사항 {#prerequisites}

아래 예제, 필드 이름, 제한은 이 [런처 소스 스냅샷](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/tree/5f1d4267d0ddd68390dc27fe12f678b06b846e6e)을 기준으로 합니다. 도구, 클라이언트 정의, 검증기는 같은 버전을 사용하세요. 더 새로운 런처 리비전을 사용한다면 해당 리비전에 포함된 형식 문서를 따르고 같은 리비전의 검증기로 확인한 뒤 게시하세요.

스토어는 UTF-8 TOML 카탈로그 하나를 제공하며 기본 경로는 `FantasyTale/source.toml`입니다. schema 2는 클라이언트 패키지, 플러그인 패키지, 클라이언트 프로필을 함께 담습니다. 매니페스트와 실제 파일은 별도로 저장하고 필요할 때 가져옵니다. 계정 인증과 로그인 티켓은 [서버의 런처 API](launcher.md)가 담당합니다. 스토어 추가로 런처 앱 자체의 업데이트 권한을 부여하지는 않습니다. [런처 업데이트 소스](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/launcher-updates.md)는 별도로 설정합니다.

아래의 `tools/publish-store/`, `tools/publish-launcher/`, `games/`, `plugin.toml` 경로는 모두 별도 [Fantasy Tale Launcher 저장소](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher)에 속하며 이 서버 저장소의 경로가 아닙니다. 소스를 받아 저장소 루트를 예시 위치인 `D:/Launcher`로 사용하세요. Python 3.11 이상이 필요하며 업로드 도구에는 `requests`도 필요합니다. 카탈로그 검증에는 .NET 10 SDK가 필요합니다. 이 운영자용 도구는 서버 배포 패키지에 포함되지 않습니다.

다음 PowerShell 준비 과정은 Python이 설치되어 있고 `D:/Store`를 별도 작업 디렉터리로 사용한다고 가정합니다. 이후 Python 명령은 이 환경을 사용하며 각 실행 명령 묶음은 런처 저장소 루트를 명시적으로 선택합니다.

```powershell
Set-Location 'D:/Launcher'
python -m venv D:/Store/.venv
& D:/Store/.venv/Scripts/python.exe -m pip install requests
```

## 호스팅과 요청 {#hosting}

| 종류 | 소스 루트 | 카탈로그 요청 |
| --- | --- | --- |
| `http` | `https://files.example.org/` | `GET /FantasyTale/source.toml` |
| `alist` | `https://archive.example.org/` | `POST /api/fs/get` 후 확인된 URL로 GET |

카탈로그 경로는 설정할 수 있습니다. 카탈로그와 매니페스트의 경로는 참조 문서가 아닌 소스 루트 기준입니다. 정적 HTTP 서버는 HTML 미리 보기나 JSON 래퍼가 아닌 TOML 원본 바이트를 반환해야 합니다. HEAD는 필요하지 않습니다. 정확한 길이와 HTTP Range 지원은 다운로드 재개에 사용됩니다.

AList는 첫 번째 JSON 요청을 받고 두 번째 형식으로 응답합니다. `size`는 실제 바이트 수로 바꾸세요.

```json
{"path":"/FantasyTale/source.toml","password":""}
```

```json
{"code":200,"message":"success","data":{"is_dir":false,"size":1234,"raw_url":"https://storage.example.org/source.toml","sign":""}}
```

`raw_url`이 없으면 어댑터는 `/d/<path>?sign=<sign>`을 사용합니다. URL 확인 메타데이터는 64 KiB로 제한되며 최종 다운로드는 원본 바이트를 반환해야 합니다. 임시 URL은 필요할 때 확인하며 패키지 식별자로 저장하지 않습니다. 데스크톱 소스 편집기는 공개 소스를 설정합니다. 탐색에 관리자 자격 증명을 요구하지 마세요.

## 카탈로그 필드 {#catalog-fields}

```toml
schema = 2

[catalog]
id = "community"
name = { en = "Community store", zh-CN = "社区商店" }
```

`[[packages]]`와 `[[profiles]]`를 추가하며 빈 배열은 생략할 수 있습니다. 알 수 없는 키는 거부됩니다. 런처 파서는 schema 2를 최대 4 MiB, 패키지 릴리스 256개, 프로필 512개까지 읽습니다. schema 1은 256 KiB로 제한되며 프로필과 인라인 플러그인 정의를 포함하지 않습니다. 개별 프로필과 인라인 플러그인 정의도 각각 256 KiB로 제한됩니다.

| 패키지 필드 | 의미 |
| --- | --- |
| `id`, `kind`, `game` | 패키지 ID, 종류, 게임. 여기서는 `client` 또는 `plugin` 사용 |
| `builds` | 클라이언트는 빌드 하나, 스토어 플러그인은 구체적인 지원 빌드 ID 목록 |
| `version` | 클라이언트의 선택적 업데이트 표시값, 플러그인의 SemVer |
| `name`, `description` | 영어 이름을 포함한 다국어 텍스트. 설명은 선택 사항 |
| `publisher` | 필수 표시 이름이며 인증된 신원은 아님 |
| `size` | 매니페스트에 기록된 전체 다운로드 바이트 수 |
| `manifest`, `manifest_sha256` | 매니페스트 상대 경로와 정확한 원본 바이트의 SHA-256 |
| `update_mode` | 클라이언트의 현재 스냅샷은 `files`, 버전별 릴리스는 `release` |
| `revision` | 선택적 정렬 값. 양수 리비전은 같은 패키지 내에서 고유해야 함 |
| `authors`, `uploader` | 선택적 작성자 목록과 업로더 표시 이름 |
| `plugin_definition` | schema 2 전용 완전한 인라인 플러그인 정의 |
| `minimum_launcher_version` | schema 2 전용 선택적 최소 앱 SemVer |

패키지 식별은 설정된 소스 범위에 속합니다. 다른 소스에 같은 ID를 복사해도 설치된 패키지를 교체할 수 없습니다. 상대 경로에는 URL, 상위 경로 이동, 역슬래시, 퍼센트 인코딩, 쿼리, 프래그먼트를 사용할 수 없습니다. 크기와 해시는 실제 바이트로 생성하세요.

## 파일 매니페스트 {#file-manifests}

다음은 파일 동기화 클라이언트의 개요이며 실제 릴리스 메타데이터가 아닙니다. 예시 크기와 두 `<file-sha256>` 값을 생성된 값으로 바꾸세요. 카탈로그의 `manifest_sha256`은 전체 UTF-8 매니페스트를 해시하며 각 `[[files]]`는 자신의 파일 내용을 별도로 해시합니다.

```toml
schema = 1

[release]
id = "bubblefighter.community.client"
game = "bubblefighter"
build = "cn-279"

[[files]]
path = "BubbleFighter.exe"
source_path = "FantasyTale/files/<file-sha256>"
size = 1234
sha256 = "<file-sha256>"
```

`path`는 설치 디렉터리 기준 대상 경로이고 `source_path`는 소스 루트 기준입니다. 플러그인 매니페스트는 클라이언트의 단일 `build` 대신 `[release] version`과 `builds`를 사용합니다. 매니페스트는 256 KiB로 제한됩니다. 완전한 출력은 게시 도구로 생성하세요. 압축 파일 매니페스트의 추가 필드는 [런처 스토어 형식](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md)을 참조하세요.

## 클라이언트 로컬 생성 {#clients}

위 준비를 마친 뒤 예시를 `D:/Store/publish.toml`에 저장하세요. 입력은 배포할 파일만 담은 디렉터리이며 출력은 모든 입력 디렉터리 밖에 있어야 합니다. 게시 설정의 상대 경로는 설정 파일이 있는 디렉터리 기준입니다.

```toml
[store]
id = "community"
name = "Community store"
output = "./generated"
storage_gib = 200

[[clients]]
game = "bubblefighter"
build = "cn-279"
name = "China 2011"
directory = "./clients/cn-279"
exclude = ["logs/*", "*.log", "*.dmp"]
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_store.py --config D:/Store/publish.toml
```

도구는 `D:/Store/generated/FantasyTale/source.toml`, 변경 불가능한 해시 매니페스트, 콘텐츠 주소 기반 파일을 생성합니다. **schema 1**을 출력하며 설정된 클라이언트/서버 행으로 자체 카탈로그를 교체합니다. 기존 플러그인이나 프로필을 병합하지 않습니다. 운영 중인 통합 카탈로그를 출력 대상으로 지정하지 마세요. `--watch 30`도 이 교체를 반복합니다. `--full-check`는 메타데이터 캐시를 신뢰하지 않고 입력 파일을 다시 해시합니다. 생성 디렉터리와 게시 트리를 분리하세요. 카탈로그 행만으로 새 게임을 구현할 수는 없습니다. 모듈과 유효한 빌드 정의가 이미 지원해야 합니다.

## 프로필 합성과 검증 {#profiles}

런처 저장소의 지원되는 `games/<game>/builds/<build>.toml`에서 시작하세요. `[build]`는 ID, 실행 파일, 표시 이름, 브랜치, `year`, `client_version`, 선택적 `release_version`을 담습니다. 선택적 `region`은 대문자 두 글자입니다. 위에 링크한 스냅샷에서 `client_version`은 정수이고 선택적 `wire_version`은 프로토콜 식별자 문자열입니다. 해당 런처 리비전의 필드 이름과 타입을 유지하세요. 이 파서는 `build_date`, `protocol_version`, `patch_version`을 허용하지 않습니다. `[launch]`의 `login`, `arguments`, `launcher_login`(`required`, `optional`, `none`), `requires_elevation`은 모듈별 시작 및 로그인 정책을 정의합니다. `[plugins]`의 ID 배열 중 `required`는 활성화를 강제하고 `recommended`는 추천, `default`는 기본 선택을 제공합니다. 지문이나 로그인 동작을 추측하지 마세요.

설정을 `D:/Store/profiles.toml`에 저장하고 참조 정의를 `D:/Store/profiles/cn-279.toml`에 두세요. 보존할 모든 패키지를 담은 `D:/Store/package-catalog.toml`을 먼저 준비하세요. 명령은 Python 3.11+ 및 .NET 10 SDK를 갖춘 런처 루트에서 실행합니다.

```toml
[[profiles]]
definition = "./profiles/cn-279.toml"
revision = 1
```

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/compose_catalog.py --catalog D:/Store/package-catalog.toml --config D:/Store/profiles.toml --output D:/Store/unified-source.toml
dotnet run --project tools/publish-launcher -c Release -- --validate-catalog D:/Store/unified-source.toml
```

**합성 설정**의 `definition`은 파일 경로이지만 **출력 카탈로그**에는 전체 텍스트와 `game`, `build`, 양수 `revision`이 들어갑니다. 정의가 바뀔 때마다 리비전을 올리세요. 합성기는 schema 1/2를 받아 입력 패키지 행과 기존 프로필을 유지하고 존재하지 않는 새 출력 경로에 schema 2를 생성합니다. 설정의 프로필을 병합하지만 여러 패키지 카탈로그를 병합하지는 않습니다. 합성 전에 새 클라이언트 스냅샷과 유지할 모든 플러그인/패키지 행을 조정하세요. 새 클라이언트 카탈로그만 사용하면 다른 행이 사라집니다. 선택적 `--legacy-output <new-path>`는 프로필과 인라인 플러그인 메타데이터가 없는 schema 1 사본을 별도로 출력합니다.

## 플러그인 작성자와 게시 {#plugins}

런처 플러그인은 .NET 서버 플러그인과 다릅니다. 배포 폴더 루트에는 `plugin.toml`과 런타임 파일이 있습니다. `[plugin]`은 ID, 게임, SemVer, 다국어 이름, 선택적 `authors`를 선언하고 `[compatibility]`는 빌드 지원을 선언합니다. [런처 hook 플러그인 형식](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/hook/plugin-format.md)과 [파일 교체 플러그인](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/file-plugins.md)을 참조하세요. 하나의 플러그인 ID가 여러 빌드를 지원할 수 있습니다. 플러그인 정의가 선택자를 허용하더라도 스토어 패키지 `builds`에는 구체적인 빌드 ID가 필요합니다.

`authors`는 작성자를 나타내고 `uploader`는 선택적 업로더 표시 이름이며 비어 있으면 생략합니다. 두 필드 모두 신원을 인증하지 않습니다. 바이트가 바뀌면 새 SemVer를 사용하세요. 메타데이터 추가를 위해 이미 게시한 버전을 덮어쓰지 마세요.

다음 명령은 **실제로 원격 게시**하며 CLI 시험 실행 모드는 없습니다. 기존 schema 1 카탈로그, 쓰기 가능한 드라이버, 위 준비 사항을 갖춘 독립된 AList 스테이징 소스를 사용하세요. `FT_ALIST_USERNAME`과 `FT_ALIST_PASSWORD`는 프로세스 환경으로만 제공하세요. 목적지를 검토한 뒤 `staging.example.org`를 바꾸세요.

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/publish_plugins.py --package D:/Store/plugin-package --output D:/Store/plugin-output --base-url https://staging.example.org/
```

게시기는 파일, 매니페스트, 카탈로그 순으로 업로드합니다. 생성한 패키지 텍스트를 원격 카탈로그에 덧붙이며 기존 `(id, version)`의 메타데이터가 달라지면 거부합니다. 입력에 schema 2가 적혀 있어도 출력은 **256 KiB, 릴리스 256개**로 제한됩니다. 플러그인 패키지 하나의 제한은 파일 256개와 128 MiB입니다. 기존 텍스트를 보존하지만 schema 2 전체를 검증하거나 카탈로그의 `plugin_definition` / `minimum_launcher_version` 필드를 생성하지 않습니다. 일반적인 schema 2 편집기나 로컬 준비 명령이 아닙니다.

통합 합성 전에 스테이징 카탈로그 전체와 참조된 모든 객체를 보존하세요. 인라인 메타데이터는 schema 2 후보에 직접 추가하고 검증해야 합니다. 내장 ID/게임/버전은 패키지와 일치해야 하며 별도로 지정한 최소 버전은 내장 `[plugin] minimum_launcher_version`과 같아야 합니다. 합성기는 기존 패키지 필드를 보존할 뿐 생성하지 않습니다. 메타데이터 다운로드는 플러그인 코드를 실행하지 않습니다.

## 도구 호환성 {#tool-compatibility}

| 도구 또는 파서 | 실제 동작 |
| --- | --- |
| 런처 카탈로그 파서 | schema 1: 256 KiB, schema 2: 4 MiB, 패키지 최대 256개 / 프로필 512개 |
| `publish_store.py` | 로컬 schema 1 생성기. 자체 카탈로그를 덮어쓰며 패키지/프로필 병합 없음 |
| `publish_plugins.py` | 원격 AList 게시기. 텍스트 추가, 256 KiB / 릴리스 256개, 인라인 메타데이터 생성 없음 |
| `compose_catalog.py` | schema 1/2 입력, 최대 4 MiB schema 2 출력. 패키지 보존, 프로필 병합 |
| `upload_alist.py` | 전체 참조 그래프. schema 2 카탈로그 최대 4 MiB, 매니페스트 256 KiB, 객체 4096개 |

## 안전한 게시 순서 {#publication}

1. 현재 카탈로그와 객체를 보존하고 한 게시자가 작업하도록 조정하세요.
2. 별도 디렉터리에 클라이언트를 생성하세요. 플러그인 CLI를 쓴다면 독립된 스테이징 소스에 먼저 게시하세요. 관련 없는 행과 기존 프로필을 보존하며 최종 패키지 카탈로그를 구성하세요. 클라이언트 생성기는 이 병합을 하지 않습니다.
3. 새 통합 카탈로그를 합성하고 위 검증기를 실행하세요. `D:/Store/release` 같은 별도 게시 루트에 **모든** 참조 매니페스트와 파일을 준비하고 검증한 카탈로그를 그 루트의 `FantasyTale/source.toml`에 두세요. AList 업로더는 이전에 참조된 객체도 로컬에 있어야 합니다.
4. HTTP에서는 파일, 변경 불가능한 매니페스트 순으로 복사하고 카탈로그를 마지막에 원자적으로 교체하세요. 이전 매니페스트를 선택한 다운로드를 위해 기존 객체를 유지하세요.
5. AList에서는 위 준비가 끝난 뒤 다음 게시 명령을 실행하세요. 시험 실행이 아닙니다. Python 환경에 `requests`, 프로세스 환경에 `FT_ALIST_USERNAME` / `FT_ALIST_PASSWORD`가 필요합니다.

```powershell
Set-Location 'D:/Launcher'
& D:/Store/.venv/Scripts/python.exe tools/publish-store/upload_alist.py --input D:/Store/release --base-url https://archive.example.org/ --workers 2 --verify-timeout-seconds 900
```

업로더는 로컬 해시와 익명 원격 다운로드를 확인하고 파일, 매니페스트 순으로 업로드합니다. 이전 공개 카탈로그의 해시를 확인한 뒤 카탈로그를 마지막에 교체합니다. 이 낙관적 확인은 원자적인 다중 작성자 잠금이 아닙니다. `--workers`는 1–4(기본 2), `--verify-timeout-seconds`는 객체당 30–3600초(기본 900)입니다. `--catalog`는 카탈로그 경로를 바꾸고 `--no-proxy`는 이 업로드 프로세스에만 적용됩니다. 쓰기 가능한 AList 드라이버와 익명으로 읽을 수 있는 최종 파일이 필요합니다. 추가 옵션은 [런처 업로드 문서](https://github.com/Wasabi-FantasyTaleDev/FantasyTale.Launcher/blob/main/docs/store-sources.md#uploading-a-generated-catalog-to-alist)를 참조하세요.

## 검수와 경계 {#verification}

익명 소비자로 카탈로그를 가져와 검증하고 참조 매니페스트 하나를 받아 원본 바이트의 SHA-256을 비교한 뒤 별도 테스트 디렉터리에 설치하세요. 프로필 ID, 필수/추천/기본 플러그인 선택, 여러 빌드의 호환성을 확인하세요. 해시는 선택한 카탈로그와의 무결성을 증명할 뿐 게시자의 신원을 인증하지 않습니다. 계정 데이터, 자격 증명, 티켓, 로컬 재정의, 진단 파일, 비공개 연구 자료는 제외하세요. 이 가이드는 도구 계약을 설명하며 특정 스토어, 데스크톱 흐름, 실제 클라이언트 실행의 검수 완료를 뜻하지 않습니다.

## 서버 스토어 다운로드 {#server-store}

서버 WebUI의 **스토어**는 일반 플러그인 행 `distribution.sources`, `distribution.plans`, `distribution.packages`, `distribution.jobs`를 사용하여 HTTP, AList, GitHub 카탈로그와 검증 다운로드를 제공합니다. 위의 데스크톱 런처 절차와 다릅니다. 파일은 격리된 임시 저장소에만 저장되며 실행 중인 서버나 플러그인을 설치, 활성화, 업데이트 또는 롤백하지 않습니다.

1. **스토어 → 소스 관리**에서 고유 ID, 표시 이름, 종류, 기본 URL, 카탈로그 경로를 입력합니다. URL에 자격 증명을 넣을 수 없습니다. 저장한 소스 순서가 같은 패키지의 기본 소스를 결정하며 실패한 소스는 오류로 표시합니다.
2. 카탈로그에서 소스를 선택합니다. GitHub Release는 게시자의 SHA-256이 있는 첨부 파일을 선택해야 합니다. 저장소 URL로 Release를 조회하는 기능은 미리 보기이며 설치 가능한 패키지를 직접 만들지 않습니다. 클라이언트 패키지도 이 서버 화면에서는 미리 보기만 지원합니다.
3. **임시 저장소로 다운로드**를 선택합니다. 선택은 현재 소스 수정 버전과 정확한 릴리스 정보에 연결되며 manifest 및 파일 해시를 확인하고 진행 상황을 기록합니다. 브라우저를 닫아도 접수된 작업은 취소되지 않습니다. 작업의 취소 기능을 사용하세요.
4. **저장 완료**는 압축을 풀지 않은 파일을 검증했다는 뜻이며 플러그인 설치 완료가 아닙니다. 파일 재검증, 이전 작업 조회, 명시적인 임시 파일 삭제를 지원합니다. 실패나 취소 시 부분 파일을 정리하고 결과를 남깁니다. 재시작 시 끝나지 않은 작업은 중단 상태로 표시합니다.

소스는 기본적으로 `data/distribution-sources.json`, 다운로드는 `data/distribution-staging`에 저장됩니다. 행 옵션 기본값은 `maximum_document_bytes` 4194304, `maximum_sources` 512, 메타데이터 제한 시간 `request_timeout_ms` 30000, 전송 제한 시간 `transfer_timeout_ms` 1800000입니다. `maximum_payload_bytes`는 선택 사항이며 선언된 정확한 크기와 디스크 여유 공간은 항상 확인합니다. `free_space_reserve_bytes`는 0, 작업 목록의 `history_page_size`는 20이 기본값입니다. 이는 배포 설정이며 패키지 형식 요구 사항이 아닙니다. 완료된 다운로드는 자동 삭제하지 않습니다.

서버 응용 프로그램 manifest는 기존 schema 1 형식을 사용합니다. 서버 플러그인 manifest에는 실험 판독기 `distribution.packages.experimental_release_format: true`가 필요하며 기본값은 false입니다. 이 제안은 아직 런처의 확정된 공통 규격이 아닙니다. GitHub 첨부 파일 해시는 플랫폼 호환성을 증명하지 않습니다. 릴리스 첨부 파일은 [GitHub 미러](store.ko.md#github-mirror)로 내려받을 수 있으며 여전히 그 해시와 대조합니다. 설치된 서버의 스토어 API에는 관리자 세션과 기존 요청 보호가 적용되지만 작업별 스토어 권한 노드는 아직 없습니다. 명령 모드 관리는 루프백에서만 열립니다. [클라이언트 플러그인 실행 정책](plugins.md#client-plugin-policy)은 별도 권한이 필요합니다.

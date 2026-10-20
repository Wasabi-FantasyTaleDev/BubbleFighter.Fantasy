---
title: "기여하기"
description: "도구 모음을 설정하고 빌드·테스트하며, 프로젝트 규칙을 따르고 검토 가능한 변경을 제출합니다."
updated: 2026-10-18
---

## 이 페이지의 범위

이 페이지는 새 기여자가 깨끗한 머신에서 검토·병합 가능한 변경까지 가는 길을 다룹니다. 먼저 읽을 규칙, 필요한 도구 모음, 빌드와 검증 명령, 저장소 구조, 변경에 따르는 의무, 검토 절차입니다. Bubble Fighter Fantasy는 Fantasy Tale Studio가 관리하는 프리릴리스 커뮤니티 서버이며, C#과 .NET 10으로 작성되고 플러그인으로 구성됩니다.

**이 페이지의 대상.** 처음 기여하는 사람과 다른 사람의 변경을 재현하거나 검토해야 하는 기여자입니다.

**시작하기 전에.**

- 저장소 루트의 `AGENTS.md`와 `CONTRIBUTING.md`를 읽습니다. `AGENTS.md`는 구속력이 있으며 아키텍처, packet 데이터, 증거, 명명, 언어 규칙을 담고 있습니다.
- .NET 10 SDK와 Git을 설치합니다. 서버를 빌드하고 실행하려면 둘 다 필요합니다.
- 실제 게임 클라이언트와 그 데이터는 네이티브 클라이언트 테스트에만 필요합니다. 저장소는 둘 다 포함하지 않습니다.

## 먼저 규칙 읽기

| 규칙 | 실제 의미 |
| --- | --- |
| 모든 것이 플러그인 | 새 기능은 커널이나 Host의 특례가 아니라 profile의 플러그인 행으로 제공됩니다. |
| Context는 서비스 레지스트리 | 선언된 인터페이스 형식으로만 서비스를 사용하고, 다른 플러그인의 구체 클래스에 접근하지 않습니다. |
| 등록은 effect | 모든 `Register*`/`On*`은 해제를 반환하며, `context.Effect(...)`에 넘겨 언로드 시 역순으로 되돌립니다. |
| 하드코딩된 조정 값 금지 | 배포마다 달라지는 값은 상수가 아니라 플러그인 행의 검증된 설정 필드입니다. |
| 실패는 크게 | 잘못된 설정, 누락된 서비스, 잘못된 packet, 연결 실패는 오류이며 조용한 기본값으로 넘어가지 않습니다. |
| 리소스는 유한 | 무한 큐, 버퍼, 파일 싱크를 만들지 않습니다. 넘침은 보고되고 게임을 지연시키지 않습니다. |
| packet 레이아웃은 데이터 | 구조는 `packets/`에 기준 빌드 기준으로 한 번 쓰고 빌드별로 주석을 달며, 코드는 필드 이름으로 값을 제공합니다. |
| 증거는 클라이언트에서 | 레이아웃과 순서는 클라이언트 자체 코드를 읽어 확정하고, 각 주장은 빌드가 붙은 함수 주소를 유지합니다. |
| 코드는 영어 | 코드, 주석, XML 문서, 커밋 메시지, `opcodes.yaml`, `profiles/opcode-policy.md`는 영어를 씁니다. |
| 사용자 문서는 이중 언어 | 영어 안내서와 중국어 번역을 같은 커밋에서 갱신합니다. 사이트 페이지에는 번체 중국어와 한국어도 필요합니다. |

어디에도 emoji를 쓰지 않습니다. 타사 프로젝트, 다른 서버, 참조 트리를 인용하거나 언급하지 말고, 이 프로젝트 자체 용어로 설계를 설명합니다.

## 도구 모음 설정

| 도구 | 용도 | 확인 |
| --- | --- | --- |
| .NET 10 SDK | 서버 빌드와 실행 | `dotnet --version`이 10.x 보고 |
| Git | 복제, 브랜치, 검증 게이트 | `git --version` |
| Python 3 | CI 게이트 스크립트와 단위 테스트 | `python --version` |
| Node.js와 npm | 문서 사이트, TypeScript SDK 적합성 | `node --version`, `npm --version` |
| Go | Go SDK 적합성 검사와 테스트 | `go version` |
| Rust와 cargo, rustup, `wasm32-wasip1` | Rust SDK 적합성과 WebAssembly 테스트 | `rustc --version`; `rustup target add wasm32-wasip1` |

서버를 빌드하고 실행하는 데는 .NET과 Git만 필요합니다. 나머지 도구 모음은 검증 게이트, 문서 사이트, SDK 테스트 모음에 필요하며, 없으면 게이트가 조용히 건너뛰지 않고 보고합니다.

PowerShell이나 셸에서 체크아웃을 둘 디렉터리로 이동해 실행합니다:

```sh
git clone https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy.git
cd BubbleFighter.Fantasy
dotnet build BubbleFighter.Fantasy.sln -c Release
```

**확인.** 복제 디렉터리에 `BubbleFighter.Fantasy.sln`이 있고, 빌드가 `0 Warning(s)`와 `0 Error(s)`로 끝납니다.

## 로컬 빌드와 테스트

### 솔루션 빌드

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
```

경고는 오류입니다(`TreatWarningsAsErrors` 활성화). 또한 `GenerateDocumentationFile`은 모든 공개 형식과 멤버에 의미 있는 XML 요약을 요구합니다. 원인을 고치고, 빌드를 통과시키려고 경고를 억제하지 마세요.

### 테스트 프로젝트 실행

```sh
dotnet test BubbleFighter.Fantasy.sln -c Release
```

개발 중 빠른 반복에 쓰는 명령입니다. 릴리스 게이트는 이를 직접 쓰지 않고, `git ls-files tests/*/*.csproj`로 솔루션 밖의 프로젝트까지 포함한 모든 추적 테스트 프로젝트를 찾아 프로젝트마다 TRX 하나로 순차 실행합니다. 시도마다 별도 결과 디렉터리나 파일 이름을 사용해 재실행이 이전 실패를 덮어쓰지 않게 합니다.

### 게이트 자체 테스트 실행

```sh
python -m unittest discover -s tools/ci -p "test_*.py"
```

이 테스트는 게이트 실행기 자체를 보호해, 비어 있거나 누락되었거나 건너뛴 보고서가 성공으로 오인되지 않게 합니다.

### 공개 게이트 실행

```sh
python tools/ci/serial-gates.py --public --results-directory artifacts/validation/public-01
```

시도마다 새롭고 존재하지 않는 결과 디렉터리를 사용합니다. 실행기는 이미 있는 디렉터리를 거부합니다. 게이트는 Release를 빌드하고 SDK 적합성을 실행하며 테스트 프로젝트를 순차 실행하고 packet 테이블을 검사하며 격리 패치를 적용한 `kr-1256` 설정을 덤프합니다. 마지막에 `PASS public gates: <n> populated test reports, <n> tests, no skips`를 출력하고 제외 범위를 `scope.json`에 기록합니다. 공개 모드는 모든 추적 테스트 프로젝트를 실행하되 `Category=LocalClientData` 케이스만 제외하며, 완전한 릴리스 게이트가 아닙니다. `CI=true`이면 실행기는 `.env` 또는 `profiles/*/local.patch.yaml`이 있는 체크아웃도 거부합니다.

**확인.** 종료 코드가 0이고 마지막 줄이 위의 `PASS` 줄입니다. 0이 아닌 종료는 `FAIL <reason>`을 출력하며, 실패 단계의 로그는 결과 디렉터리에 있습니다.

### 로컬 Host 확인 실행

[빠른 시작](quickstart.md)을 따릅니다. `.env.example`을 `.env`로 복사하고 `BFF_MYSQL_PASSWORD`를 비어 있지 않은 임시 값으로 설정하며, `--dump-config`를 포함해 항상 격리 패치를 추가합니다:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml
```

이 패치는 `persistence.mysql`을 비활성화하고 메모리 영속성을 활성화하므로, 개발자의 `.env`나 로컬 재정의가 실제 데이터베이스에 접속하지 않습니다. 테스트나 로컬 확인을 실제 데이터베이스로 향하게 하지 마세요. **확인:** 다른 터미널에서 `http://127.0.0.1:8080/health`가 `{"status":"ok", ...}`를 반환합니다. Ctrl+C로 중지합니다.

## 저장소 구조

| 경로 | 내용 |
| --- | --- |
| `src/BubbleFighter.Fantasy.Abstractions`, `src/BubbleFighter.Fantasy.Contracts` | 공유 플러그인 계약. Host가 한 번 로드해 모든 플러그인과 공유 |
| `src/BubbleFighter.Fantasy.Kernel` | 플러그인 런타임: 검색, 활성화, 서비스 레지스트리, effect, profile 조합 |
| `src/BubbleFighter.Fantasy.Host` | `bff` 콘솔 Host와 bootstrap profile |
| `src/Plugins/` | 리스너, 다이얼렉트, 게임 기능, 영속성, 관리, SDK loader |
| `profiles/` | profile 행, bundle, 패치, 빌드별 opcode 테이블 |
| `packets/` | packet 레이아웃 데이터와 빌드별 상속 |
| `sdk/` | 선택적 Python, TypeScript, Go, Rust 플러그인 SDK |
| `tests/` | .NET 테스트 프로젝트와 공유 fixture |
| `tools/` | 검증 게이트(`tools/ci`), 로컬 확인 패치(`tools/local-checks`), 유지 관리 스크립트 |
| `docs/` | 게시 가이드의 원본 |
| `docs-site/` | VitePress 사이트, 페이지 목록, 콘텐츠 동기화 |
| `deploy/` | Docker 파일과 Compose 템플릿 |

## 변경 만들기

### 브랜치와 작업 분리

자체 브랜치에서 작업하고 커밋 하나에 논리적 변경 하나만 담습니다. 변경은 검토 후에만 `main`에 들어갑니다. 커밋 메시지는 영어로 씁니다: 무엇을, 왜 바꿨고, 어떤 증거가 뒷받침하는지.

```sh
git switch -c fix/short-topic
```

### 증거 제공

- 변경과 함께 테스트를 추가하거나 갱신합니다. packet 작업에는 변경 전 실패하고 변경 후 통과하는 바이트 고정 테스트를 넣습니다.
- 대상 테스트를 먼저 실행하고 전체 빌드와 테스트를 실행합니다. 이전 실패 시도의 출력을 보관하고 재실행으로 덮어쓰지 않습니다.
- 코드가 하는 방식으로 증거를 인용합니다: wire 주장에는 빌드가 붙은 클라이언트 함수 주소(`kr-812 sub_810C20`). 테스트 세션 증거 등급의 opcode 행은 세션을 그 행에 적고 코드 주석에는 적지 않습니다.
- 변경 설명에 무엇을 실행했고 무엇을 증명하는지 적습니다. 자동화 테스트는 실제 클라이언트 수용을 증명하지 않습니다.

### 플러그인과 리소스 규칙 따르기

- 기능을 플러그인 행으로 추가합니다: `[Plugin(...)]`에 필요한 인터페이스와 제공 인터페이스를 선언하고, `IPluginContext`로 사용하며, 그것을 통해서만 등록합니다.
- 모든 등록과 외부 리소스를 effect로 소유해 언로드 시 되돌립니다. 무한 백그라운드 작업이나 큐를 남기지 않습니다.
- 배포마다 달라지는 값은 상수가 아니라 행의 검증된 옵션 클래스(`[Required]`, `[Range]`, `IValidatableOptions`)에 둡니다.
- 공유 계약 어셈블리를 기준으로 빌드하고, `Abstractions`와 `Contracts`의 사본을 플러그인과 함께 배포하지 않습니다.
- 공개 형식과 멤버에는 실제 XML 요약을 답니다. 주석은 왜 그런지 설명하고 증거를 가리킵니다.

### packet 또는 opcode 데이터 변경

레이아웃을 만지기 전에 `packets/README.md`와 `profiles/opcode-policy.md`를 읽습니다. 요약하면 다음과 같습니다.

- 각 구조를 `kr-812` 기준으로 한 번 쓰고, 필드마다 이름과 클라이언트 오프셋을 적습니다. 차이가 있는 빌드는 그 행에 주석(`except`, `only`, `also`, 또는 자체 대체)을 답니다. 표가 말할 수 있는 것을 코드에 빌드별 필드 표나 `if (build ...)`로 추가하지 않습니다.
- 용도가 증명되지 않은 필드는 `unknown_<offset>`으로 둡니다. 의미를 만들어 내지 않습니다. 모든 `sub_` 주소에는 클라이언트 빌드 접두사가 붙습니다.
- opcode 행에는 `verified`, `inferred`, `unverified`, `observed-session` 증거 등급과 `name_source`를 기록합니다. 알 수 없는 인바운드 opcode는 기록만 하고 낙관적으로 해석하지 않습니다. C2S와 S2C, 게임과 relay 연결은 서로 다른 공간입니다.
- 모든 codec에는 바이트 길이 고정 테스트가 필요하며, wire 변경은 커밋 전에 클라이언트의 소비 경로와 대조합니다.
- `bff packets show <build> <structure>`, `bff packets diff <build> <build>`, `bff packets check`로 표를 확인합니다.

## 문서 변경

`docs/`는 게시 가이드의 원본이고, `docs-site/`는 `docs-site/pages.json`의 명시적 페이지 목록으로 사이트를 빌드합니다. 게시 페이지는 `X.md`(영어, 기준), `X.zh.md`, `X.zh-tw.md`, `X.ko.md` 네 파일로 존재하며, 모든 파일에 `title`, `description`, `updated` 날짜가 있는 front matter가 필요합니다.

- 로컬 링크는 게시된 페이지 원본을 가리켜야 합니다(예: [설정](configuration.md)). 외부 URL과 앵커는 허용됩니다.
- `docs-site/content/` 아래 생성 콘텐츠를 편집하거나 빌드 산출물을 커밋하지 않습니다. 둘 다 무시됩니다.
- 영어와 중국어 안내서를 함께 갱신하고, 사이트 페이지의 네 번역을 모두 최신으로 유지합니다.
- 같은 커밋에서 두 루트 변경 기록(`CHANGELOG.md`, `CHANGELOG.zh.md`)에 날짜가 있는 항목을 추가합니다. 날짜는 `YYYY-MM-DD`이며 시간을 적을 때는 Asia/Shanghai를 사용합니다. 새 항목은 이전 항목 위에 둡니다.

사이트 디렉터리에서 빌드합니다:

```sh
cd docs-site
npm ci
npm test
npm run build
```

동기화 단계가 빌드 전에 실행되어 메타데이터와 링크를 검증합니다. front matter가 불완전하면 `Missing guide metadata: <path>`, 네 파일 중 하나가 없으면 `Missing public guide: <path>`, 로컬 링크가 게시 페이지를 가리키지 않으면 `Unpublished local link: <target>`으로 실패합니다. [설정](configuration.md)과 [플러그인](plugins.md) 같은 다른 게시 가이드를 예로 참고합니다.

## 검증 수준

| 수준 | 실행 내용 | 필요 | 증명하는 것 |
| --- | --- | --- | --- |
| 빠른 확인 | `dotnet build`와 `dotnet test` | .NET | 변경이 컴파일되고 .NET 테스트 모음이 통과 |
| 공개 게이트 | `tools/ci/serial-gates.py --public` | .NET, Python, Node.js/npm, Go, Rust와 `wasm32-wasip1` | 로컬 데이터 케이스를 뺀 모든 추적 테스트 프로젝트, SDK 적합성, packet 표, 격리 설정 |
| 전체 게이트 | 같은 명령에서 `--public` 제거 | 아래 로컬 클라이언트 워크북 | 모든 테스트, 건너뛰기 없음. 릴리스 게이트 |
| 릴리스 검토 | 격리 시작과 영향받는 실제 클라이언트 흐름 | 실제 게임 클라이언트 | 클라이언트 수용. 어떤 자동 검사도 대신하지 못함 |

전체 게이트에는 아래 로컬에서 구한 클라이언트 파일이 필요합니다. 저장소와 함께 배포되지 않습니다.

| 설정 또는 경로 | 필요한 파일 |
| --- | --- |
| `client-data/cn-324/` | `Config/ShopItem2.xml` |
| `client-data/kr-1256/data/` | `Packs/Config_00000.pkn`(상점 및 캐릭터 통합 문서)과 `Packs/ConfigSh_00000.pkn`(펫 오버레이) |
| `artifacts/shop-tables-survey/id400/xml/` | `Config/ShopItem2.xml` |

파일이 없으면 전체 게이트는 누락된 전제를 출력하고 `Local game data is missing for <tests>; provision it or pass --public to run the publicly reproducible gates only.`로 멈춥니다.

검증 결과를 정직하게 보고합니다.

- 시도마다 새 결과 디렉터리를 사용합니다. 채워진 TRX는 실패, 빈 결과, 건너뛴 결과 없이 통과 카운터와 일치해야 합니다. 건너뛴 테스트는 통과가 아닙니다.
- 실제로 실행한 모드를 보고합니다. 공개 게이트는 제외 범위를 `scope.json`에 기록하며 완전한 릴리스 게이트가 아닙니다.
- 실패 결과를 삭제하거나 다시 쓰지 않습니다. 실패 시도의 콘솔 로그와 TRX를 보관합니다.
- 결과가 자동 검사에서 나왔는지 실제 게임 클라이언트에서 나왔는지 밝힙니다.

공개 저장소 워크플로는 GitHub 호스팅 Windows 러너에서 `main` 푸시와 pull request에 공개 재현 게이트를 실행하며 로컬 데이터 사례는 실행하지 않습니다. 로컬 클라이언트 데이터 전제 조건이 있으면 로컬에서 전체 게이트를 실행하고 CI 체크아웃에는 `.env`와 로컬 profile 패치를 두지 않습니다.

## 검토 가능한 변경의 내용

- 한 가지 일만 하는 집중된 diff와 그 동작을 다루는 테스트.
- 검증 증거: 명령, 결과, 수정 전에 있었던 실패 출력.
- 변경이 요구하는 데이터와 문서 갱신: wire 레이아웃의 packet 표, 증거가 있는 opcode 행, 영어와 중국어 안내서 갱신, 변경 기록 항목.
- 무관한 재포맷, 생성 파일, 빌드 산출물, 로그, 로컬 재정의는 없음.

## 거부되는 것

- 자격 증명, 토큰, `.env` 파일, 로컬 profile 패치, 또는 코드·문서·로그에 들어간 비밀.
- 비공개 조사 자료, 캡처, 디컴파일한 클라이언트 내보내기, 클라이언트 자산.
- 타사 코드, 복사한 레이아웃, 다른 프로젝트와 서버를 이름으로 언급하는 참조.
- 추측한 packet 이름이나 필드 의미, 또는 `unverified` 증거로 활성화한 핸들러.
- 무한 큐, 버퍼, 파일 싱크, 그리고 검증된 설정 대신 하드코딩한 배포 값.
- 경고를 억제한 빌드, 또는 건너뛰었는데 통과로 보고한 테스트.
- 생성된 문서 콘텐츠, 빌드 산출물, 또는 병합되지 않은 브랜치의 기능을 사용 가능하다고 설명하는 것.

## 제출

1. 브랜치를 푸시하고 `main` 대상 pull request를 엽니다.
2. 동작 변경, 증거, 실행한 검증 모드를 설명합니다. 버그를 수정하는 변경이면 실패 출력을 첨부합니다.
3. diff에 비밀, 비공개 자료, 생성 콘텐츠가 없는지 확인합니다.
4. 검토 의견에는 새 커밋이나 명확한 설명으로 답하고, 이력을 검토 가능하게 유지합니다.
5. 검토 후 변경이 `main`에 병합됩니다. 워크플로가 그곳에서 실행되므로 다음 순차 검증 실행을 확인합니다.

## 다음

- 프로젝트의 GitHub 저장소가 README에 기록된 진입점입니다: https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy . 저장소는 채팅 서버나 포럼을 기록하지 않습니다.
- [설정](configuration.md)과 [플러그인](plugins.md)은 변경이 보통 건드리는 구조를 설명합니다.
- [SDK 프로토콜](sdk-protocol.md)은 선택적 언어 SDK를, [문제 해결](troubleshooting.md)은 빌드나 게이트 실패 해결을 다룹니다.

---
title: "SDK 프로토콜"
description: "선택형 언어 SDK 플러그인을 Host 프로세스에 연결하거나 .NET으로 네이티브 플러그인을 작성합니다."
updated: 2026-10-18
---

서버의 주 구현은 .NET 10의 C#입니다. `sdk/`의 SDK는 Python, TypeScript, Go, Rust를 위한 선택형 플러그인 클라이언트입니다. 같은 Host 계약을 적용해 서버를 다시 구현하지 않고 다른 언어로 플러그인을 작성할 수 있습니다. 프로세스 플러그인은 표준 입출력의 제한된 줄 단위 JSON 프로토콜로 Host에 연결합니다. Go와 Rust 모듈은 Host 안에서 WebAssembly로 실행할 수도 있습니다.

이 페이지는 먼저 SDK가 무엇이고 무엇이 아닌지, 언어별로 실행 가능한 빠른 시작 하나씩, 플러그인 테스트 방법과 흔한 실패 진단을 설명합니다. 그다음 loader, 활성화, 취소, 메시지, 서비스, 작업, 리소스, 이벤트 계약을 자세히 기록합니다. SDK는 이 저장소에서 직접 사용하며 패키지 레지스트리에서 설치하지 않습니다.

## 이 페이지에서 다루는 내용

- 두 가지 loader: 자식 프로세스를 위한 `loader.process`와 샌드박스 WebAssembly 모듈을 위한 `loader.wasm`.
- SDK 플러그인이 현재 할 수 있는 일: 형식화된 메시지, 선언된 교체와 middleware, 선언된 서비스 호출, 제공자와 데코레이터, 순서형 컬렉션, 공유 이벤트, 소유한 작업과 리소스.
- 각 번들 언어에서 플러그인을 빌드, 실행, 테스트, 마운트하는 방법.
- 프로토콜 버전 협상, 생성된 계약 매니페스트, 일관성 테스트가 동작하는 방식.
- 프로토콜과 플랫폼의 실패 모드.

**대상 독자:** [플러그인](plugins.md)을 읽었고 C# 이외의 언어로 동작을 추가하려는 개발자, 또는 loader와 프로토콜 계약 자체가 필요한 독자.

## 시작하기 전에

- [설치](installation.md)에 따라 검사를 빌드했고 터미널이 저장소 루트에 있습니다.
- 선택한 언어의 SDK 도구 체인: 현재 Python 3 인터프리터(일관성 실행기는 `python`을 호출합니다), Node.js 20 이상과 npm(`sdk/typescript/package.json`이 `node >=20`을 선언), Go 1.22 이상(`sdk/go/go.mod`), 또는 edition 2021을 지원하는 Rust 도구 체인과 cargo(`sdk/rust/Cargo.toml`)입니다. 하나만 있으면 되고 네 가지가 모두 필요하지는 않습니다. WebAssembly 모듈에는 Rust의 `wasm32-wasip1` 또는 Go의 `wasip1` GOOS가 추가로 필요합니다.
- 실행 단계에는 `BFF_MYSQL_PASSWORD`가 비어 있지 않은 임시 값인 `.env` 파일과 로컬 검사 패치 `tools/local-checks/isolated-memory.patch.yaml`이 필요합니다. 자세한 내용은 [빠른 시작](quickstart.md)을 참고하세요.
- [플러그인](plugins.md)의 플러그인 모델: 플러그인 id, 행 id, 선언, effect.

## 빠른 시작

네 가지 빠른 시작은 각 언어로 같은 작은 플러그인을 만듭니다. 활성화될 때 한 줄만 기록하고 메시지를 처리하지 않으므로 관리 전용 `management` profile에서 실행되며 클라이언트 데이터가 필요 없습니다. Python과 Go SDK는 서드파티 런타임 종속성이 없고, TypeScript와 Rust SDK는 각각 `npm ci`와 `cargo`로 빌드 종속성을 가져옵니다.

| SDK | 위치 | 프로토콜 |
| --- | --- | --- |
| Python | `sdk/python` | stdio 기반 JSON-RPC, 프로토콜 v1과 v2 |
| TypeScript | `sdk/typescript` | stdio 기반 JSON-RPC, 프로토콜 v1과 v2 |
| Go | `sdk/go` | stdio 기반 JSON-RPC, 프로토콜 v1과 v2; `loader.wasm`용 `wasip1` 빌드 |
| Rust | `sdk/rust` | stdio 기반 JSON-RPC, 프로토콜 v1과 v2; `loader.wasm`용 `wasm32-wasip1` 빌드 |

### Python

Python SDK는 `sdk/python`의 표준 라이브러리만 사용하는 `bff_plugin` 패키지입니다. 저장소 루트에서 단위 테스트를 실행합니다:

```sh
python -m unittest discover -s sdk/python/tests -p "test_*.py"
```

공유 프로토콜 fixture로 Python 일관성 플러그인을 실행합니다:

```sh
python sdk/python/tests/run_conformance.py
```

`sdk/python/examples/hello.py`를 만듭니다:

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, plugin, run


@plugin("example.hello", version="1.0.0", description="Logs one message.")
class Hello:
    async def activate(self, ctx: Context) -> None:
        ctx.logger.info("hello from the Python SDK")


if __name__ == "__main__":
    run()
```

loader 항목은 `bff_plugin`을 import할 수 있도록 `sdk/python/examples`에서 모듈을 시작합니다. `@plugin` 선언의 `config` 스키마는 선택 사항이며 이 플러그인은 선언하지 않습니다. 이 패치를 `artifacts/sdk-hello.patch.yaml`로 저장하세요:

```yaml
- insert:
    - id: hello.python
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: python
            args: ["-m", "hello"]
            cwd: sdk/python/examples
            env:
              PYTHONUNBUFFERED: "1"
            source_id: hello-python
    - id: hello.binding
      plugin: example.hello
```

### TypeScript

TypeScript SDK는 `sdk/typescript`의 로컬 npm 패키지입니다. 해당 디렉터리에서 빌드 종속성을 설치하고 빌드한 뒤 자체 테스트 스크립트를 실행합니다:

```sh
cd sdk/typescript
npm ci
npm test
```

`npm test`는 패키지를 빌드하고 회선 형식, 수명, 일관성 테스트 모음을 실행합니다. `sdk/typescript/examples/hello.ts`를 만듭니다:

```ts
import { Context, plugin, run } from "../src/index.js";

@plugin("example.hello", { version: "1.0.0", description: "Logs one message." })
class Hello {
  async activate(ctx: Context): Promise<void> {
    ctx.logger.info("hello from the TypeScript SDK");
  }
}

run();
```

`npm run build`가 이를 `dist/examples/hello.js`로 컴파일합니다. 이 패치를 `artifacts/sdk-hello.patch.yaml`로 저장해 Python 패치를 교체하세요:

```yaml
- insert:
    - id: hello.typescript
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: node
            args: ["dist/examples/hello.js"]
            cwd: sdk/typescript
            source_id: hello-typescript
    - id: hello.binding
      plugin: example.hello
```

### Go

Go SDK는 `sdk/go`의 `bffplugin` 패키지입니다. 해당 디렉터리에서 테스트를 실행합니다:

```sh
cd sdk/go
go test ./...
```

`sdk/go/examples/hello/main.go`를 만듭니다:

```go
package main

import (
	"bffplugin/bffplugin"
	"os"
)

type hello struct{}

func (*hello) Activate(ctx *bffplugin.Context) error {
	ctx.Logger.Info("hello from the Go SDK", nil)
	return nil
}

func main() {
	bffplugin.Register(bffplugin.Definition{ID: "example.hello", Version: "1.0.0"}, func() bffplugin.Plugin { return &hello{} })
	os.Exit(bffplugin.Run())
}
```

바이너리를 `sdk/go`에 빌드하고 이 패치를 `artifacts/sdk-hello.patch.yaml`로 저장해 TypeScript 패치를 교체하세요:

```sh
cd sdk/go
go build -o hello ./examples/hello
```

```yaml
- insert:
    - id: hello.go
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/go/hello
            cwd: .
            source_id: hello-go
    - id: hello.binding
      plugin: example.hello
```

Windows에서는 출력 파일이 `hello.exe`입니다. `go build -o hello.exe ./examples/hello`를 사용하고 `command`를 `sdk/go/hello.exe`로 설정하세요.

### Rust

Rust SDK는 `sdk/rust`의 `bff-plugin` crate입니다. 해당 디렉터리에서 테스트를 실행합니다:

```sh
cd sdk/rust
cargo test --release
```

`sdk/rust/examples/hello.rs`를 만듭니다:

```rust
use bff_plugin::{Context, Definition, Error, Plugin, Server};

struct Hello;

impl Plugin for Hello {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        ctx.info("hello from the Rust SDK");
        Ok(())
    }
}

fn main() {
    std::process::exit(
        Server::new()
            .register(
                Definition {
                    id: "example.hello".into(),
                    version: "1.0.0".into(),
                    ..Default::default()
                },
                || Hello,
            )
            .run(),
    );
}
```

빌드한 뒤 이 패치를 `artifacts/sdk-hello.patch.yaml`로 저장해 Go 패치를 교체하세요:

```sh
cd sdk/rust
cargo build --release --example hello
```

```yaml
- insert:
    - id: hello.rust
      plugin: loader.process
      lifecycle:
        definition_source_reserve: 1
        definition_reserve: 16
      config:
        plugins:
          - command: sdk/rust/target/release/examples/hello
            cwd: .
            source_id: hello-rust
    - id: hello.binding
      plugin: example.hello
```

Windows에서는 같은 디렉터리의 `hello.exe`가 바이너리입니다.

### 플러그인 마운트

위의 각 언어 절은 같은 종류의 패치를 저장합니다. `insert` 작업으로 `loader.process` 행 하나와 `example.hello`를 바인딩하는 일반 행 하나를 추가합니다. 선택한 언어의 패치를 두 번째 `--patch`로 적용하세요. 실제 데이터베이스를 건드리지 않도록 격리 메모리 패치가 먼저 와야 합니다:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/sdk-hello.patch.yaml
```

loader 행의 두 시작 부여량은 필수입니다. `definition_source_reserve: 1`은 프로세스 항목에 정의 출처 하나를 예약합니다. 이것이 없으면 시작 시 바인딩 행을 `references unknown plugin 'example.hello'`로 거부합니다. `definition_reserve: 16`은 그 출처가 게시할 정의를 위한 공간을 예약합니다. 부트스트랩 loader 행도 같은 방식으로 용량을 예약합니다. 행 id는 임의로 정할 수 있지만 `source_id`는 정의 출처의 ID이며 하나의 loader 활성화 안에서 유일해야 합니다. 이름이 없는 프로세스 항목은 `process-<index>`가 기본값입니다. 상대 `command`와 `args` 경로, `cwd`는 `base_dir` 기준으로 해석되며, `base_dir`는 기본적으로 Host의 작업 디렉터리(위 명령에서는 저장소 루트)입니다. 디렉터리가 없는 명령 이름은 `PATH`에서 찾습니다.

loader 행이 활성 상태여야 정의가 게시됩니다. 그 전까지 `example.hello`를 바인딩한 행은 `Pending`으로 남습니다. 패키지 profile에 플러그인을 영구적으로 마운트하려면 같은 행을 해당 profile의 패치 파일에 복사하고 명령줄에서 `--patch`를 전달하지 않으면 됩니다.

시작 로그에는 loader 행 활성화, SDK 출처의 자기 보고, 바인딩 행 활성화가 차례로 나타납니다:

```text
Row 'hello.python' activated (plugin 'loader.process' version 1.0.0+<commit>; contracts ...; builds independent).
SDK plugin source 'hello-python' runs SDK {"name":"...","version":"..."}.
Row 'hello.binding' activated (plugin 'example.hello' version 1.0.0; contracts ...; builds independent).
hello from the Python SDK
```

**확인 방법:** loader 행과 바인딩 행이 모두 나타나고, 플러그인 메시지가 기록되며, `GET /api/plugins`가 `hello.binding`을 `Active` 상태로, loader 행도 `Active`로 보여 줍니다. 바인딩 행이 `Failed`이거나 계속 `Pending`이면 `error` 필드에 SDK 또는 설정 오류가 담깁니다. Ctrl+C로 중지하세요. loader는 자신이 서비스하는 모든 행이 비워진 뒤에야 자식 프로세스를 중지합니다.

이 빠른 시작은 상대 `command` 경로와 `cwd`를 사용하므로 저장소 루트에서 실행됩니다. WebAssembly 플러그인은 대신 `loader.wasm` 행과 `module` 항목을 사용합니다. 모듈은 `_start`를 내보내는 WASI 명령이고 `bff.recv`, `bff.send`, `bff.log`를 import하며, 인수, 환경 변수, 파일, stdio를 받지 않습니다. 선형 메모리는 `max_memory_bytes`(기본 64 MiB)로 제한됩니다. Go 모듈은 `GOOS=wasip1 GOARCH=wasm`으로, Rust 모듈은 `wasm32-wasip1` 대상으로 빌드합니다.

메시지 처리기를 등록하는 플러그인은 해당 프로토콜과 메시지 형식의 인바운드 바인딩을 제공하는 게임 profile도 필요합니다. 번들 echo 예제는 `handles: ["LoginRequested"]`와 `supported_builds: ["kr:812"]`를 선언하므로 `management`가 아니라 클라이언트 데이터가 있는 `kr-812` profile에 마운트해야 합니다.

## 기능 협상과 호환성

Host는 `initialize` 요청 하나로 대화를 시작하고 SDK는 선택한 프로토콜 버전으로 응답합니다. 매개변수에는 플러그인이 무엇을 선언할 수 있는지 판단하는 데 필요한 모든 정보가 담깁니다:

| initialize 필드 | 의미 |
| --- | --- |
| `protocol_version`, `protocol_versions` | 기존 단일 버전 필드와 전체 제공 목록. Host는 `1`과 `2`를 제공 |
| `features` | 이 빌드가 구현한 Host 알림과 콜백. `lifecycle.cancel` 포함 |
| `max_in_flight` | 한 방향의 미완료 요청 상한 |
| `max_reentry_depth` | 단일 스레드 모듈의 중첩 Host 요청 프레임 상한 |
| `host_version`, `contracts` | Host 릴리스와 Host가 제공하는 공유 계약 어셈블리 버전 |

프로토콜 v1은 원래 표면을 다룹니다: `initialize`, `describe`, `activate`, `deactivate`, `deliver`, `messages.on`, `messages.override`, `messages.send`, `effect.dispose`, `log`. 프로토콜 v2는 서비스 호출과 제공자, 순서형 컬렉션, 이벤트, 작업, 리소스, 메시지 middleware, 그리고 `lifecycle.ready`와 함께 오는 조기 `lifecycle.cancel` 알림을 추가합니다. 협상된 버전을 벗어난 선언은 무시되지 않고 실패합니다. Host는 `provides, provides_many and decorates require protocol v2.`, `requires_many and optional_many require protocol v2.`, `event declarations require protocol v2.`를 보고하거나 SDK가 알 수 없는 메서드를 거부합니다.

Host 기능이 없다는 것 자체는 오류가 아닙니다. 해당 기능에 의존하기 전에 문서화된 기능 플래그를 확인하세요. 예: Python의 `ctx.supports_lifetime_cancellation`, TypeScript의 `ctx.supportsLifetimeCancellation`, Go의 `ctx.SupportsLifetimeCancellation()`, Rust의 `ctx.supports_lifetime_cancellation()`. 이전 Host도 로컬 롤백, 비활성화, 전송 종료 경로로 계속 동작하지만 비활성화 전에 취소를 전달할 수는 없습니다.

`describe` 응답은 공유 계약과 검증됩니다. 선언의 이름은 공유 계약 어셈블리의 단일 메시지 레코드, 공유 추상 또는 계약의 단일 서비스 인터페이스로 해석되어야 하고, family는 `legacy` 또는 `modern`이어야 합니다. 알 수 없는 이름은 허용된 계약을 나열하는 오류와 함께 전체 출처를 실패시킵니다. 따라서 오타는 메시지가 도착할 때가 아니라 행이 활성화될 때 드러납니다.

### 계약 매니페스트 확인

현재 검사에 대한 매니페스트를 생성합니다:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

`--out`이 없으면 매니페스트는 표준 출력으로 기록됩니다. `contracts` 명령의 로그는 표준 오류로 가므로 문서가 깨끗하게 유지됩니다. 매니페스트는 SDK 플러그인이 선언하고 호출할 수 있는 내용을 설명합니다:

| 매니페스트 필드 | 내용 |
| --- | --- |
| `schema_version` | 매니페스트 형식 버전 |
| `runtime_protocol_versions`, `runtime_features` | Host가 광고하는 프로토콜 버전과 기능 목록 |
| `messages`, `events`, `event_result_types` | 형식화된 메시지 레코드, 이벤트 페이로드, 본문 형태를 포함한 이벤트 결과 형식 |
| `services` | 서비스 인터페이스와 그 연산, 매개변수, 결과 형태 |
| `types` | 참조되는 레코드와 열거형 형태 |

모든 서비스 연산에는 `rpc_shape`와 `provider_rpc_shape`가 있습니다. SDK 플러그인이 해당 연산을 호출할 수 있으면 `rpc_shape`가 `supported`이고, SDK 플러그인이 구현할 수 있으면 `provider_rpc_shape`가 `supported`입니다. 지원되지 않는 항목에는 `unsupported_reason` 또는 `provider_unsupported_reason`이 붙습니다. 추측 대신 매니페스트를 사용하세요. 제공자는 인터페이스 전체를 구현해야 하며, 모호하지 않으면 메서드 이름을, 오버로드는 전체 schema method id를 사용하고, 속성 접근자는 `get_ReplyAfterInventory` 같은 이름을 씁니다.

## 플러그인 테스트

저장소는 하나의 공유 일관성 실행기와 모든 SDK가 함께 쓰는 하나의 fixture 형식을 제공합니다. 저장소 루트에서 실행합니다:

```sh
python sdk/run-conformance.py
```

실행기는 도구 체인이 있는 각 SDK를 빌드하고 단위 테스트한 뒤, 공유 fixture(`tests/fixtures/sdk-protocol`)와 protocol-v2 fixture를 해당 SDK의 플러그인에 대해 실행합니다. 모음마다 `PASS`, `FAIL`, `SKIP`과 한 줄 요약을 출력하고, 하나라도 실패하면 0이 아닌 코드로 종료합니다. 도구 체인이 없어 건너뛴 언어는 통과가 아닙니다. 이것이 현재 프로토콜의 호환성 기준입니다.

fixture는 JSON-lines 파일입니다. 첫 줄은 비교 전에 양쪽에서 제거할 점 경로 `ignore` 목록이 있는 헤더입니다. 이후 각 줄은 호스트에서 플러그인으로 가는 `{"dir": "h2p", ...}`, 플러그인의 예상 응답 `{"dir": "p2h", ...}`, 또는 `{"expect_exit": N}`입니다. 실행기는 fixture마다 새 플러그인 프로세스를 시작하고, 각 호스트 메시지를 표준 입력에 쓰고, ignore 목록을 적용한 뒤 플러그인의 각 줄을 예상 응답과 정확히 비교하며, 깨끗한 종료를 요구합니다. ID와 핸들은 정확히 비교됩니다. 공유 Python 실행기로 직접 만든 플러그인을 같은 fixture에 실행할 수 있습니다:

```sh
python sdk/python/tests/run_conformance.py --fixtures <fixture-dir> --command <plugin-executable> [args...]
```

**확인 방법:** 실행기가 `<passed>/<total> fixtures passed` 줄로 끝나고 종료 코드 0을 반환합니다. 불일치가 있으면 첫 실패 줄의 예상 JSON과 실제 JSON, 그리고 플러그인의 표준 오류를 출력합니다.

## 흔한 실패

| 증상 | 확인 | 가능한 원인과 해결 |
| --- | --- | --- |
| 출처가 `answered initialize without a supported protocol_version (1 or 2).`로 실패 | 런타임의 `protocol_versions` 응답 | 직접 작성한 런타임은 제공 목록에서 `1` 또는 `2`를 선택해야 합니다. 번들 SDK는 항상 지원되는 버전으로 응답합니다. |
| `stdout carried a line that is not JSON ...; stdout is reserved for the protocol` | 프로세스가 표준 출력에 쓰는 모든 내용 | 표준 출력은 프로토콜 채널입니다. Python은 `print`를, TypeScript는 `console.*`를 표준 오류로 리디렉션합니다. Go와 Rust는 진단을 표준 오류나 SDK logger로 보내고 표준 출력에는 절대 쓰지 마세요. |
| 시작이 `SDK plugin process '<command>' could not start in '<cwd>'`로 실패 | `command`, `args`, `cwd`, `PATH` | 실행 파일이나 작업 디렉터리가 잘못되었습니다. 디렉터리 구분자가 있는 경로는 `base_dir` 기준으로 해석되고, 맨 이름은 `PATH`가 필요합니다. |
| 행이 `requires_many and optional_many require protocol v2.` 또는 비슷한 v2 메시지로 실패 | 협상된 프로토콜 버전과 선언 필드 | 선언에 프로토콜 v2가 필요합니다. v2를 협상하거나 선언을 제거하세요. 오류가 필드 계열을 알려 줍니다. |
| 메시지 처리기가 `protocol '<p>' has no inbound binding for '<type>'`로 실패 | profile의 dialect 행 | 해당 프로토콜과 메시지 형식의 형식화된 dialect가 같은 profile에서 활성 상태여야 합니다. SDK는 패킷을 직접 디코딩할 수 없습니다. |
| 콜백 시간 초과: `did not answer '<method>' within <n> ms (request_timeout_ms).` | 플러그인이 블록되는지, 변경이 이미 커밋되었을 수 있는지 | 시간 초과는 활성화를 실패시키고 요청 결과는 알 수 없습니다. 재시도 전에 읽기 작업으로 상태를 확인하고, 변경을 무작정 재시도하지 마세요. |
| `.wasm` 모듈이 `exports no '_start'; build it as a WASI command`로 실패 | 빌드 대상과 내보내기 | `wasm32-wasip1`(Rust) 또는 `GOOS=wasip1 GOARCH=wasm`(Go)으로 빌드하세요. 모듈은 WASI 명령이어야 합니다. |
| 행이 `Invalid configuration for row '<row>': unknown key '<key>'`로 실패 | 행의 `config`와 선언된 스키마 | Host가 기본값을 적용하고 형식을 검증하며, 선언되지 않은 키는 거부됩니다. 선언이나 행을 갱신하세요. |

## loader 선택

플러그인이 실행 파일이나 인터프리터 진입점이면 `loader.process`를 사용합니다. Host가 프로세스를 시작하고 `initialize`로 프로토콜 버전 1 또는 2를 협상하며 프로세스 수명을 관리합니다. SDK WebAssembly 모듈에는 `loader.wasm`을 사용합니다. 같은 JSON 프로토콜을 `bff.recv`, `bff.send`, `bff.log` import로 교환합니다. loader source를 설정하고 발견된 플러그인 정의를 별도 profile 행으로 마운트하세요.

두 loader 모두 정의를 소유된 정의 출처로 게시하므로 SDK 플러그인을 바인딩한 행은 네이티브 행과 똑같이 선언된 서비스 순서로 활성화됩니다. 프로세스 항목 하나가 `max_definitions` 예약 범위 안에서 여러 플러그인을 설명할 수 있습니다. loader는 자신이 서비스하는 모든 행이 비워진 뒤에야 프로세스를 중지하고, SDK 행의 다시 로드는 프로세스를 재시작하지 않고 같은 프로세스에 새 활성화를 요청합니다.

## 활성화와 소유권

Host는 `activate`에 불투명한 activation id, 행 id, 플러그인 id, 검증된 설정, reload 플래그를 보냅니다. contract 선언은 앞선 `describe` 단계에서 읽습니다. 활성화 콜백이 반환되기 전부터 활성화에 접근할 수 있으므로 콜백 안에서 메시지 처리기를 등록하고 Host 서비스를 호출할 수 있습니다. 요청 대기는 Host의 시간 제한을 따릅니다. 구독 등 소유한 자원은 비활성화 시 해제해야 합니다. 정리가 끝나지 않으면 오류로 보고되며 native 코드는 취소에 협력해야 합니다.

## 활성화 수명 취소

각 활성화에는 읽기 전용 수명 신호가 있습니다. 행의 종료가 시작되면 이미 수락한 콜백이 모두 끝나기 전에 신호가 취소됩니다. 활성화 실패 롤백, 비활성화 훅 실행, 전송 연결 종료 시에도 먼저 취소됩니다. 한 행을 중지해도 같은 프로세스의 다른 행은 취소되지 않으며, 다시 로드하면 새 신호를 만듭니다.

| SDK | 취소 확인 | 조기 알림 지원 확인 |
| --- | --- | --- |
| Python | `await ctx.lifetime.wait()` / `ctx.lifetime.is_set()` | `ctx.supports_lifetime_cancellation` |
| TypeScript | `ctx.lifetime` (`AbortSignal`) | `ctx.supportsLifetimeCancellation` |
| Go | `ctx.Lifetime.Done()` / `ctx.Lifetime.Err()` | `ctx.SupportsLifetimeCancellation()` |
| Rust | `ctx.lifetime().is_cancelled()` | `ctx.supports_lifetime_cancellation()` |

Host는 프로토콜 v2의 `initialize.features` 목록에 `lifecycle.cancel`을 선언합니다. SDK는 활성화 상태를 등록한 다음 사용자 활성화 훅에 진입하기 전에 `{ "activation": "opaque-id" }`로 `lifecycle.ready`를 호출합니다. Host는 네이티브 작업 관리자와 공유 effect 용량으로 취소 관찰 작업 하나를 소유하며, 중지 시 같은 활성화 ID의 `lifecycle.cancel` 알림을 보냅니다. 준비 및 취소의 반복은 중복 등록을 만들지 않습니다. 알 수 없거나 제거된 활성화의 알림은 저장하지 않고 무시합니다.

이전 Host와도 호환되지만 조기 알림은 제공되지 않습니다. 비활성화 전에 취소가 도착해야 하는 작업은 위 지원 여부를 확인하세요. 로컬 롤백, 비활성화 및 전송 종료는 계속 신호를 취소합니다. Go/Rust WebAssembly는 Host 통신을 읽는 동안 알림을 처리합니다. 단일 실행 스레드에서 로컬 신호를 기다리며 차단하면 알림을 처리할 수 없습니다. 독립적으로 실행되는 작업에는 협력적 Host 호출이나 프로세스 로더를 사용하세요.

취소는 중지 요청이며 작업 완료를 의미하지 않습니다. 등록 핸들의 소유권을 유지하고 실제 정리를 기다리세요.

## 타입 메시지 처리기와 명시적 교체

입력 메시지 타입을 `handles`에 선언한 뒤 `ctx.messages.on`으로 처리기를 등록합니다. 특정 기존 처리기를 교체하려면 `overrides`에 정확한 `rowId:fully.qualified.MessageType` 대상을 선언하고 SDK의 `messages.override`를 호출해야 합니다. Host는 선언, 프로토콜 바인딩, 현재 선택된 대상을 검증한 뒤에만 교체를 게시합니다. 반환된 핸들을 해제하면 이전 처리기가 복원되며 활성화의 역순 effect 정리를 따릅니다. 대상이 없거나 교체가 설치되지 않으면 조용히 순서를 바꾸지 않고 활성화를 실패시킵니다.

| SDK | 기본 등록 | 명시적 교체 |
| --- | --- | --- |
| Python | `await ctx.messages.on(protocol, type, handler)` | `await ctx.messages.override(protocol, type, target_row_id, handler)` |
| TypeScript | `ctx.messages.on(protocol, type, handler)` | `ctx.messages.override(protocol, type, targetRowId, handler)` |
| Go | `ctx.Messages.On(protocol, type, handler)` | `ctx.Messages.Override(protocol, type, targetRowID, handler)` |
| Rust | `ctx.on(protocol, type, handler)` | `ctx.override_handler(protocol, type, targetRowId, handler)` |

콜백은 기본 처리기와 같은 세션과 디코딩된 메시지 본문을 받습니다. 회선 프로토콜은 여전히 Host의 형식화된 dialect 계층이 소유하며, SDK 처리기는 패킷 opcode를 직접 읽거나 쓸 수 없습니다.

## 메시지 middleware와 단일 사용 continuation

프로토콜 v2 SDK 플러그인은 네이티브 메시지 middleware와 같은 순서 및 수명 규칙으로 형식화된 메시지 파이프라인을 감쌀 수 있습니다. `middleware`에 메시지 짧은 이름을 나열한 뒤 `messages.use`를 호출합니다. 우선순위가 높은 middleware가 바깥쪽이고, 우선순위가 같으면 등록 순서를 유지합니다. 콜백은 세션, 디코딩된 본문, 단일 사용 `next` continuation, 호출 취소 신호를 받습니다. `next`를 호출하지 않고 반환하면 하위 처리기를 단락시킵니다. 메시지가 middleware 파이프라인에 들어가기 전에 하위 형식화 처리기가 선택되어 있어야 합니다.

| SDK | 등록 |
| --- | --- |
| Python | `await ctx.messages.use(protocol, type, middleware, priority=0)` |
| TypeScript | `ctx.messages.use(protocol, type, middleware, priority)` |
| Go | `ctx.Messages.Use(protocol, type, priority, middleware)` |
| Rust | `ctx.use_middleware(protocol, type, priority, middleware)` |

Host는 `messages.use`가 반환한 등록을 소유합니다. 활성화 effect 범위에 넘기세요. 각 디스패치는 하나의 제한된 `messages.invoke` 콜백으로 수락됩니다. SDK가 `next`를 호출하면 호출 id와 함께 `messages.next`를 보내고, Host는 하위 파이프라인이 끝날 때까지 네이티브 continuation을 유지합니다. `next`를 두 번 호출하거나, 콜백이 반환된 뒤 호출하거나, 다른 활성화의 호출 id를 사용하면 invalid-params 오류로 실패합니다. continuation은 단일 사용이며 자신의 호출을 벗어날 수 없습니다. 호출이나 활성화가 중지되면 Host는 `messages.cancel`을 보냅니다. 취소는 협력을 요청하며 이미 수락된 하위 작업을 버리지 않습니다.

middleware 등록과 continuation 호출에는 프로토콜 v2가 필요합니다. 따라서 네이티브 라우터, 형식화된 dialect, SDK loader가 하나의 메시지 파이프라인을 공유합니다. 패킷 프레이밍과 필드 의미는 Host에 남고, 정책과 게임 로직은 지원되는 어떤 플러그인 언어로도 구현할 수 있습니다.

## 네이티브 소유 작업

프로토콜 v2 SDK는 활성화의 네이티브 작업 소유자를 통해 백그라운드 작업을 시작할 수 있습니다:

```python
task = await ctx.tasks.run("refresh-cache", work)
ctx.effect(task)
```

`ctx.tasks.run`은 네이티브 `RunTaskAsync`와 같은 제한된 활성화 용량을 예약합니다. Host는 등록된 콜백을 실행하기 위해 `tasks.invoke`를 보내고, 작업이 중지될 때 `tasks.cancel`을 보냅니다. `TaskHandle.wait`은 콜백 결과를 관찰하며 소유권을 해제하지 않습니다. `TaskHandle.dispose`는 취소를 요청하고 콜백과 네이티브 정리를 기다린 뒤 작업 소유자를 해제합니다. 콜백 자체에서 해제를 요청하면 Host는 자기 자신을 기다리는 교착을 피하기 위해 상위 범위 해제를 지연합니다. 정상 완료, 취소, 오류는 서로 다른 결과로 유지되며, 오류는 정리가 보고될 때까지 소유권을 유지합니다.

동등한 진입점은 Python과 TypeScript의 `ctx.tasks.run`/`wait`/`dispose`, Go의 `ctx.Tasks.Run`/`Wait`/`Dispose`, Rust의 `Context::run_task`/`TaskHandle::wait`/`dispose`입니다. 모든 핸들을 effect로 등록하세요. Go와 Rust WebAssembly는 계속 협력적입니다. 모듈이 Host 통신으로 돌아와야 취소를 받을 수 있습니다.

## 네이티브 소유 리소스 범위

프로토콜 v2 SDK는 제한된 네이티브 리소스 범위를 만들고 해제 콜백이 SDK 프로세스에 남는 리소스를 얻을 수 있습니다:

```python
scope = await ctx.resources.scope("cache")
ctx.effect(scope)
resource = await scope.acquire("cache-entry", release)
ctx.effect(resource)
```

`create`는 하위 범위를 만들고 `acquire`는 네이티브 effect 슬롯 하나를 예약합니다. 범위와 리소스 핸들은 활성화가 소유하며 effect로 등록해야 합니다. 비활성화 중 네이티브 상위 범위가 해제되고 있으면 Host가 `{ "deferred": true }`를 반환할 수 있으며, SDK는 Host가 호출해 완료할 때까지 콜백을 등록된 상태로 유지합니다. 해제 콜백은 취소 신호를 받고, Host는 콜백이 반환될 때까지 네이티브 소유권을 유지합니다. 콜백은 같은 리소스를 동기적으로 해제하면 안 됩니다. Python, TypeScript, Go, Rust는 같은 범위/획득/해제 형태를 제공하며, Go와 Rust WebAssembly 콜백은 취소를 받으려면 Host 루프로 돌아와야 합니다.

## 서비스 호출

현재 프로토콜은 형식화된 메시지, 로깅, effects, 그리고 명시적으로 나열된 Host 서비스 호출을 제공합니다. 계약은 플러그인 선언으로 부여됩니다. 현재 검사에 대한 목록을 생성합니다:

```sh
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- contracts schema --out artifacts/contracts.json
```

`rpc_shape`로 호출 가능한 연산을, `provider_rpc_shape`로 제공자 연산을 확인합니다. 제공자는 모든 메서드를 구현해야 하며, 모호하지 않으면 메서드 이름을, 오버로드는 전체 schema method id를 사용합니다. 속성 접근자는 `get_ReplyAfterInventory` 같은 이름을 씁니다. 지원되지 않는 형태는 명시적으로 실패합니다. 델리게이트, 참조 매개변수, 범용 리소스 어댑터, 패킷 코덱, `IPluginContext`를 받는 제공자 메서드는 이 서비스 브리지가 다루지 않습니다.

## 서비스 제공과 데코레이션

다음 등록에는 프로토콜 v2가 필요하며 네이티브 종속성 그래프에 참여합니다:

| 선언 | 등록 모드 | 동작 |
| --- | --- | --- |
| `provides` | `singleton` | 단일 구현, 데코레이터 계층 없음 |
| `provides` | `decoratable` | 선언된 데코레이터 계층을 허용하는 단일 구현 |
| `provides_many` | `many` | 순서형 구현. 우선순위가 높은 것이 먼저, 동률이면 등록 순서 |
| `decorates` | `decorate` | 선택된 내부 서비스를 감싸는 계층. 기본 서비스가 데코레이션을 허용해야 함 |

예를 들어 Python 행이 네이티브 정책을 데코레이션할 수 있습니다. 데코레이션을 허용하는 기본 제공자와 함께 마운트합니다:

```python
from bff_plugin import Context, ServiceCall, plugin, run

@plugin("example.inventory-policy", version="1.0.0",
        decorates=["IEquipmentReplyPolicy"])
class InventoryPolicy:
    async def activate(self, ctx: Context) -> None:
        async def reply_policy(call: ServiceCall):
            return await call.inner()

        ctx.effect(await ctx.services.decorate("IEquipmentReplyPolicy", {
            "get_ReplyAfterInventory": reply_policy,
        }, priority=10))

if __name__ == "__main__":
    run()
```

예제는 내부 결과를 유지합니다. 정책은 대신 자신의 유효한 결과를 반환할 수 있습니다. 내부 서비스는 해당 호출에 속하며 선택된 데코레이터 체인을 유지하므로 이후 호출을 위해 저장하면 안 됩니다. Host는 외부 또는 완료된 호출 토큰을 거부합니다. 등록을 제거해도 소비자가 이미 보유한 프로젝션이 무효화되지는 않습니다. 콜백 대상은 제공자 활성화가 끝날 때까지 소유됩니다.

| SDK | 등록 / 데코레이션 / 전체 선택 |
| --- | --- |
| Python | `ctx.services.provide` / `decorate` / `get_all` |
| TypeScript | `ctx.services.provide` / `decorate` / `getAll` |
| Go | `ctx.Services.Provide` / `Decorate` / `GetAll` |
| Rust | `ctx.provide_service` / `decorate_service` / `get_all_services` |

## 순서형 컬렉션과 취소

`requires_many`를 선언하면 구현이 하나 이상 필요하고, `optional_many`는 빈 컬렉션을 허용합니다. TypeScript는 이 옵션을 `requiresMany`, `optionalMany`로, Go는 `RequiresMany`, `OptionalMany`로 표기합니다. 컬렉션은 개수를 제공하고 0부터 시작하는 인덱스로 멤버를 호출합니다. 선택된 ID와 순서는 해제 전까지 안정적으로 유지됩니다. 컬렉션 핸들은 한 활성화에 속하며 제한된 핸들 예산을 공유합니다. 해제를 effect로 등록하세요. Rust 컬렉션은 `effect_fn`을 사용합니다.

각 제공자 콜백은 협력적 취소를 받습니다. Python은 `call.cancellation`, TypeScript는 `call.signal`, Go는 `call.Context`, Rust는 `call.is_cancelled()`를 제공합니다. 취소 후에는 신속히 반환하세요. WebAssembly는 Host 호출을 처리하는 동안 알림을 처리하므로 바쁜 연산이 배경 취소 스레드를 가정하면 안 됩니다. 기한 초과는 활성화를 실패시키지만 수락된 작업은 실제 응답, 네이티브 완료, 전송 종료까지 소유됩니다. 반환된 콜백도 이미 수락된 내부 호출이 끝날 때까지 기다립니다.

## 공유 이벤트

프로토콜 v2는 SDK 이벤트를 활성화의 네이티브 이벤트 버스로 라우팅합니다. 네이티브와 SDK 리스너는 같은 우선순위 순서와 디스패치 스냅샷에 참여합니다. `subscribes`와 `publishes`를 따로 선언하고, 이벤트 `type`, 명시적 `mode`, 결과를 내는 모드에는 `result_type`을 지정합니다. 생성된 목록의 `events`에서 이벤트 ID를, `event_result_types`에서 결과 ID를 읽으세요. 짧은 이름이 모호하면 정규화된 이름을 사용합니다. 네이티브 연결, 델리게이트 또는 지원되지 않는 값을 포함하는 형식은 전용 어댑터가 필요하며 선언 시 거부됩니다.

| 게시 모드 | 리스너와 동작 |
| --- | --- |
| `emit` | 동기 리스너만. 리스너 오류는 기록하고 디스패치는 계속 |
| `serial` | 동기 및 비동기 리스너를 순서대로 대기 |
| `parallel` | 동기 및 비동기 리스너. 비동기 작업을 함께 대기 |
| `waterfall` | 인터셉터가 터미널 콜백을 감싸고 대체 결과를 반환 가능 |
| `bail` | 첫 번째 non-null 참조 결과가 승리. null은 계속, 처리기 오류는 기록 |

구독 모드는 `sync`, `async`, `waterfall`, `bail`입니다. 우선순위가 높은 것이 먼저 실행되고 동률이면 등록 순서를 유지합니다. `serial`과 `parallel`에서 동기 리스너 오류는 네이티브 로그 후 계속 규칙을 따르고, 비동기 실패는 전파되지만 해당 리스너 제거로 인한 취소는 예외입니다. 이벤트의 네이티브 호출자가 실제로 사용하는 게시 모드를 선택하세요. 알림 리스너는 값을 반환하지 않고, waterfall은 선언된 non-null 결과를 반환하며, bail만 null로 계속을 허용합니다.

다음 Python 정책은 기존 장비 결정 파이프라인을 결과 변경 없이 관찰합니다. 장비 선택 이벤트를 게시하는 profile에 마운트합니다:

```python
from bff_plugin import Context, EventSpec, plugin, run

selection = EventSpec("EquipmentSelectionApplying", "waterfall",
                      "EquipmentSelectionDecision")

@plugin("example.selection-policy", version="1.0.0", subscribes=[selection])
class SelectionPolicy:
    async def activate(self, ctx: Context) -> None:
        async def inspect(call):
            decision = await call.next()
            ctx.logger.info(f"Selection allowed: {decision['allowed']}")
            return decision

        ctx.effect(await ctx.events.on(selection, inspect, priority=10))

if __name__ == "__main__":
    run()
```

`call.next()`는 이미 선택된 하위 파이프라인을 호출합니다. 콜백이 살아 있는 동안 여러 번 호출할 수 있으며 네이티브 동작과 일치합니다. 호출할 때마다 하위 작업이 반복될 수 있습니다. 자신의 유효한 결정을 반환하면 단락됩니다. 이벤트 데이터는 값으로 경계를 넘습니다. 로컬 본문을 변경해도 게시자의 개체는 변경되지 않습니다. 정책 변경은 공유 개체 참조가 아니라 이벤트/결과 계약으로 표현합니다.

| SDK | 구독 / 게시 | 결과 선언 |
| --- | --- | --- |
| Python | `ctx.events.on` / `ctx.events.publish` | `EventSpec(type, mode, result_type)` |
| TypeScript | `ctx.events.on` / `ctx.events.publish` | `{ type, mode, resultType }` |
| Go | `ctx.Events.On` / `ctx.Events.Publish` | `EventSpec{Type, Mode, ResultType}` |
| Rust | `ctx.on_event` / `ctx.publish_event` | `EventSpec::new(type, mode).with_result(result_type)` |

Waterfall 게시는 선언된 결과를 반환하는 터미널 콜백을 요구하고, 다른 모드는 터미널을 거부합니다. 모든 구독 핸들을 effect로 등록하세요. 리스너 안에서는 `call.dispose(handle)`(Go: `call.Dispose(handle)`)로 자신의 등록을 제거합니다. 이렇게 하면 네이티브 콜백 컨텍스트가 복원되고 자신을 기다리지 않습니다. 외부 제거는 여전히 수락된 작업을 기다립니다. continuation과 자기 제거 토큰은 하나의 살아 있는 호출에 속하며, 외부 또는 완료된 토큰은 실패합니다.

이벤트 콜백은 서비스 콜백과 같은 협력적 취소 API를 노출합니다. 취소는 중지를 요청하고, 실제 완료가 소유권을 해제합니다. 시간 초과는 행을 실패시키지만 수락된 네이티브 작업은 끝날 때까지 소유됩니다. 게시는 Python 호출자가 await를 취소해도 네이티브 디스패치가 완료될 때까지 터미널을 유지합니다. 동기 및 bail 리스너는 게시자 경로에서 블로킹 RPC를 수행하므로 짧게 유지하세요. 제공자 등록, 유지된 이벤트 콜백, 임시 터미널은 활성화의 제한된 콜백 예산(`max_provider_callbacks`, 행의 `effect_capacity`에서 부여)을 공유합니다. 제거된 리스너 콜백은 활성화 정리까지 유지되고, 실패한 등록 예약과 완료된 임시 터미널은 해제됩니다.

## 신뢰성

Host가 변경을 커밋한 뒤에도 요청이 시간 초과될 수 있습니다. 시간 초과는 결과를 알 수 없다는 뜻이므로 재시도 전에 읽기 작업으로 상태를 확인하세요. 로그에 request id를 남기고, 비활성화 후에는 전송을 중지하며, Host 연결 끊김을 활성화의 종료 실패로 처리하세요.

`sdk/`의 예제는 현재 프로토콜의 호환성 기준입니다. profile의 capability와 권한 선언을 대신하지는 않습니다.

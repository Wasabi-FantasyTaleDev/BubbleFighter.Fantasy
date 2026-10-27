---
title: "플러그인"
description: "플러그인 모델을 이해하고 네이티브 .NET 플러그인을 빌드, 마운트, 설정, 다시 로드합니다."
updated: 2026-10-23
---

서버의 모든 동작은 플러그인으로 구현됩니다. profile이 플러그인 행을 구성하고, 각 행은 하나의 플러그인 구현과 하나의 설정을 안정적인 인스턴스 id에 묶으며, Host는 각 행이 선언한 서비스 요구 사항이 충족되는 순서로 행을 활성화합니다. 이 페이지는 그 모델을 설명하고, Host가 무엇을 발견하고 로드했는지 확인하는 방법을 보여 준 뒤, 프로젝트 파일에서 실행되는 행까지 첫 네이티브 .NET 플러그인을 단계별로 안내합니다. 마지막으로 설정, 수명 주기와 다시 로드, 네이티브 플러그인과 선택형 언어 SDK의 경계, 플러그인 작성자가 지켜야 할 규칙, 가장 자주 만나는 실패를 다룹니다.

**대상 독자:** 솔루션을 빌드할 수 있고 C#과 YAML을 읽을 수 있으며, 서버 코어를 바꾸지 않고 동작을 추가하려는 개발자.

## 시작하기 전에

- [설치](installation.md)를 마쳤습니다: `dotnet build BubbleFighter.Fantasy.sln -c Release`가 경고 0개, 오류 0개를 보고합니다.
- 터미널이 저장소 루트에 있습니다. 별도 안내가 없는 한 이 페이지의 모든 명령은 여기서 실행합니다.
- .NET 10 SDK를 사용할 수 있습니다: `dotnet --version`이 10.x 버전을 보고합니다.
- 실행 단계에는 `BFF_MYSQL_PASSWORD`가 비어 있지 않은 임시 값인 `.env` 파일이 필요합니다. MySQL을 비활성화해도 이 변수가 필요한 이유는 [빠른 시작](quickstart.md)을 참고하세요.
- 로컬 검사 패치 `tools/local-checks/isolated-memory.patch.yaml`이 존재합니다. 이 페이지의 모든 로컬 실행은 이 패치를 적용하므로 실제 데이터베이스를 건드리지 않습니다.
- 첫 실행에 사용하는 `management` profile은 관리 기능만 제공하며 클라이언트 데이터가 필요 없습니다. `kr-812` 같은 게임 profile은 해당 빌드의 클라이언트 데이터도 필요합니다.

## 플러그인 모델

플러그인은 `[Plugin("plugin.id")]`로 표시되고 `BubbleFighter.Fantasy.Abstractions`의 `IPlugin`을 구현하는 클래스입니다. 속성의 id가 행이 참조하는 **플러그인 id**입니다. profile은 **플러그인 행**을 제공하며, 각 행의 `id`는 **행 id**, 즉 구성된 profile 안에서 유일한 인스턴스 식별자입니다. 하나의 구현을 여러 번 마운트할 수 있습니다. `kr-1256` bundle은 `net.tcp` 플러그인을 `game-listener`와 `relay-listener` 두 행으로 마운트하고, `all` profile은 각 빌드의 행에 `<build>.<행 id>` 접두사를 붙여 마운트합니다.

플러그인은 서로의 클래스를 볼 수 없습니다. 플러그인은 공유 인터페이스 형식으로만 서비스를 게시하고 사용합니다. 이 인터페이스는 Host가 한 번 로드하고 모든 플러그인에 공유하는 두 어셈블리에 있습니다: `BubbleFighter.Fantasy.Abstractions`(`IPlugin`, `IServiceRegistry`, `IEventBus`, `IMessageRouter` 같은 커널 계약)와 `BubbleFighter.Fantasy.Contracts`(메시지 레코드와 도메인 서비스 같은 게임 계약). 행은 필요한 것과 게시하는 것을 선언하며, Host는 요구 사항이 등록된 뒤에만 해당 행을 활성화합니다. 따라서 활성화 순서는 YAML의 행 순서가 아니라 서비스 가용성으로 정해집니다.

| 선언 | 의미 |
| --- | --- |
| `Requires` | 이 행이 활성화되기 전에 등록되어야 하는 싱글턴 서비스 |
| `Optional` | 없어도 되는 서비스. 선택된 제공자는 언로드와 다시 로드 순서에 참여 |
| `Provides`, `ConditionalProvides` | 이 행이 활성화 중에 반드시, 또는 일부 설정에서 게시하는 서비스 |
| `ProvidesMany`, `RequiresMany`, `OptionalMany` | 순서형 확장 지점: 게시, 하나 이상 요구, 빈 목록 허용 |
| `Decorates` | `RegisterDecoratable`로 명시적으로 허용된 싱글턴을 감싸는 데코레이터 계층 |
| `Overrides`, `Middleware` | 형식화된 메시지 처리기 교체와 래핑. `rowId:정규화된 메시지 타입`으로 표기 |
| `SupportedBuilds`, `SupportedFamilies` | 행이 속한 profile에 대한 클라이언트 빌드 호환성 검사 |

등록은 소유권이 생겨야 완료됩니다. `IPluginContext.Effect(IDisposable)`과 `Effect(Action)`은 Host가 정리 중 역순으로 실행하는 해제기를 등록합니다. `AcquireEffectAsync`는 비동기 팩터리가 실행되기 전에 슬롯을 예약하고, `CreateResourceScopeAsync`는 자식 리소스 수명을 만들며, `RunTaskAsync`는 Host가 완료와 취소를 관리하는 백그라운드 작업을 시작합니다. context의 `Services`와 `Events` 뷰로 만든 서비스 등록, 이벤트 구독, 메시지 라우팅은 자동으로 추적되지만, 다른 경로로 얻은 리소스는 반드시 effect로 명시해야 합니다. 각 행은 활성화와 리소스 범위가 공유하는 유한한 effect 용량(`lifecycle.effect_capacity`, 기본 4096)을 받으므로, 등록을 누수하는 플러그인은 결국 실패하고 프로세스를 고갈시키지 않습니다.

## 로드된 항목 확인

### 1. 플러그인 정의 나열

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --log-level Error
```

`plugins list`는 Host의 정의 부트스트랩을 시작하고 플러그인 루트 아래에서 네이티브 플러그인 모듈을 발견한 뒤, 수락된 정의마다 탭으로 구분된 한 줄을 출력합니다:

```text
<플러그인 id>	<버전>	<쉼표로 구분된 지원 빌드>	<출처>
```

세 번째 열은 플러그인이 빌드 독립적일 때 비어 있습니다(예: `loader.native`). 선언이 빌드를 제한하면 `kr:1256` 같은 범위가 표시됩니다. 네 번째 열은 정의 출처입니다. Host 자체가 가진 정의는 어셈블리 이름을, 루트 아래에서 발견된 모듈은 플러그인 디렉터리를 표시합니다. `--log-level Error`는 활성화 로그가 목록에 섞이지 않게 합니다. 지정하지 않으면 활성화 메시지와 목록이 표준 출력을 함께 사용합니다. `--plugin-root <dir>`는 반복 지정할 수 있으며, 하나라도 주어지면 기본 루트 목록을 대체합니다. 기본 루트는 Host 실행 파일 옆의 `plugins` 디렉터리입니다(`dotnet run` 빌드에서는 `src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/` 아래 빌드 출력).

**확인 방법:** 명령이 코드 0으로 끝나고 플러그인 id가 예상한 버전과 빌드 범위로 나타납니다. 이 명령은 Host가 마운트할 수 있는 정의를 나열할 뿐, 어떤 행도 활성화하지 않습니다.

### 2. 런타임 상태 읽기

실행 중인 서버는 `GET /api/plugins`에서 행 인스턴스를 노출합니다:

```powershell
Invoke-RestMethod http://127.0.0.1:8080/api/plugins | Select-Object rowId, plugin, state, error
```

```sh
curl -s http://127.0.0.1:8080/api/plugins
```

각 항목에는 `rowId`, `plugin`, `source`, `state`, `error`, `codeRefreshPolicy`, `codeLifetime`, `restartRequiredForReclamation`, `isRetiring`, `overrides`, `version`, `contractVersions`, `supportedBuilds`가 있습니다. 상태는 문자열로 기록되며 `PluginState` 열거형에서 가져옵니다. `start`로 시작한 서버에서는 `/api/plugins`에 로그인한 관리자 세션이 필요하며, `run --profile` 서버는 루프백에서 로그인 없이 응답합니다. 자세한 내용은 [빠른 시작](quickstart.md)을 참고하세요.

### 3. 플러그인 어셈블리 위치 파악

빌드된 Host는 플러그인 루트의 각 하위 디렉터리를 하나의 네이티브 모듈로 취급합니다. 규칙은 고정입니다. 루트 `<root>`에 대해 디렉터리 `<root>/<Name>`이 하나의 모듈이고, 주 어셈블리는 반드시 `<root>/<Name>/<Name>.dll`이어야 하며, 모듈의 다른 종속성은 같은 디렉터리에 둡니다. 저장소 빌드는 정확히 이 배치를 만듭니다. 각 플러그인 프로젝트의 빌드 출력은 `artifacts/plugins/<AssemblyName>/`로 미러링되고, Host 빌드는 그 미러 폴더로 출력 `plugins` 디렉터리를 교체합니다. 게시 스크립트는 같은 구조로 `<게시 디렉터리>/plugins/<프로젝트 이름>/`을 만듭니다.

Host는 `BubbleFighter.Fantasy.Abstractions`, `BubbleFighter.Fantasy.Contracts`, 로깅 추상 어셈블리를 자신의 기본 컨텍스트에서 제공하므로, 이 어셈블리를 플러그인 디렉터리에 복사하면 안 됩니다. 저장소의 플러그인 빌드 대상이 자동으로 제거합니다. 그 밖에 플러그인이 런타임에 필요로 하는 모든 것은 자신의 모듈 디렉터리 안에 있어야 합니다.

## 첫 네이티브 플러그인 작성

### 1. 프로젝트 만들기

`src/Plugins/BubbleFighter.Fantasy.Heartbeat/BubbleFighter.Fantasy.Heartbeat.csproj`를 만듭니다:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Logs one heartbeat line per activation.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Abstractions\BubbleFighter.Fantasy.Abstractions.csproj" />
    <ProjectReference Include="..\..\BubbleFighter.Fantasy.Contracts\BubbleFighter.Fantasy.Contracts.csproj" />
  </ItemGroup>
</Project>
```

저장소 루트의 `Directory.Build.props`가 `net10.0`, nullable 참조, 경고를 오류로 처리하는 설정을 제공하고, `src/Plugins/Directory.Build.targets`가 ASP.NET Core 프레임워크 참조를 추가하고 Host 공유 어셈블리를 제거하며 빌드를 `artifacts/plugins/`로 미러링합니다. 저장소 밖의 플러그인은 `<root>/<Name>/` 폴더에 두고 DLL을 `<root>/<Name>/<Name>.dll`에 배치한 뒤 `--plugin-root`로 Host를 `<root>`에 지정합니다.

### 2. 플러그인 클래스 작성

`src/Plugins/BubbleFighter.Fantasy.Heartbeat/HeartbeatPlugin.cs`를 만듭니다:

```csharp
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Example.Heartbeat;

[Plugin("example.heartbeat", Description = "Logs one heartbeat line per activation.")]
public sealed class HeartbeatPlugin : IPlugin
{
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<HeartbeatOptions>();
        context.Logger.LogInformation("Heartbeat every {IntervalSeconds} s.", options.IntervalSeconds);
        context.Effect(() => context.Logger.LogInformation("Heartbeat stopped."));
        return ValueTask.CompletedTask;
    }
}

public sealed class HeartbeatOptions
{
    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 30;
}
```

`Config.Bind<T>()`는 행의 `config` 매핑을 읽고, snake_case 키를 PascalCase 속성으로 바꾸며, DataAnnotations 유효성 검사를 실행하고, 모든 실패를 나열하는 하나의 `ConfigException`을 던집니다. `Effect`에 전달한 람다는 정리 시 한 번 실행되며, 그 시점에 행의 수명 토큰은 이미 취소된 상태입니다. 실제 플러그인에서는 리소스를 먼저 얻고 즉시 해제기를 등록해야 합니다.

### 3. 빌드

```powershell
dotnet build BubbleFighter.Fantasy.sln -c Release
```

빌드에 성공하면 `0 Warning(s)`와 `0 Error(s)`가 출력됩니다. 플러그인은 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`에 생성되고, Host 빌드가 그 미러를 복사하므로 Host 출력의 `plugins/BubbleFighter.Fantasy.Heartbeat/`에도 나타납니다.

**확인 방법:** 이제 정의를 발견할 수 있습니다. Host를 다시 빌드하지 않고 목록을 산출물 미러로 지정해 확인할 수 있습니다:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- plugins list --plugin-root artifacts/plugins --log-level Error
```

출력에 첫 번째 열이 `example.heartbeat`인 줄이 있어야 합니다.

### 4. 행으로 마운트

패키지된 profile을 건드리지 않도록 패치 파일로 행을 추가합니다. `artifacts/heartbeat.patch.yaml`을 만듭니다:

```yaml
- insert:
    - id: example.heartbeat
      plugin: example.heartbeat
      config:
        interval_seconds: 15
```

`insert`는 행을 덧붙입니다. 다른 패치 작업은 `replace`, `set`, `remove`입니다. 격리 메모리 패치를 먼저, heartbeat 패치를 마지막에 적용해 관리 전용 profile을 시작합니다:

```powershell
dotnet run --project src/BubbleFighter.Fantasy.Host -c Release -- run --profile management --patch tools/local-checks/isolated-memory.patch.yaml --patch artifacts/heartbeat.patch.yaml
```

Host가 행을 마운트하고 활성화합니다. 예상 출력에는 다음 형태의 줄이 나오고, 이어서 플러그인 자체 메시지와 활성 행 수가 나옵니다:

```text
Row 'example.heartbeat' activated (plugin 'example.heartbeat' version 1.0.0+<commit>; contracts ...; builds independent).
Heartbeat every 15 s.
```

**확인 방법:** 활성화 줄에 행과 플러그인 이름이 나오고, 플러그인 로그 줄이 나타나며, 어떤 행도 `Failed`나 `Pending`을 보고하지 않습니다. `GET /api/plugins`가 `example.heartbeat`를 `Active` 상태로 보여 줍니다. Ctrl+C로 중지하면 콘솔이 `Shutdown complete.`를 출력하고 heartbeat effect가 종료 과정에서 실행됩니다.

### 5. 되돌리기

`artifacts/heartbeat.patch.yaml`을 삭제하고 더 이상 전달하지 않습니다. 패치를 제거하면 인스턴스만 사라지고, 모듈 디렉터리를 삭제하기 전까지 정의는 계속 발견됩니다. 플러그인 프로젝트도 삭제했다면 Host를 다시 빌드하기 전에 `artifacts/plugins/BubbleFighter.Fantasy.Heartbeat/`를 삭제하세요. Host는 산출물 미러에 있는 폴더를 모두 복사하기 때문입니다.

## 행 설정

| 행 키 | 용도 |
| --- | --- |
| `id` | 필수. 구성된 profile 안에서 유일한 인스턴스 id |
| `plugin` | 필수. `[Plugin]` 선언의 플러그인 id |
| `config` | 플러그인이 바인딩하는 매핑. 키는 플러그인이 바인딩할 때 검증됨 |
| `disabled` | `true`이면 행을 나열하지만 활성화하지 않음. 비활성 행은 보간하지 않음 |
| `version` | `>=1.2 <2` 같은 릴리스 제약. 정의 버전과 비교 |
| `parent_id` | 런타임 부모 행. 부모가 먼저 활성화되고 자식 정리가 끝난 뒤 해제됨 |
| `isolated_services` | 이 행의 하위 트리에 새 서비스 네임스페이스를 부여하는 인터페이스 이름 |
| `lifecycle` | 리소스 용량, 정리 한도, 코드 새로 고침 정책. 다음 절 참고 |

활성화된 행의 `config` 값은 구성 시 환경 변수 보간을 지원합니다. `${NAME}`은 변수를 요구하고, `${NAME:-default}`는 기본값을 제공하며, 기본값 없는 변수가 없으면 구성이 `environment variable '<NAME>' is not set and has no default.`로 실패합니다. `disabled: true`인 행은 절대 활성화되지 않으므로 그 설정은 전혀 보간하지 않습니다. 행의 `version` 제약은 활성화 시 검사되며, 실패하면 `Plugin '<id>' version <version> does not satisfy row '<row>' constraint '<constraint>'.`를 보고합니다.

활성화 순서는 선언된 서비스의 가용성으로 결정됩니다. 요구 사항이 등록되지 않은 행은 `Pending`으로 남고, 그 `error`가 대기 내용을 나열합니다(예: `Waiting for services: <interface>; provider rows: <rows>; override rows: <rows>.`). 시작이 진행되지 않으면 서버는 `Plugin activation stalled; the following rows have unsatisfiable service dependencies:`를 보고하고 막힌 행마다 한 줄을 덧붙입니다. 플러그인이 서비스를 필요로 하면 먼저 선언하고 context를 통해서만 읽어야 합니다. 선언되지 않은 접근은 다른 플러그인의 구현을 조용히 관찰하는 대신 예외를 던집니다.

## 수명 주기와 다시 로드

| 상태 | 의미 |
| --- | --- |
| `Active` | 행이 활성화되어 effect가 살아 있음 |
| `Pending` | 행은 원하는 상태지만 필수 서비스나 선택된 제공자가 아직 준비되지 않음 |
| `Unloading` | effect 수락이 닫히고 행이 비워지는 중 |
| `Unloaded` | 행이 중지되어 살아 있는 effect가 없음 |
| `Failed` | 활성화나 소유 작업이 실패했고 행이 effect를 보유하지 않음 |
| `WorkFailed` | 소유한 백그라운드 작업이 실패했고 정리가 끝날 때까지 리소스를 계속 소유 |
| `CleanupIncomplete` | 정리가 관찰 한도를 넘어섰고 소유권과 종속성이 유지됨 |
| `CleanupFailed` | 정리가 오류를 보고했고 재시작이 거부됨 |

언로드는 effect 수락을 닫고, 활성화의 `Lifetime` 토큰을 취소하고, 수락된 이벤트 콜백이 끝날 때까지 기다린 뒤 effect를 역순으로 해제합니다. 한 행이 게시한 서비스에 아직 활성 행이 종속되어 있으면 그 행은 언로드될 수 없습니다. Host는 `Row '<id>' cannot unload; active row(s) depend on its services: ...`를 던집니다.

`POST /api/plugins/{rowId}/reload`는 `IPluginHost.ReloadAsync`로 한 행을 다시 로드합니다. 그 행의 서비스를 사용하는 종속 행이 활성화 역순으로 먼저 언로드되고, 대상 행이 다시 로드된 뒤 종속 행이 원래 순서로 다시 활성화됩니다. 응답에는 다시 로드된 행 id가 포함됩니다. 격리된 모듈에서 로드된 네이티브 플러그인은 다시 로드할 때 디스크에서 모듈 어셈블리도 다시 로드합니다. 계약에는 두 가지 한도가 있습니다.

- `lifecycle.code_refresh_policy: restart_required`는 코드의 제자리 새로 고침을 `Row '<id>' requires a process restart for code refresh (lifecycle.code_refresh_policy=restart_required).`로 거부합니다. 안전하게 새로 고칠 수 없는 코드에 사용하세요.
- 다시 로드는 프로세스 수명 코드를 회수할 수 없습니다. 일반 네이티브 모듈의 `codeLifetime`은 `ProcessLifetime`이고, 백엔드가 회수를 지원할 때만 `Collectible`입니다. 회수에 재시작이 필요하면 `restartRequiredForReclamation`이 true입니다. 다시 로드는 백엔드가 지원하는 범위에서만 코드를 새로 고칩니다.

실패하거나 시간 초과된 정리는 완료된 척하지 않습니다. 시간 초과는 행을 `CleanupIncomplete`로, 오류는 행을 `CleanupFailed`로 남기며, 행은 소유권과 종속성 리스를 유지하고 정리가 끝날 때까지 재시작을 거부합니다. `lifecycle` 아래에서 행별 한도를 설정합니다. 플러그인 작성자가 보통 다루는 값은 `effect_capacity`(기본 4096), `teardown_timeout_ms`(기본 10000), `code_refresh_policy`(기본 `allow`)입니다. 정의를 게시하는 행은 시작 부여량(`definition_source_reserve`, `definition_reserve`)도 선언합니다. 이 값은 정의를 게시하는 행에 두고 시작 후에는 변경하지 마세요.

## 네이티브 .NET 또는 언어 SDK 선택

| 능력 | 네이티브 .NET 플러그인 | 선택형 언어 SDK 플러그인 |
| --- | --- | --- |
| 확장 범위 | 모든 커널 및 게임 계약 | 형식화된 메시지, 선언된 서비스, 데코레이터, 순서형 컬렉션, 이벤트, 작업, 리소스 |
| 로드 방식 | 네이티브 모듈로 발견 | `loader.process` 자식 프로세스 또는 `loader.wasm` 모듈 |
| 계약 접근 | 인터페이스와 형식을 직접 사용 | 선언한 이름을 생성된 계약 매니페스트로 해석 |
| 코드 새로 고침 | 설정된 백엔드로 모듈 다시 로드 | 프로세스나 모듈이 행 또는 프로세스 종료까지 실행 |
| 도구 체인 | .NET 10만 | Python, Node.js, Go 또는 Rust와 해당 loader |

.NET이 계속 주요 플랫폼입니다. SDK는 같은 Host 계약을 연결하며 게임 로직을 중복 구현하지 않지만, 그중 일부만 다룹니다. 완전한 네이티브 동등성은 구현되지 않았습니다. 패킷 코덱, 네이티브 연결 및 형식 식별자, 델리게이트, 참조 매개변수, 공유 계약 어셈블리 밖의 계약 검색, 네이티브 호출자 컨텍스트를 받는 제공자 메서드는 전용 브리지가 필요합니다. 계약 매니페스트는 모든 작업을 호출 가능(`rpc_shape`) 또는 SDK 제공자가 구현 가능(`provider_rpc_shape`)으로 표시하며, 지원되지 않는 형식은 조용히 저하되지 않고 명시적으로 실패합니다.

코덱, 디코딩 계층의 메시지 middleware, 브리지가 모델링하지 않은 계약이 필요하면 네이티브 플러그인을 사용하세요. 다른 언어로 정책, 게임 로직, 서비스 구현을 작성하고 능력이 선언된 범위에 들어오면 SDK 플러그인을 사용하세요. [SDK 프로토콜](sdk-protocol.md)에서 프로토콜, 언어별 빠른 시작, 계약 매니페스트, 일관성 테스트 실행기를 설명합니다.

## 플러그인 작성자 규칙

- 조정 가능한 값을 하드코딩하지 마세요. 한도, 간격, 식별자를 경계와 검증이 있는 행 설정으로 노출하세요. 운영자가 바꿀 수 없는 값은 결함입니다.
- 모든 리소스를 유한하게 유지하세요. 큐, 캐시, 세션, 처리기 테이블, 백그라운드 작업에는 명시적 한도가 필요합니다. 고갈을 나중에 발견하지 말고 획득 전에 effect 용량을 예약하세요.
- 명확하게 실패하세요. 설정이나 필요한 상태가 잘못되면 활성화 중에 예외를 던지고, 실패를 삼킨 채 부분 상태로 계속하지 마세요. Host는 예외를 메시지가 있는 `Failed` 행으로 바꿉니다.
- 등록한 것을 소유하세요. 리소스를 얻은 직후 effect를 등록하고, 소유하지 않은 핸들을 해제하지 말며, 정리 실패를 숨기지 말고 보고하세요. 불완전한 정리는 설계상 소유권을 유지합니다.
- 비동기 작업에는 소유권 API를 사용하세요. `AcquireEffectAsync`와 `RunTaskAsync`는 취소와 완료 추적을 제공합니다. 맨 fire-and-forget 작업은 행의 수명에서 벗어나 누수가 됩니다.
- 수명 주기 콜백은 짧게 유지하세요. 활성화, 비활성화, 해제기는 무관한 작업을 기다리거나 같은 행의 배수를 기다리면 안 됩니다.
- 공개 형식과 멤버에 영어 XML 문서를 작성하세요. 솔루션은 모든 프로젝트에서 경고를 오류로 처리하며 문서를 생성하므로, 문서가 없는 공개 멤버는 빌드를 실패시킵니다.
- 공유 계약 어셈블리의 사본을 플러그인과 함께 배포하지 말고, 플러그인 코드에서 제3자 프로젝트를 참조하지 마세요.
- 메시지와 이벤트 페이로드를 값으로 취급하세요. 받은 개체를 변경해도 변경이 게시되지 않고 다른 플러그인의 뷰를 손상시킬 수 있습니다.

## 문제 해결

| 증상 | 확인 | 가능한 원인과 해결 |
| --- | --- | --- |
| `plugins list`에 플러그인이 없음 | 전달한 루트, 폴더와 DLL 이름 | 모듈 디렉터리 이름은 어셈블리 파일 이름의 줄기와 같아야 하고, 어셈블리에 `[Plugin]` 형식이 있어야 합니다. 잘못된 모듈은 `Plugin assembly '<path>' defines no [Plugin] type.` 또는 중복 id 오류를 보고합니다. |
| 행이 계속 `Pending` | `/api/plugins`의 `error` 필드와 시작 로그 | 필수 서비스가 빠졌습니다. `Waiting for services: ...; provider rows: ...`를 읽고 제공자 행을 활성화하거나 수정하세요. override 대상 행도 활성 상태여야 합니다. |
| 시작이 `references unknown plugin '<id>'`로 실패 | 정의 출처 행이 마운트되고 활성화되었는지 | 네이티브 정의에는 모듈이 들어 있는 플러그인 루트가 필요합니다. SDK 정의에는 같은 profile에 `loader.process` 또는 `loader.wasm` 행이 있어야 하고, 그 loader 행이 정의 출처(`lifecycle.definition_source_reserve`)를 예약해야 합니다. |
| `Declared service(s) were not published: ...` | `Services.Register`를 호출해야 하는 활성화 코드 | `Provides` 선언은 약속입니다. 활성화 중에 서비스를 등록하고 등록을 `Effect`에 넘기세요. |
| `Invalid configuration for row '<row>': unknown key '<key>'` | 행의 `config` 키와 바인딩 옵션 형식 | 바인딩은 알 수 없는 키를 거부합니다. 키를 바꾸거나 옵션 형식에서 받아들이세요. 메시지가 모든 실패를 하나의 예외로 나열합니다. |
| `Duplicate row id '<id>' in profile '<profile>'` | `--dump-config`로 구성한 행 목록 | 두 행이 같은 id를 쓰거나 `all` profile에서 빌드 범위 행이 충돌합니다. 인스턴스 id를 바꾸세요. |
| 버전 또는 빌드 불일치 | 행의 `version`, profile의 `client_build`, 플러그인의 지원 범위 | 오류가 제약을 명시합니다: `does not satisfy row ... constraint`, `does not support client build ...`, `Plugin requires BubbleFighter.Fantasy.Abstractions ... host provides ...`. 플러그인을 배포된 계약에 맞추세요. |
| 행이 `Failed`이거나 정리가 불완전함 | `error` 텍스트와 플러그인의 해제기 | 첫 실패를 읽으세요. `CleanupIncomplete`와 `CleanupFailed`는 소유권을 유지하고 재시작을 거부하므로, 무작정 재시도하지 말고 실패한 해제기나 취소 경로를 고치세요. |

서버 전체가 시작 중 종료되면 메시지 `Server startup did not converge (<outcome>): <row>: <state> (<error>)`가 가장 먼저 활성화되지 않은 행을 나열합니다. 첫 번째 `Failed` 또는 `Pending` 항목을 고치세요. 나머지는 대개 그 결과입니다. 증상 중심 안내는 [문제 해결](troubleshooting.md)을 참고하세요.

## 다음 단계

[SDK 프로토콜](sdk-protocol.md)을 이어 읽고 Python, TypeScript, Go 또는 Rust로 작성한 플러그인을 마운트하세요. 그다음 [기여](contributing.md)에서 검증 게이트와 검토 규칙을 확인하세요. [서버 설정](configuration.md)은 profile, bundle, 패치, 환경 변수를 자세히 설명합니다.
## 명령과 권한 등록 {#register-commands}

알림 후 연결을 끊는 작업에서 `ISession.SendAsync`는 메시지를 큐에 넣기만 합니다. 세션이 `ISessionOutputDrain`을 구현하면 닫기 전에 작업에 설정된 취소 기한으로 `DrainOutputAsync`를 기다립니다. 이 펜스는 이전 전송 쓰기의 완료만 보장하며 클라이언트 수신이나 화면 표시를 확인하지 않습니다. 취소는 대기만 중단하고 큐의 메시지를 철회하지 않습니다. 큐가 가득 차거나 세션이 중지되거나 쓰기가 실패하면 대기도 실패합니다. 고정 지연이나 구체적인 프로토콜 클래스로 공유 인터페이스를 대체하지 마세요.

core 번들의 `permissions`, `commands` 행은 네이티브 플러그인용 명령 기반을 제공합니다. 플러그인은 `Requires`에 `BubbleFighter.Fantasy.Contracts.Administration`의 `IPermissionRegistry`, `ICommandRegistry`를 선언하고 권한 노드를 먼저 등록한 뒤 명령을 등록합니다. 두 등록 핸들은 모두 `context.Effect(...)`로 소유합니다.

```csharp
context.Effect(context.Services.Get<IPermissionRegistry>().Register(
    new PermissionDefinition("example.ping", "Check whether the example plugin is ready.")));
context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
    new CommandDefinition("ping", ["ready"], "example.ping", "Check plugin readiness.", []),
    (invocation, token) => ValueTask.FromResult(
        new CommandResult(CommandStatus.Success, "Ready."))));
```

명령 이름과 별칭은 전역에서 고유하며 등록 시 메타데이터를 복사합니다. 등록 해제는 모든 별칭을 제거하고 실행 중인 작업에 취소를 알립니다. 처리기는 등록 플러그인의 감독되는 작업으로 실행되므로 플러그인 해제 시 취소하고 완료를 기다립니다. 처리기는 전달된 취소 토큰을 따라야 합니다. 정상적인 상태 오류는 `InvalidState`로 반환하고 예기치 않은 예외는 호출자와 소유 플러그인에 보고합니다. `ICommandRegistry.List(principal)`은 권한으로 필터링된 도움말 및 패널 메타데이터를 반환합니다.

`ICommandExecutor.ExecuteAsync`는 명령 이름과 구조화된 위치 인수를 받습니다. `ExecuteTextAsync`는 `/`, `!`, `！`, 큰따옴표 인수, 부호 있는 정수, 마지막 `RemainingText`를 지원합니다. 구조화된 호출은 나머지 본문을 하나의 인수로 전달합니다. 본문에 별도의 단어 수 제한은 없습니다. 콘솔 어댑터는 접두사 없는 입력을 명시적으로 허용할 수 있으며 shell 코드를 실행하지 않습니다.

신뢰할 수 있는 어댑터만 인증 결과로 `CommandCaller`를 구성합니다. 안정적인 계정 ID를 사용하고 대리 실행 시 원래 `Actor`와 유효 `Subject`를 보존하며 요청 인수에서 이 신원을 역직렬화하면 안 됩니다. `Language`는 사용자의 UI 언어이며 빈 값은 배포 기본 언어를 요청합니다. 감사 이벤트는 신원과 결과만 포함하며 원본 인수는 저장하지 않습니다. 네이티브 등록을 지원하며 원격 SDK 등록에는 콜백 및 수명 어댑터가 더 필요합니다. 콘솔 입력과 WebUI는 아직 이 디스패처에 연결되지 않았습니다.

`ingame` 행의 config에 `shared_chat_commands: true`를 설정하면 해당 게임 범위의 채팅을 공유 명령에 연결합니다. 네이티브 클라이언트 검증 전까지 기본값은 `false`이며, 비활성 범위는 기존 방장 전용 `!start`, `!set`을 유지합니다. 활성화하려면 두 공유 명령 서비스가 모두 필요하며 없으면 활성화에 실패합니다.

core 번들의 `commands.game` 행은 활성 범위를 같은 디스패처에 연결하며 네이티브 플러그인이 등록한 명령도 호출합니다. 모든 게임 범위에 `/start`, `/mode <번호>`(별칭 `/set`)를 한 번만 등록하고 `!`, `！` 접두사도 지원합니다. `game.room.start`, `game.room.mode` 권한은 기본적으로 일반 플레이어에게 허용되며 자신의 대기 중인 방에만 적용됩니다. 설정된 모드 표에 없는 번호는 거부됩니다. 명령 시작은 준비 표시를 건너뛰지만 기본 시작 버튼의 방장 검사는 유지됩니다. 권한이 거부된 접두사 입력은 로컬 처리기로 우회하지 않습니다.

게임 플러그인은 `IGameCommandEndpoints`를 통해 `IGameCommandEndpoint`를 등록하고 반환된 해제 핸들을 소유합니다. 라우팅은 인증된 게임 계정을 현재 좌석과 대조하고 중복된 세션 소유권을 거부하며 게임 플러그인의 수명 안에서 실행합니다. `commands.game.max_endpoints`는 기본 512로 동시 범위 등록 저장소를 제한합니다. 이 행은 지도, 관리 또는 방 이동 명령을 아직 제공하지 않습니다.

게임 범위를 등록 해제하거나 교체하면 이전 라우팅 스냅샷은 무효가 됩니다. 실행을 시작하지 않은 호출은 해제된 엔드포인트를 호출하거나 교체된 엔드포인트로 자동 전환할 수 없습니다.

`permissions` 행의 설정 예:

```yaml
config:
  groups:
    - name: players
      allow: [example.ping]
    - name: helpers
      inherits: [players]
      deny: [example.restricted]
  subjects:
    - kind: game
      id: "42"
      groups: [helpers]
    - kind: game
      id: "7"
      operator: true
    - kind: webui
      id: "admin-account-id"
      allow: [webui.access, webui.admin]
```

ID는 각 인증 네임스페이스에 이미 존재하는 계정을 가리키며 이 설정은 계정이나 로그인 자격 증명을 만들지 않습니다. 일반 신원의 거부 규칙은 개인, 상속 및 와일드카드 허용보다 우선합니다. 규칙은 정확한 노드, `prefix.*`, `*`를 지원합니다. 알 수 없는 그룹과 상속 순환은 활성화 오류이고 미등록 노드는 거부합니다. 게임 OP와 로컬 콘솔은 모든 등록 권한을 가지지만 게임 OP가 WebUI 로그인을 허용하지는 않습니다. `webui.access`는 진입만, `webui.admin`은 WebUI 전체 관리 권한을 나타냅니다. 권한은 인수 또는 상태 검증을 우회하지 않습니다. 이 WebUI 노드는 어댑터 통합용이며 기존 HTTP 인증을 아직 대체하지 않습니다.

자원 설정은 게임 기능 수가 아닌 등록 저장소와 실행 작업을 보호합니다. `commands`는 `max_commands`(512), `max_input_characters`(4096, 등록 이름 및 인수 정의 텍스트 예산에도 사용), `max_result_characters`(16384), `max_concurrent`(64)를 받습니다. 실행 용량이 차면 대기열 없이 `Busy`를 반환합니다. `permissions`는 `max_nodes`(2048), `max_policy_entries`(65536, 계정·그룹·상속 링크·확장된 규칙의 총 저장 예산)를 받습니다. 검증되는 값은 배포에 맞게 변경할 수 있습니다.

### 온라인 관리 명령 {#online-management-commands}

Contracts 2.61.0은 네이티브 온라인 범위 등록을 추가합니다. `commands.management`는 `/list`, `/notice <텍스트>`, `/disconnect <닉네임> [사유]`를 한 번 등록합니다. `commands.players`는 빌드별 인증된 플레이어 디렉터리와 지정한 프로토콜의 알림 인코더를 연결합니다. 실제 클라이언트 검증 전까지 두 행은 기본 비활성 상태입니다. 관리 행과 대상 빌드의 플레이어 행을 함께 활성화하세요. 플레이어 행은 `lobby` 아래에 두고 `lobby.online_players: true`와 필수 `protocol` 설정이 필요합니다. 해당 `dialect.messages` 행도 `server_notices: true`여야 합니다. 인코더가 없거나 중복되면 활성화에 실패합니다. 복합 프로필의 전체 행 ID는 `--dump-config`로 확인하세요.

`/list`는 메인 메뉴 플레이어를 포함한 중복 없는 온라인 계정 수를 반환하며 이름이나 네트워크 주소는 공개하지 않습니다. `game.management.list`는 기본적으로 일반 플레이어에게 허용됩니다. `/notice`는 모든 빌드의 인증된 게임 연결에 입력 텍스트를 보내고 대기열 등록 및 실패 수를 보고합니다. `game.management.notice`가 필요합니다. `/disconnect`는 `game.management.disconnect`가 필요하며 현재 닉네임을 정확히 비교하고 여러 연결이 일치하면 거부합니다. 게임 OP는 두 관리 권한을 가집니다. 게임 호출은 호출자의 고정 계정이 현재 연결을 소유하는지도 확인합니다. 채팅 진입에는 `ingame.shared_chat_commands: true`가 필요합니다. 콘솔과 WebUI 어댑터는 아직 없습니다.

연결 종료 알림은 대상의 `launcher_language`를 사용하며 선택 사유를 포함합니다. 알림을 대기열에 넣고 `ISessionOutputDrain`을 기다린 다음 선택한 연결을 닫습니다. 알림 실패를 기록하며 대상이 여전히 유효하면 연결을 종료합니다. 재로그인한 새 연결을 따라가서 종료하지 않습니다. 결과는 알림을 완료하지 못한 경우 이를 표시합니다. 클라이언트 수신이나 화면 표시를 보장하지 않습니다. 공지도 대기열 등록 및 부분 실패만 보고하며 읽음 확인이 아닙니다.

`commands.management` 옵션은 `max_scopes`(등록 512개), `disconnect_notice_timeout_ms`(5000), `max_notice_characters`(511)입니다. 텍스트 예산은 낮출 수 있으며 상한은 클라이언트 알림 버퍼를 따릅니다. 서식 적용 후 사유가 너무 길면 전송이나 종료 전에 거부합니다. 범위 등록은 소유된 효과이며 호출은 등록 플러그인의 감독 수명 안에서 실행됩니다. 원격 SDK 등록은 네이티브 소유자와 콜백 전송 어댑터가 필요하므로 아직 지원하지 않습니다. 이 행은 kick, sudo, OP 영구 편집, 방 이동, WebUI 설정 또는 배포 스토어를 구현하지 않습니다.


### 클라이언트 정책 {#client-plugin-policy}

WebUI는 설치된 서버의 [클라이언트 정책](launcher.ko.md#client-policy), 즉 스토어 소스와 플러그인별 유형 규칙을 편집합니다. 이 규칙은 호환 런처를 구성하며 클라이언트 파일은 서버에 설치되지 않습니다. 프로필 모드에서는 선택한 프로필의 정책을 사용합니다. 처음 변경을 저장하면 사용자 지정 정책으로 바뀌고, 프로필 모드로 복원하면 프로필 자체의 정책으로 돌아갑니다.

모든 경로에는 `webui.access`와 `webui.settings.client_policy`를 모두 가진 로그인한 WebUI 계정이 필요하며 `webui.admin`은 둘 다 부여합니다. 게임 OP는 WebUI 접근 권한을 부여하지 않습니다.

| 경로 | 용도 |
|---|---|
| `GET /api/setup/client-policy` | `mode`(`profile` 또는 `custom`), `revision`, 저장된 `policy`(프로필 모드에서는 null), 그리고 런처가 지금 받는 정책인 `active`를 반환합니다. |
| `GET /api/setup/client-policy/rule-types` | 각 규칙 유형의 `name`, 양식 `field`(`none`, `version` 또는 `sources`), 충돌할 수 있는 유형 `conflicts`를 반환합니다. |
| `PUT /api/setup/client-policy` | 정책 전체를 교체합니다: `{"policy":{"sources":[],"plugins":[]}}`. `{"policy":null}`은 프로필 정책으로 돌아갑니다. |
| `PUT /api/setup/client-policy/sources` | 소스 목록만 교체합니다. |
| `PUT /api/setup/client-policy/plugins/{id}` | 다른 플러그인은 그대로 두고 한 플러그인의 항목 `{"id":"{id}","name":null,"description":null,"rules":[...]}`을 교체합니다. |
| `DELETE /api/setup/client-policy/plugins/{id}` | 한 플러그인의 항목을 삭제합니다. |

쓰기 요청은 기존 동일 출처 요청 보호를 사용하며 편집한 `revision`을 `If-Match`로 보내야 합니다. 없으면 428로 거부되고, 이전 revision에 대한 쓰기는 현재 `revision`과 함께 409로 거부되므로 동시에 열린 두 편집기가 서로 덮어쓰지 않습니다. 프로필 모드에서의 부분 쓰기는 현재 적용 중인 정책을 기준으로 합니다. 거부된 정책은 `error`(`client_policy_invalid`, `client_policy_unknown_type`, `client_policy_unknown_source`, `client_policy_unknown_build` 또는 `client_policy_conflict`)와 알 수 있는 경우 `plugin`, `rule`, 충돌하는 `other` 규칙을 담아 400을 반환합니다. 규칙의 빌드는 `management`가 아닌 구성된 클라이언트 프로필이어야 합니다.

변경은 데이터베이스, 계정, 보상 및 클라이언트 데이터 설정과 독립적으로 저장되고 기존 감독 설정 적용 절차로 실행됩니다. HTTP 202는 요청 수락만 뜻합니다. 적용 중 게임 서비스가 다시 연결될 수 있으며 설치 상태에 진행 상황과 실패가 표시됩니다. 기존 설정 절차로 수정하거나 다시 시도할 수 있습니다.

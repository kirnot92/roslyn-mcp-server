# Roslyn MCP Server Architecture

## 방향

`roslyn-mcp-server`는 Roslyn API를 직접 감싸는 서버가 아니라, `roslyn-language-server`를 자식 프로세스로 실행하고 MCP tool 호출을 LSP 요청으로 변환하는 얇은 브리지다.

초기 구현의 핵심 원칙은 다음과 같다.

- MCP 서버는 C#/.NET으로 작성한다.
- MCP 구현은 `modelcontextprotocol/csharp-sdk`를 사용한다.
- `roslyn-language-server`는 사용자가 별도로 설치한 실행 파일을 PATH 또는 명시 경로에서 찾는다.
- 서버 시작 시 솔루션을 자동으로 강제 로드하지 않는다.
- 현재 작업 디렉터리 또는 `--root` 아래의 `.sln`, `.slnx`, `.csproj` 후보를 탐색하고, `load_solution`/`load_project` tool 호출로 workspace를 선택한다.
- 읽기 전용 tool을 먼저 구현한다. 파일을 수정하는 refactoring/code action 계열은 별도 단계에서 opt-in으로 추가한다.
- 대규모 repo를 기본 대상으로 보고, 모든 탐색/진단/검색 tool은 timeout, result limit, cancellation, pagination을 갖는다.

## 프로세스 모델

```text
Agent CLI
  <-> MCP JSON-RPC over stdio
roslyn-mcp-server
  <-> LSP JSON-RPC over stdio
roslyn-language-server
  <-> selected .sln/.slnx/.csproj and source files
```

`roslyn-mcp-server`는 MCP 클라이언트와 stdio로 통신한다. Roslyn 기능이 필요한 시점에 `roslyn-language-server --stdio --autoLoadProjects`를 자식 프로세스로 실행하고, 별도의 LSP read loop/write queue를 통해 요청을 중계한다.

중요한 선택은 Roslyn LS를 서버 시작 직후 띄우지 않는 것이다. 먼저 workspace 후보를 탐색하고, 실제로 어떤 솔루션 또는 프로젝트를 사용할지 정해진 뒤 Roslyn LS를 시작한다. 이렇게 해야 여러 `.sln`이 있는 repo에서 모호한 자동 로드를 피할 수 있다.

## .NET 프로젝트 구성

현재 solution 구조는 다음처럼 둔다.

```text
roslyn-mcp-server.sln
src/
  RoslynMcpServer/
    RoslynMcpServer.csproj
    Program.cs
    Cli/
      CliOptions.cs
    Mcp/
      WorkspaceTools.cs
      NavigationTools.cs
      DiagnosticsTools.cs
      ToolModels.cs
    Workspace/
      WorkspaceScanner.cs
      WorkspaceSession.cs
      WorkspaceModels.cs
      PathGuard.cs
      DocumentStateManager.cs
      DocumentPathMapper.cs
      IGitWorkspaceScanner.cs
      GitWorkspaceScanner.cs
    Lsp/
      RoslynLanguageServerLocator.cs
      RoslynLanguageServerProcess.cs
      IRoslynWorkspaceLoader.cs
      LspClient.cs
      LspModels.cs
      LspFraming.cs
      JsonOptions.cs
      DiagnosticStore.cs
    Infrastructure/
      IClock.cs
      FileLoggerProvider.cs
      UserFacingException.cs
tests/
  RoslynMcpServer.Tests/
```

`RoslynMcpServer.csproj`는 app 프로젝트 하나로 시작한다. target framework는 `net10.0`으로 둔다. 이유는 `roslyn-language-server`가 .NET 10 런타임을 요구하므로, MCP 서버와 Roslyn LS의 실행 환경을 맞추는 편이 설치 오류를 줄이기 때문이다.

## 시작 흐름

`Program.cs`는 최대한 얇게 유지한다.

1. `CliOptions.Parse(args)`로 옵션을 읽는다.
2. `--root`가 있으면 해당 경로, 없으면 `Directory.GetCurrentDirectory()`를 root로 정규화한다.
3. `PathGuard`가 root 존재 여부와 디렉터리 여부를 검증한다.
4. DI container에 singleton 서비스들을 등록한다.
5. MCP stdio transport를 시작한다.

`CliOptions`는 기능 옵션뿐 아니라 대규모 repo 보호용 기본 제한도 가진다.

```csharp
public sealed record CliOptions(
    string Root,
    string? RoslynLanguageServerPath,
    LogLevel LogLevel,
    string? LogFile,
    string? LanguageServerLogDirectory,
    TimeSpan StartupTimeout,
    int ScanMaxDepth,
    TimeSpan ScanTimeout,
    int MaxSolutionCandidates,
    int MaxProjectCandidates,
    int MaxOpenDocuments,
    long MaxDocumentBytes,
    int MaxInFlightLspRequests,
    int MaxExpensiveLspRequests);
```

M2 large repo readiness 이후 다음 대형 repo tuning option도 CLI parser에 공개되어 있다.

- `--scan-max-depth`
- `--scan-timeout`
- `--max-solution-candidates`
- `--max-project-candidates`
- `--max-open-documents`
- `--max-document-bytes`
- `--max-in-flight-lsp-requests`
- `--max-expensive-lsp-requests`

예상 형태는 다음과 같다.

```csharp
var options = CliOptions.Parse(args);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkspaceScanner>();
builder.Services.AddSingleton<WorkspaceSession>();
builder.Services.AddSingleton<RoslynLanguageServerLocator>();
builder.Services.AddSingleton<RoslynLanguageServerProcess>();
builder.Services.AddSingleton<LspClient>();
builder.Services.AddSingleton<DocumentPathMapper>();
builder.Services.AddSingleton<DocumentStateManager>();
builder.Services.AddSingleton<DiagnosticStore>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

정확한 MCP SDK API 이름은 구현 시 SDK 버전에 맞춘다. 중요한 점은 MCP tool 메서드가 직접 프로세스를 다루지 않고 `WorkspaceSession`을 통해서만 Roslyn 상태에 접근하게 하는 것이다.

## Workspace 상태 모델

`WorkspaceSession`은 서버 전체의 핵심 상태를 가진다.

```csharp
public sealed record WorkspaceTarget(
    WorkspaceKind Kind,
    string FullPath,
    string RelativePath,
    string RepositoryRoot,
    string WorkspaceDirectory);

public enum WorkspaceKind
{
    Solution,
    SolutionX,
    Project
}

public enum WorkspaceLoadState
{
    NotLoaded,
    StartingLanguageServer,
    LspReady,
    WorkspaceWarming,
    Ready,
    Failed
}
```

`WorkspaceSession` 책임:

- root 기준 `.sln`, `.slnx`, `.csproj` 후보 캐시
- 현재 선택된 `WorkspaceTarget` 관리
- 현재 `WorkspaceLoadState` 관리
- Roslyn LS 시작/중지 요청 조율
- 여러 후보가 있을 때 명시적 선택 요구
- M2+에서 첫 Roslyn tool 호출 시 후보가 하나뿐이면 자동 로드
- 이미 로드된 workspace와 다른 workspace를 로드하면 Roslyn LS를 shutdown 후 재시작

용어를 명확히 구분한다.

- `RepositoryRoot`: MCP 서버가 기준으로 삼는 root. 기본값은 process cwd이고, `--root`로 바꿀 수 있다. 경로 검증과 상대 경로 반환의 기준이다.
- `WorkspaceDirectory`: 선택한 `.sln`, `.slnx`, `.csproj` 파일이 있는 디렉터리. Roslyn LS process working directory 후보로 사용한다.
- `WorkspaceTarget.FullPath`: 선택한 workspace 파일의 절대 경로.
- `WorkspaceTarget.RelativePath`: `RepositoryRoot` 기준 상대 경로.

자동 선택 규칙:

- `.sln` 또는 `.slnx`가 하나만 있으면 우선 선택한다.
- 솔루션 후보가 여러 개면 자동 선택하지 않는다.
- 솔루션이 없고 `.csproj`가 하나만 있으면 선택한다.
- 후보가 없으면 tool 오류에 root와 탐색 패턴을 포함한다.

## Workspace 탐색

`WorkspaceScanner`는 root 아래를 재귀 탐색하되 대규모 mono-repo에서 전체 파일 트리를 매번 훑지 않는다.

M1 이후 구현은 git worktree 안에서는 `GitWorkspaceScanner`를 먼저 사용한다. `GitWorkspaceScanner`는 `git -C <root> ls-files -co --exclude-standard -z -- '*.sln' '*.slnx' '*.csproj'` 결과에서 `.sln`, `.slnx`, `.csproj` 후보만 골라낸다. 이 방식을 우선하는 이유는 `.gitignore` 파일을 MCP 서버가 직접 재구현하는 것보다 git이 이미 알고 있는 ignore 규칙을 쓰는 편이 정확하기 때문이다. `--exclude-standard`는 repository의 `.gitignore`, 하위 `.gitignore`, `.git/info/exclude`, global exclude 규칙을 모두 반영한다. pathspec은 대형 repo에서 workspace 파일이 아닌 전체 git file list를 불필요하게 materialize하지 않기 위한 것이다.

git 기반 탐색이 실패하거나 root가 git worktree 밖이면 기존 bounded recursive scanner로 fallback한다. fallback은 non-git 디렉터리에서도 동작해야 하므로 유지한다. git 기반 결과도 최종적으로 `PathGuard`를 통과시켜 root 밖 경로, reparse point, 존재하지 않는 파일을 방어한다.

filesystem fallback은 solution/project candidate limit이 모두 찬 경우 조기 중단하고 `candidate_limit` truncation reason을 반환할 수 있다.

초기 제외 디렉터리:

- `.git`
- `.vs`
- `bin`
- `obj`
- `node_modules`
- `packages`
- `.idea`
- `.vscode`
- `.cache`
- `.nuke`
- `artifacts`
- `dist`
- `out`
- `target`

이 제외 목록은 git 기반 탐색이 아니라 fallback recursive scanner의 안전망이다. git repository에서는 `.gitignore`와 git exclude 규칙이 더 정확한 후보 필터 역할을 한다.

탐색 결과는 상대 경로와 절대 경로를 모두 가진다. tool 결과에는 상대 경로를 우선 반환하고, 디버깅용으로만 절대 경로를 포함할 수 있다.

대규모 repo 대응 규칙:

- `Directory.EnumerateFileSystemEntries` 기반 streaming 탐색을 사용하고 전체 결과를 먼저 메모리에 올리지 않는다.
- symlink, junction, reparse point 디렉터리는 기본적으로 따라가지 않는다.
- 기본 최대 탐색 깊이를 둔다. 예: root 기준 6단계. `--scan-max-depth`로 조정할 수 있게 한다.
- 기본 시간 예산을 둔다. 예: 3초. 시간 예산을 넘기면 지금까지 찾은 후보와 `truncated: true`를 반환한다.
- 후보 개수 상한을 둔다. 예: solution 100개, project 1000개. 상한을 넘기면 더 깊은 탐색을 멈추고 명시적 경로 입력을 유도한다.
- root 바로 아래와 1단계 하위 디렉터리의 `.sln`/`.slnx`를 먼저 찾는다. 대규모 repo에서는 top-level solution이 가장 유용할 가능성이 높다.
- 탐색 결과는 `WorkspaceSession`에 캐시한다.
- `list_workspaces`는 기본적으로 캐시된 결과를 반환하고, 필요할 때만 `refresh: true` 입력으로 재탐색한다.
- `.csproj` 후보가 매우 많으면 `load_project` 자동 선택은 하지 않는다. 자동 선택은 후보가 작고 명확한 경우에만 적용한다.

경로 입력은 모두 `PathGuard`를 통과해야 한다.

- 상대 경로는 root와 결합 후 `Path.GetFullPath`로 정규화한다.
- 절대 경로도 정규화 후 root 내부인지 확인한다.
- Windows에서는 대소문자 무시 비교를 사용한다.
- root 밖 경로는 거부한다.
- symlink/junction/reparse point를 통해 root 밖으로 나가는 경로는 거부한다.
- 최종 target을 안정적으로 확인할 수 없는 reparse point는 M1에서는 보수적으로 거부한다.

## Roslyn LS 실행 관리

`RoslynLanguageServerLocator`는 실행 파일을 찾는다.

우선순위:

1. `--roslyn-language-server <path>`
2. PATH의 `roslyn-language-server`

찾지 못하면 다음 메시지를 포함한 사용자 오류를 만든다.

```text
roslyn-language-server was not found.
Install it with:
dotnet tool install --global roslyn-language-server --prerelease
```

`RoslynLanguageServerProcess`는 자식 프로세스의 수명만 책임진다.

- `ProcessStartInfo.UseShellExecute = false`
- stdin/stdout/stderr redirect
- working directory는 기본적으로 선택된 workspace의 `WorkspaceDirectory`
- stderr는 로그로 수집하되 MCP stdout에 섞지 않는다.
- shutdown 요청 시 LSP `shutdown`/`exit` 순서로 종료
- 정상 종료가 안 되면 timeout 후 kill

`load_solution` 또는 `load_project`가 호출되면 다음 순서로 처리한다.

1. 입력 경로를 root 내부 경로로 검증한다.
2. `WorkspaceTarget`을 만든다.
3. 기존 Roslyn LS가 있으면 graceful shutdown한다.
4. 새 프로세스를 시작한다.
5. LSP `initialize`를 보낸다.
6. `initialized` notification을 보낸다.
7. readiness 확인을 위해 가벼운 요청을 수행한다.

대규모 solution에서는 `initialize` 완료와 전체 semantic analysis 완료가 같은 의미가 아니다. 따라서 `load_solution`은 "프로세스와 LSP 세션이 준비됨"까지만 보장하고, 전체 프로젝트 로드/진단 완료를 기다리지 않는다.

Roslyn language server는 프로젝트 로딩 중에도 LSP queue가 계속 응답 가능하도록 설계되어 있다. Roslyn의 `LanguageServerProjectLoader`는 design-time build 중 LSP queue를 막지 않아야 한다고 명시하고, 첫 design-time build가 끝나기 전에는 workspace에 primordial project를 추가해 요청을 처리할 수 있게 한다. 이후 프로젝트 로딩이 완료되면 `ProjectInitializationHandler`가 `workspace/projectInitializationComplete` notification을 보낸다.

이 구조 때문에 MCP 서버는 initialize 이후의 요청을 전부 막지 않는다. 대신 project loading 중인 결과를 best-effort로 반환하고, 결과 metadata에 완전성 상태를 표시한다.

로드 상태는 다음 단계로 나눈다.

- `StartingLanguageServer`: 프로세스를 시작하고 initialize 중
- `LspReady`: LSP initialize가 끝나 요청을 받을 수 있음
- `WorkspaceWarming`: Roslyn LS가 프로젝트 로드, restore 상태 반영, diagnostics publish를 진행 중일 수 있음
- `Ready`: `workspace/projectInitializationComplete`를 받았거나, 최근 요청이 정상 처리됐고 warming으로 볼 신호가 없는 상태

`get_workspace_status`는 repository root, 후보 목록, 현재 target, load state, Roslyn LS process 상태, pending request 수, 열린 문서 수, 알려진 diagnostics file count, 마지막 diagnostics update 시각을 반환한다. `diagnostics` 같은 tool은 필요한 범위만 기다리고, workspace 전체가 안정될 때까지 무기한 대기하지 않는다.

Roslyn LS가 특정 solution/project를 명시적으로 선택하는 정확한 방법은 M1 구현 초기에 실제 실행 spike로 확인한다. 아키텍처는 이를 `IRoslynWorkspaceLoader` 인터페이스 뒤로 숨긴다.

Spike에서 확인할 항목:

- `roslyn-language-server --stdio --autoLoadProjects` 실행 가능 여부
- `initialize`의 `rootUri`와 `workspaceFolders`만으로 `.sln`, `.slnx`, `.csproj`가 로드되는지
- working directory를 선택된 workspace 파일의 directory로 두는 것이 필요한지
- solution file path를 전달하는 Roslyn 전용 initialization option 또는 command가 있는지
- `workspace/projectInitializationComplete` notification이 실제로 오는지
- initialize 이후 `document_symbols` 또는 `workspace/symbol`이 동작하는지

Spike 결과는 `docs/architecture.md`에 짧게 기록한 뒤 `IRoslynWorkspaceLoader` 구현에 반영한다.

초기 구현 후보:

- workspace root를 선택한 `.sln`/`.csproj`의 directory로 두고 `--autoLoadProjects` 사용
- `initialize`의 `rootUri`, `workspaceFolders`에 선택된 root 전달
- Roslyn LS가 별도 initialization option이나 command를 요구하면 `IRoslynWorkspaceLoader` 구현만 교체

### 2026-05-16 Roslyn LS spike 결과

Windows 환경의 `roslyn-language-server` global tool에서 다음을 확인했다.

- `roslyn-language-server --stdio --autoLoadProjects`는 정상 실행된다.
- `initialize`에 `rootUri`와 `workspaceFolders`를 workspace directory URI로 넘기면 initialize가 성공한다.
- initialize 직후 `initialized` notification을 보내야 이후 요청을 안정적으로 처리한다.
- Roslyn LS가 client 쪽으로 `workspace/configuration` request를 보낸다. MCP 서버의 LSP client는 알 수 없는 server-to-client request를 방치하지 말고 최소한 `null` 또는 빈 설정 결과로 응답해야 한다.
- `.slnx`와 `.csproj`가 있는 작은 sample에서 root directory를 workspace folder로 둔 경우 `textDocument/didOpen` 뒤 `textDocument/documentSymbol`이 정상 응답했고, `workspace/projectInitializationComplete` notification도 관찰됐다.
- 같은 sample에서 project directory를 workspace folder로 둔 경우 `documentSymbol`은 정상 응답했지만 짧은 관찰 구간에는 `workspace/projectInitializationComplete`가 오지 않았다.
- `workspace/symbol`은 initialize 직후와 warming 중 모두 오류 없이 응답했지만 trivial query에서는 빈 배열을 반환했다. 따라서 M1 loader는 `workspace/symbol` 결과를 readiness 판단으로 사용하지 않는다.
- solution/project 파일 경로를 직접 지정하는 표준 LSP parameter는 확인되지 않았다. M1 구현은 선택된 `.sln`, `.slnx`, `.csproj` 파일을 검증한 뒤 해당 파일의 directory를 Roslyn LS working directory, `rootUri`, `workspaceFolders`로 사용한다. 명시 파일 선택의 더 정밀한 방식이 확인되면 `IRoslynWorkspaceLoader` 내부만 교체한다.

### 2026-05-17 explicit workspace selection 재확인

M2 large repo readiness 단계에서 설치된 `roslyn-language-server 5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`를 다시 확인했다.

- `roslyn-language-server --help`에는 `.sln`, `.slnx`, `.csproj` 파일 경로를 직접 받는 안정적인 CLI option이 없다.
- workspace load에 관련된 공개 옵션은 `--autoLoadProjects`뿐이다.
- 따라서 MCP 서버는 계속 `WorkspaceTarget.FullPath`를 검증과 사용자 상태 표시에는 사용하되, Roslyn LS에는 `WorkspaceTarget.WorkspaceDirectory`를 working directory, `rootUri`, `workspaceFolders`로 전달한다.
- 같은 `WorkspaceDirectory`에 workspace 파일이 여러 개 있으면 `WorkspaceStatus.Warnings`에 `workspace_directory_ambiguous`를 포함해 Roslyn LS auto-load가 directory 단위로만 제어된다는 점을 알린다.

## Agent CLI 사용 계약

이 MCP 서버는 사람이 직접 순서대로 호출하는 API가 아니라 Agent CLI가 계획 중간에 호출하는 도구다. 따라서 "기다리면 언젠가 된다"보다 "현재 무엇을 해야 하는지 명확히 알려주는" 동작이 중요하다.

권장 호출 흐름은 다음과 같다.

1. Agent CLI가 repo root에서 MCP 서버를 실행한다.
2. 필요할 때 `get_workspace_status` 또는 `list_workspaces`를 호출한다.
3. 후보가 하나면 `load_solution`/`load_project`를 호출한다. M2+에서는 첫 navigation tool 호출이 자동 로드를 시도할 수도 있다.
4. 후보가 여러 개면 Agent CLI는 사용자 또는 자체 판단으로 하나를 골라 `load_solution`을 호출한다.
5. `load_solution`이 반환된 뒤 M2+ navigation/diagnostics tool을 호출한다.

`load_solution`/`load_project`는 오래 걸릴 수 있지만, 호출 자체는 LSP initialize 완료까지만 기다린다. 전체 solution 분석, restore 반영, diagnostics 수집이 끝날 때까지 blocking하지 않는다. Agent CLI는 `get_workspace_status`로 warming 상태를 확인할 수 있다.

상태별 tool 동작은 다음과 같다.

| 상태 | 허용 tool | Roslyn navigation/diagnostics tool 동작 |
| --- | --- | --- |
| `NotLoaded` | `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status` | 후보가 하나면 자동 로드 시도, 여러 개면 `workspace_not_loaded` 오류 |
| `StartingLanguageServer` | `get_workspace_status` | 즉시 `workspace_loading` 오류 반환 |
| `LspReady` | 전체 읽기 tool | best-effort 실행 허용 |
| `WorkspaceWarming` | 전체 읽기 tool | best-effort 실행 허용, 결과에 불완전 가능성 표시 |
| `Ready` | 전체 읽기 tool | 정상 실행 |
| `Failed` | `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status` | 실패 원인과 재시도 안내 반환 |

`StartingLanguageServer`에서 navigation tool을 내부 queue에 넣고 기다리게 하지 않는다. Agent CLI 입장에서는 tool 호출이 길게 매달리는 것보다, `workspace_loading`을 받고 잠시 후 재시도하거나 `get_workspace_status`를 호출하는 편이 예측 가능하다.

반대로 `LspReady`와 `WorkspaceWarming`에서는 실패를 최소화한다. 이 상태에서는 Roslyn LS가 요청을 받을 수 있으므로, 결과가 완전하지 않을 수 있더라도 가능한 tool은 best-effort로 실행한다. Agent CLI가 이 MCP 서버를 계속 쓰게 하려면 "아직 완전히 준비되지 않았으니 실패"보다 "현재 가능한 결과와 불완전성 표시"가 낫다.

Best-effort 결과는 공통 metadata를 포함한다.

```json
{
  "workspaceState": "WorkspaceWarming",
  "completeness": "partial",
  "reason": "Workspace is still warming; symbols from projects not loaded yet may be missing.",
  "retryAfterMs": 2000
}
```

`completeness` 값은 다음 중 하나다.

- `complete`: 현재 서버 상태에서 완전한 결과로 판단됨
- `partial`: 일부 프로젝트/진단/심볼이 아직 반영되지 않았을 수 있음
- `unknown`: LSP가 결과 완전성을 판단할 신호를 제공하지 않음

`retryAfterMs`는 완료 시간 추정이 아니라 재시도 간격 힌트다. Agent CLI는 partial 결과를 먼저 사용하고, 작업상 더 높은 확신이 필요할 때만 `get_workspace_status` 또는 같은 tool을 재시도한다.

tool별 warming 중 신뢰도는 다르게 본다.

- `document_symbols`: 파일 단위 구조라 상대적으로 안정적이다.
- `hover`: syntax와 이미 로드된 semantic 정보 범위에서는 동작하지만, 타입/참조 정보가 덜 로드됐으면 빈약할 수 있다.
- `go_to_definition`: 같은 프로젝트나 이미 로드된 참조 범위에서는 동작할 수 있지만, 아직 로드되지 않은 project/metadata 참조는 누락될 수 있다.
- `find_references`: cross-project 결과가 빠질 수 있어 warming 중에는 partial로 표시한다.
- `find_symbols`: 실행은 허용하지만 solution 전체 symbol index가 완성되지 않았을 수 있어 partial 또는 unknown으로 표시한다.
- `diagnostics`: notification이 도착한 파일 기준으로만 신뢰하고, workspace 전체 diagnostics는 `Ready` 전까지 partial로 본다.

자동 로드는 `NotLoaded` 상태에서만 허용한다. 자동 로드 중 다른 tool 호출이 들어오면 다음 정책을 따른다.

- 첫 호출이 자동 로드를 시작한 경우, 그 호출은 initialize 완료 후 원래 요청을 한 번 실행한다.
- 동시에 들어온 다른 Roslyn tool 호출은 queue에 쌓지 않고 `workspace_loading`을 반환한다.
- Agent CLI는 이 오류를 retry 가능한 상태로 취급한다.

오류 예시는 다음과 같다.

```json
{
  "error": "workspace_loading",
  "message": "Workspace is starting. Call get_workspace_status and retry when state is LspReady or WorkspaceWarming.",
  "workspaceState": "StartingLanguageServer",
  "retryAfterMs": 1000
}
```

warming 상태에서의 결과 예시는 다음과 같다.

```json
{
  "workspaceState": "WorkspaceWarming",
  "completeness": "partial",
  "items": [
    {
      "file": "src/App/Program.cs",
      "line": 12,
      "column": 5
    }
  ],
  "truncated": false
}
```

이 계약의 의도는 Agent CLI가 다음 행동을 쉽게 결정하게 하는 것이다. `workspace_loading`은 재시도 가능한 일시 상태이고, `workspace_not_loaded`는 먼저 `load_solution`/`load_project`가 필요한 상태이며, `workspace_failed`는 사용자에게 설치/경로/프로젝트 로드 문제를 보여줘야 하는 상태다.

## LSP 클라이언트

`LspClient`는 JSON-RPC transport와 request correlation을 담당한다.

주요 내부 구조:

```csharp
private long nextId;
private readonly ConcurrentDictionary<long, PendingRequest> pendingRequests = new();
private readonly SemaphoreSlim inFlightLimit;
private readonly SemaphoreSlim expensiveLimit;
private readonly SemaphoreSlim writeLock = new(1, 1);
```

기능:

- `Content-Length` 기반 LSP framing 읽기/쓰기
- request id 생성
- response id와 `TaskCompletionSource` 연결
- notification dispatch
- `workspace/projectInitializationComplete` 수신 처리
- timeout/cancellation 처리
- 프로세스 종료 시 pending request 전체 실패 처리
- pending request 개수 상한
- method별 동시성 제한
- result size 제한과 truncation 표시

LSP 메시지는 `System.Text.Json`을 사용한다. 요청/응답 전체를 거대한 클래스 계층으로 만들지 않고, 많이 쓰는 요청/응답만 typed record로 둔다. 나머지는 `JsonElement`로 받은 뒤 tool별 mapper에서 필요한 필드만 읽는다.

대규모 repo에서 `workspace/symbol`, `textDocument/references`, diagnostics 관련 응답은 커질 수 있다. 현재 `LspClient.RequestAsync`는 timeout, cancellation token, expensive request 여부를 받는다.

```csharp
Task<JsonElement> RequestAsync(
    string method,
    object? parameters,
    TimeSpan timeout,
    CancellationToken cancellationToken,
    bool isExpensive = false);
```

LSP 자체가 `MaxResults`를 지원하지 않는 method도 있으므로, 응답 mapper에서 결과를 잘라내고 `truncated: true`를 tool 결과에 포함한다. LSP message body에는 `LspFraming.DefaultMaxContentLength` 기반 전역 상한을 적용해 과대 payload를 read loop fault로 정리한다.

초기 typed model:

- `InitializeParams`
- `InitializeResult`
- `TextDocumentIdentifier`
- `TextDocumentPositionParams`
- `Location`
- `Range`
- `Position`
- `Hover`
- `Diagnostic`
- `DocumentSymbol`
- `SymbolInformation`

Roslyn 확장 notification:

- `workspace/projectInitializationComplete`
  - 수신 시 `WorkspaceSession`의 상태를 `Ready`로 전환한다.
  - notification이 오지 않더라도 LSP 요청은 계속 best-effort로 처리한다.
  - notification 이름은 Roslyn source의 `ProjectInitializationHandler.ProjectInitializationCompleteName`에 맞춘다.

## 문서 동기화 M2+

Agent CLI는 MCP 서버 밖에서 파일을 수정할 수 있다. LSP 서버는 편집기가 보내는 `didOpen`/`didChange` 이벤트를 기대하므로, tool 호출 전에 파일 상태를 맞춰야 한다.

`DocumentStateManager`는 파일별로 다음 정보를 저장한다.

```csharp
public sealed record OpenDocumentState(
    string Uri,
    string FullPath,
    int Version,
    DateTimeOffset LastWriteTime,
    long Length,
    DateTimeOffset LastAccessedAt);
```

동작:

- 위치 기반 tool 호출 전 `EnsureOpenAsync(path)` 실행
- 처음 보는 파일이면 disk에서 읽고 `textDocument/didOpen` 전송
- 이미 열린 파일이면 last write time/length를 확인
- 변경됐으면 disk에서 다시 읽고 full document `textDocument/didChange` 전송
- tool 입력의 line/column은 1-based로 받고 LSP에는 0-based로 변환

초기 구현은 incremental edit 추적을 하지 않는다. Agent가 외부에서 파일을 바꾸는 환경이므로 full document sync가 단순하고 안전하다.

대규모 repo 대응 규칙:

- 열린 문서는 LRU로 관리하고 기본 상한을 둔다. 예: 200개.
- 상한을 넘으면 오래 쓰지 않은 문서에 `textDocument/didClose`를 보낸다.
- 매우 큰 파일은 기본적으로 열지 않는다. 예: 2MB 초과 파일은 사용자 오류로 처리하고 `--max-document-bytes`로 조정할 수 있게 한다.
- 파일 변경 확인은 last write time/length를 먼저 보고, 필요한 경우에만 내용을 읽는다.
- full document sync는 단순하지만 큰 파일에서 비싸므로, 변경 작업 tool을 추가하기 전까지는 위치 기반 read tool에 필요한 파일만 연다.

## MCP Tool 설계

MCP tool 클래스는 얇게 유지한다. 입력 검증과 출력 DTO 변환은 tool layer에서 하되, Roslyn/LSP 상태 변경은 `WorkspaceSession`에 위임한다.

### WorkspaceTools

- `list_workspaces`
  - 입력: optional `refresh`
  - root, solution 후보, project 후보 반환
  - `refresh: false`이면 캐시된 후보를 반환하고, `refresh: true`이면 scan 제한 안에서 다시 탐색

- `load_solution`
  - 입력: `path`
  - `.sln` 또는 `.slnx`만 허용
  - 성공 시 로드된 target과 Roslyn LS 상태 반환

- `load_project`
  - 입력: `path`
  - `.csproj`만 허용
  - 성공 시 로드된 target과 Roslyn LS 상태 반환

- `get_workspace_status`
  - root, 후보 목록, 현재 target, load state, Roslyn LS process 상태 반환

### NavigationTools M2+

- `document_symbols`
  - 입력: `file`
  - LSP: `textDocument/documentSymbol`

- `hover`
  - 입력: `file`, `line`, `column`
  - LSP: `textDocument/hover`

- `go_to_definition`
  - 입력: `file`, `line`, `column`
  - LSP: `textDocument/definition`

- `find_references`
  - 입력: `file`, `line`, `column`, `includeDeclaration`
  - LSP: `textDocument/references`

- `find_symbols`
  - 입력: `query`, optional `maxResults`
  - LSP: `workspace/symbol`
  - 기본 결과 상한을 둔다. 예: 100개.
  - `LspReady`/`WorkspaceWarming`에서도 best-effort로 실행한다.
  - warming 중이면 결과에 `completeness: "partial"` 또는 `unknown`을 포함한다.
  - 결과가 비어 있어도 이것만으로 "심볼이 없다"고 단정하지 않도록 `reason`을 함께 반환한다.

### DiagnosticsTools M2+

- `diagnostics`
  - 입력: optional `file`, optional `severity`, optional `maxResults`
  - file이 있으면 해당 문서를 open/sync하고 마지막 diagnostics 반환
  - file이 없으면 `DiagnosticStore`의 요약과 제한된 결과만 반환

진단은 LSP `textDocument/publishDiagnostics` notification을 받아 저장한다. Roslyn LS가 diagnostics를 늦게 보내는 경우가 있으므로, `diagnostics` tool은 짧은 settle timeout 옵션을 둘 수 있다.

대규모 solution에서 workspace 전체 diagnostics를 무제한 반환하지 않는다.

- 기본은 현재 열린 문서와 최근 diagnostics가 들어온 파일 중심으로 반환한다.
- workspace 전체 요청은 `scope: "workspace"`처럼 명시적으로 구분한다.
- `scope: "workspace"`도 기본 `maxResults`를 둔다. 예: 200개.
- 결과에는 `totalKnown`, `returned`, `truncated`, `lastUpdatedAt`을 포함한다.
- `DiagnosticStore`는 파일별 diagnostics를 bounded cache로 저장한다. 오래된 항목은 요약 카운트만 남기고 상세 목록은 버릴 수 있다.

## Tool 결과 포맷

Agent가 쓰기 쉽게 모든 경로는 기본적으로 root 상대 경로로 반환한다.

위치는 다음 형태를 사용한다.

```json
{
  "file": "src/App/Program.cs",
  "line": 12,
  "column": 5
}
```

line/column은 tool 입출력 모두 1-based로 통일한다. LSP와의 0-based 변환은 내부에서만 한다.

여러 결과를 반환하는 tool은 불필요한 원문 LSP payload를 그대로 노출하지 않고, 필요한 필드만 정리한다.

```json
{
  "symbol": "MyService.DoWork",
  "kind": "method",
  "location": {
    "file": "src/App/MyService.cs",
    "line": 34,
    "column": 17
  }
}
```

대량 결과 tool은 공통 metadata를 붙인다.

```json
{
  "items": [],
  "totalKnown": 1532,
  "returned": 100,
  "truncated": true,
  "nextCursor": "optional"
}
```

초기 구현은 실제 cursor 기반 pagination이 어려운 LSP method에서는 `nextCursor`를 생략한다. 대신 `maxResults`와 더 좁은 query를 유도하는 메시지를 포함한다.

## 오류 처리

사용자가 바로 고칠 수 있는 문제는 `UserFacingException`으로 표현한다.

예시:

- Roslyn LS 미설치
- workspace 후보가 여러 개라 명시적 선택 필요
- 파일이 root 밖에 있음
- 파일이 존재하지 않음
- Roslyn LS initialize timeout
- 요청 결과가 너무 큼
- workspace scan이 시간 또는 개수 상한에 걸림

내부 버그나 예상하지 못한 LSP 응답은 로그에는 상세히 남기고, MCP tool 결과에는 짧은 오류와 correlation id만 반환한다.

오류 메시지는 가능하면 다음 행동을 포함한다.

```text
Multiple solutions were found. Call load_solution with one of:
- App.sln
- Samples/Samples.sln
```

## 동시성

MCP 클라이언트가 tool을 병렬 호출할 수 있으므로 상태 변경은 직렬화한다.

- `WorkspaceSession`은 `SemaphoreSlim _stateLock`으로 load/restart를 보호한다.
- `LspClient` write는 `writeLock`으로 한 번에 하나만 쓴다.
- read loop는 단일 background task로 유지한다.
- 여러 읽기 tool은 같은 LSP process에 병렬 request를 보낼 수 있다.
- `load_solution`/`load_project` 실행 중에는 다른 Roslyn tool을 queue에 넣지 않고 즉시 `workspace_loading`을 반환한다.

대규모 repo에서는 병렬 요청이 Roslyn LS를 쉽게 포화시킬 수 있으므로 기본 제한을 둔다.

- 전체 LSP in-flight request 상한. 예: 16개.
- 비싼 method 상한. 예: `workspace/symbol`, `textDocument/references`, diagnostics 관련 요청은 2개.
- load/restart 중에는 새 navigation request를 받지 않는다.
- timeout은 method별 기본값을 둔다.
  - hover/document symbols: 10초
  - definition/references: 30초
  - workspace symbol: 30초
  - load initialize: 60초 이상
- MCP tool cancellation이 들어오면 LSP `$/cancelRequest`를 보낸다.

## 로깅

MCP stdio는 프로토콜 채널이므로 일반 로그를 stdout에 쓰면 안 된다.

- 기본 로그는 stderr로 보낸다.
- `--log-file`이 있으면 파일에도 쓴다.
- Roslyn LS stderr는 별도 logger category로 남긴다.
- LSP request/response body 전체 로깅은 기본 비활성화한다.
- trace 모드에서만 method, id, duration, payload size를 남긴다.
- 로그 파일을 사용할 경우 크기 제한과 rolling 정책을 둔다.

## 테스트 전략

### 단위 테스트

M1 단위 테스트:

- `PathGuard` root escape 방지
- `WorkspaceScanner` 후보 탐색과 제외 디렉터리 처리
- `WorkspaceScanner` 시간/개수 상한과 `truncated` 처리
- LSP `Content-Length` framing parser
- JSON-RPC request/response correlation
- `WorkspaceSession` 기본 상태 전이

M2+ 단위 테스트:

- line/column 1-based/0-based 변환
- `DocumentStateManager` didOpen/didChange 판단
- `DocumentStateManager` LRU didClose 처리
- `DiagnosticStore` maxResults/truncation 처리

### 통합 테스트

현재 Roslyn LS integration smoke는 `tests/RoslynMcpServer.Tests` 안에 작은 temporary sample solution을 구성해서 실행한다. 별도 integration test project는 아직 없다.

M1 통합 테스트 검증 흐름:

1. fixture root에서 서버 서비스를 직접 구성한다.
2. `list_workspaces`가 sample solution을 찾는지 확인한다.
3. `load_solution`이 Roslyn LS를 시작하고 initialize하는지 확인한다.
4. `get_workspace_status`가 target과 load state를 반환하는지 확인한다.
5. Roslyn LS가 설치되지 않은 환경에서는 통합 테스트를 skip하고, skip 이유에 설치 명령을 출력한다.

M2+ 통합 테스트 검증 흐름:

1. `document_symbols`, `hover`, `go_to_definition`, `find_references`, `find_symbols`를 호출한다.
2. 결과 경로와 line/column이 root 상대 및 1-based인지 확인한다.
3. `diagnostics` publish smoke는 현재 Roslyn LS fixture에서 도착 시점이 환경 의존적이라 안정적인 settle 전략이 정해질 때까지 skip/deferred 테스트로 둔다.

대규모 repo 회귀 테스트는 실제 거대 solution을 fixture로 넣지 않는다. 대신 많은 디렉터리와 project 파일을 생성하는 synthetic fixture로 scanner 상한, 캐시, truncation, 명시적 load 요구를 검증한다. Roslyn LS 통합 테스트는 작은 sample solution으로 유지한다.

## 구현 순서

M0/M1과 M2 read-only tool 구현은 완료된 상태다. 아래 목록은 구현 이력과 현재 후속 작업 기준을 함께 나타낸다.

1. solution/project 골격 생성
2. `net10.0` app project 설정
3. CLI 옵션, root 검증, logging 구성
4. MCP hello/status tool
5. workspace scanner와 `list_workspaces`
6. scanner cache, timeout, truncation
7. Roslyn LS locator와 미설치 오류
8. Roslyn LS 실제 동작 spike 수행 및 결과 기록
9. process start/stop과 LSP initialize/shutdown
10. `IRoslynWorkspaceLoader` 구현
11. `load_solution`, `load_project`, `get_workspace_status`
12. LSP framing과 request/response 테스트
13. request concurrency limit와 cancellation
14. `README.md`는 짧은 영어 소개로 유지
15. M1 범위 사용성 오류 메시지 정리
16. M2a read-tool foundation, document sync, `document_symbols`, `hover`
17. M2b `go_to_definition`, `find_references`
18. M2c `find_symbols`
19. M2d `diagnostics`, `DiagnosticStore`
20. M2 large repo readiness 일부: explicit workspace warning, LSP fault handling, scanner hardening, CLI tuning

후속 구현 후보:

- diagnostics notification offload
- 실제 MCP client smoke
- opt-in large repo 검증
- `solution_overview` 필요성 재평가
- 사용자용 설치/설정 문서 정리

## 열어둘 결정

- MCP 서버 자체의 사용자 배포 채널
- 변경 작업 tool을 어떤 opt-in 옵션으로 열지
- 대형 solution에서 diagnostics settle timeout 기본값
- workspace scan 기본 depth/time/result 상한
- method별 timeout과 in-flight request 상한

## 참고 코드

- Roslyn `LanguageServerProjectLoader`: https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/LanguageServerProjectLoader.cs
  - design-time build 중 LSP queue responsive 유지
  - first design-time build 전 primordial project로 요청 처리
  - background project reload queue

- Roslyn `ProjectInitializationHandler`: https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/ProjectInitializationHandler.cs
  - `workspace/projectInitializationComplete` notification 전송
  - project initialization 완료 관찰

# Roslyn MCP Server 계획

## 목표

이 프로젝트는 `roslyn-language-server`를 Agent CLI류에서 사용할 수 있는 MCP Server로 감싸는 것을 목표로 한다.

핵심 조건은 다음 두 가지다.

- 사용자는 `roslyn-language-server`를 별도로 명시적으로 설치한다.
- 구현 언어는 C#/.NET을 기본으로 한다.

## 기본 방향

MCP 서버는 C#으로 작성하고, MCP 프로토콜 구현에는 공식 `modelcontextprotocol/csharp-sdk`를 사용한다. 2026-05-16 기준 MCP 공식 문서에서 C# SDK는 Tier 1 SDK로 분류되어 있고, SDK 저장소도 C# 서버/클라이언트 구현을 공식 지원한다.

Roslyn 기능은 가능한 한 `roslyn-language-server` 프로세스를 실행해서 LSP로 통신하는 방식으로 붙인다. NuGet의 `roslyn-language-server.*` 패키지는 C#용 LSP 구현이며 VS Code C# 확장 및 C# Dev Kit에도 사용되는 서버라고 설명되어 있다. 따라서 MCP 서버는 Roslyn 내부 API를 직접 재구현하기보다 LSP 클라이언트 역할을 수행하면서 MCP 도구 호출을 LSP 요청으로 변환한다.

## 제품 형태

프로젝트 이름은 `roslyn-mcp-server`로 유지한다. 다만 NuGet/.NET global tool에는 MCP 서버를 게시하지 않고, 공식 제품처럼 보일 수 있는 패키지명을 피한다.

초기 제품 형태는 다음 원칙을 따른다.

1. MCP 서버
   - 프로젝트 이름과 실행 파일 이름은 `roslyn-mcp-server`로 둔다.
   - NuGet global tool로 게시하지 않는다.
   - 구체적인 사용자 배포 채널은 구현이 어느 정도 안정된 뒤 결정한다.

2. 필수 외부 도구: Roslyn language server
   - `dotnet tool install --global roslyn-language-server --prerelease`
   - Roslyn language server는 사용자가 명시적으로 설치한다.
   - 이 경로는 사용자가 .NET 런타임/SDK를 갖고 있어야 한다.

3. 개발자용: 소스 빌드
   - `dotnet build`
   - 기여자와 로컬 검증용으로만 문서화한다.

## 설치 UX 원칙

`README.md`는 GitHub 첫 화면용 짧은 영어 소개로 둔다. 구현 agent용 지침은 `AGENTS.md`와 `docs/implementation-notes.md`에 한국어로 둔다.

나중에 사용자용 설치 문서를 작성할 때는 `roslyn-language-server` 설치 명령과 MCP 클라이언트 설정 예시를 중심으로 구성한다. MCP 서버 자체의 배포 방식은 NuGet global tool을 사용하지 않는다는 점을 명확히 한다.

예상 MCP 클라이언트 설정 예시는 다음과 같다. 기본 설정에는 workspace 경로를 넣지 않는다. MCP 클라이언트가 서버를 띄운 현재 작업 디렉터리를 기준으로 동작하고, 실제 솔루션/프로젝트 로드는 MCP tool로 수행한다.

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\tools\\roslyn-mcp-server\\roslyn-mcp-server.exe"
    }
  }
}
```

Unix 계열 예시는 다음과 같다.

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/usr/local/bin/roslyn-mcp-server"
    }
  }
}
```

필요한 경우에만 `--root <path>`를 escape hatch로 제공한다. 일반 사용자는 MCP 클라이언트가 repo root에서 서버를 실행하도록 설정하는 것으로 충분해야 한다.

## 아키텍처

### 프로세스 구성

```text
Agent CLI
  <-> MCP stdio
roslyn-mcp-server
  <-> LSP stdio
roslyn-language-server
  <-> cwd 기준 .sln/.slnx/.csproj workspace
```

`roslyn-mcp-server`는 MCP stdio 서버로 실행된다. 서버 시작 시 현재 작업 디렉터리를 기본 root로 삼고, `.sln`, `.slnx`, `.csproj` 후보를 탐색한다. 이 단계에서는 무조건 솔루션을 로드하지 않는다. Agent가 `load_solution` 또는 `load_project` tool을 호출하거나, 후보가 하나뿐인 상태에서 Roslyn tool이 처음 호출될 때 로드한다.

내부에서는 `roslyn-language-server --stdio`를 자식 프로세스로 띄우고, LSP initialize 뒤 선택된 `.sln`/`.slnx`에는 `solution/open`, 선택된 `.csproj`에는 `project/open` notification을 보내며, 필요한 LSP 요청/응답 관리를 담당한다.

### 주요 컴포넌트

- `Program.cs`
  - CLI 옵션 파싱
  - MCP 서버 시작
  - 현재 작업 디렉터리 또는 `--root` 경로 검증

- `RoslynLanguageServerProcess`
  - `roslyn-language-server` 실행 파일 탐색
  - stdio 연결
  - 프로세스 수명 관리
  - 로그 수집

- `LspClient`
  - JSON-RPC 2.0 메시지 송수신
  - `initialize`, `initialized`, `shutdown`, `textDocument/*`, `workspace/*` 요청 처리
  - timeout, cancellation, diagnostics 이벤트 처리

- `WorkspaceSession`
  - 현재 root, 발견된 솔루션/프로젝트 후보, 로드된 workspace 상태 관리
  - 문서 URI 변환
  - 열려 있는 파일 상태 추적
  - 여러 솔루션/프로젝트 후보가 있을 때 명시적 선택 요구

- `McpTools`
  - Agent가 호출할 MCP tool 정의
  - 각 tool을 LSP 요청으로 변환

## MCP 도구 로드맵

2026-05-17 현재 `main` 기준으로 M0/M1 workspace tool과 M2 read-only tool이 구현되어 있다. 현재 제공하는 workspace tool은 다음과 같다.

- `list_workspaces`
  - 현재 root 기준 발견된 `.sln`, `.slnx`, `.csproj` 후보 반환

- `load_solution`
  - 특정 `.sln` 또는 `.slnx` 파일을 현재 Roslyn workspace로 로드

- `load_project`
  - 특정 `.csproj` 파일을 현재 Roslyn workspace로 로드

- `get_workspace_status`
  - 현재 root, 발견된 후보, 로드된 workspace, Roslyn LS 준비 상태 반환

M2에서 구현된 읽기 중심 기능은 다음과 같다.

- `find_symbols`
  - 이름 또는 패턴으로 심볼 검색

- `go_to_definition`
  - 파일, 라인, 컬럼 기준 정의 위치 반환

- `find_references`
  - 심볼 참조 목록 반환

- `hover`
  - Roslyn hover 정보를 반환

- `diagnostics`
  - 현재 솔루션/파일의 컴파일 및 분석 진단 반환

- `document_symbols`
  - 파일 내 클래스, 메서드, 속성 등의 구조 반환

아직 구현하지 않은 읽기/요약 기능은 후속 milestone로 남긴다.

- `solution_overview`
  - 로드된 솔루션/프로젝트, 프로젝트 목록, 타깃 프레임워크 요약

2차 버전에서 변경 작업을 추가한다.

- `rename_symbol`
- `code_actions`
- `apply_code_action`
- `format_document`

변경 작업은 MCP 클라이언트가 파일을 직접 수정하는 방식과 충돌할 수 있으므로, 1차 버전에서는 읽기 전용 도구로 안정성을 먼저 확보한다.

## Roslyn language server 확보 전략

`roslyn-language-server`는 MCP 서버에 기본 포함하지 않는다. 사용자가 다음 명령을 명시적으로 실행하도록 문서화한다.

```text
dotnet tool install --global roslyn-language-server --prerelease
```

이 방식은 설치 단계가 하나 늘지만, Roslyn LS의 버전과 런타임 요구사항을 사용자가 명확히 인식할 수 있고 MCP 서버 구성을 단순하게 유지할 수 있다. MCP 서버 자체는 NuGet global tool에는 게시하지 않는다.

런타임에서 language server 탐색 우선순위는 다음과 같다.

1. `--roslyn-language-server <path>` 명시 경로
2. PATH의 `roslyn-language-server`

MCP 서버 시작 또는 첫 Roslyn tool 호출 시 `roslyn-language-server`를 찾을 수 없으면, 빌드 명령 대신 설치 명령을 포함한 오류를 반환한다.

주의할 점은 현재 `roslyn-language-server`가 prerelease 패키지이며, NuGet 설명 기준 .NET 10.0 이상 런타임을 요구한다는 점이다. 설치 문서에 이 요구사항을 명확히 적는다.

## CLI 옵션

현재 CLI 옵션은 기본 실행 옵션과 대규모 repo tuning 옵션을 함께 제공한다.

```text
roslyn-mcp-server
  --root <path>                      기준 디렉터리. 기본값은 현재 작업 디렉터리
  --roslyn-language-server <path>     외부 Roslyn LS 경로
  --log-level <level>                 trace|debug|info|warn|error
  --log-file <path>                   MCP 서버 로그 파일
  --ls-log-dir <path>                 Roslyn LS 로그 디렉터리
  --startup-timeout <seconds>         LSP initialize timeout
  --scan-max-depth <depth>             workspace scan 최대 깊이
  --scan-timeout <seconds>             workspace scan timeout
  --max-solution-candidates <count>    solution 후보 최대 개수
  --max-project-candidates <count>     project 후보 최대 개수
  --max-open-documents <count>         LSP open document cache 상한
  --max-document-bytes <bytes>         read tool이 열 수 있는 단일 문서 크기 상한
  --max-in-flight-lsp-requests <count> 전체 LSP in-flight request 상한
  --max-expensive-lsp-requests <count> expensive LSP request 상한
```

`--root`는 필수가 아니다. 일반적인 Agent CLI 설정에서는 args 없이 서버를 실행하고, 서버는 실행된 디렉터리를 기준으로 workspace 후보를 찾는다.

workspace 선택 규칙은 다음과 같다.

- `.sln` 또는 `.slnx`가 하나만 있으면 첫 Roslyn tool 호출 시 자동 로드할 수 있다.
- 솔루션 후보가 여러 개면 `load_solution` 호출을 요구한다.
- 솔루션은 없고 `.csproj`가 하나만 있으면 `load_project`로 자동 로드할 수 있다.
- 후보가 없으면 현재 root와 탐색 결과를 포함한 명확한 오류를 반환한다.

## 테스트 계획

### 단위 테스트

- LSP 메시지 framing
- JSON-RPC request/response correlation
- URI와 파일 경로 변환
- MCP tool 입력 검증
- timeout/cancellation 처리

### 통합 테스트

샘플 C# 솔루션을 테스트 fixture로 둔다.

- 솔루션 로드 성공
- 정의 찾기 결과 위치 검증
- 참조 찾기 개수 검증
- 진단 결과 검증
- 존재하지 않는 파일/심볼 오류 메시지 검증

### 실행 테스트

현재 결정된 실행 전제를 기준으로 사용자 흐름을 검증한다.

- `dotnet tool install --global roslyn-language-server --prerelease` 후 PATH 탐색 성공 확인
- Agent CLI 설정 JSON 예시로 MCP handshake 성공 확인
- cwd 기준 workspace 후보 탐색 확인

## 보안 및 안정성

- MCP tool 입력으로 받은 경로는 workspace 내부인지 검증한다.
- 기본적으로 읽기 전용 tool만 제공한다.
- 변경 작업 tool은 명시적으로 enable해야 사용할 수 있게 한다.
- 자식 프로세스 종료, timeout, stderr 로그를 구조화해서 보고한다.
- Roslyn LS가 죽으면 MCP tool 호출은 명확한 오류를 반환하고 재시작 정책을 적용한다.

## 단계별 마일스톤

M0/M1과 M2는 완료된 상태로 본다. Target framework는 `net10.0`으로 시작했고, `load_solution`/`load_project`를 완성하기 전에 `roslyn-language-server`를 실제로 실행해 stdio LSP initialize와 workspace 로드 방식을 확인한 결과가 구현에 반영되어 있다.

### M0: 프로젝트 골격 - 완료

- C# solution 생성
- `net10.0` app project 생성
- MCP C# SDK 연결
- stdio MCP 서버 시작
- `README.md`는 짧은 영어 소개로 유지

### M1: Roslyn LS 프로세스 연결 - 완료

- `roslyn-language-server --stdio` 실행 및 `solution/open`/`project/open` notification 전송
- Roslyn LS 실제 동작 spike로 `.sln`/`.slnx`/`.csproj` 로드 방식 확인
- LSP initialize/shutdown 구현
- 로그 및 timeout 처리
- cwd/`--root` 기준 workspace 후보 탐색
- `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status` 구현

### M2: 읽기 전용 도구 - 완료

- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`
- `DiagnosticStore`
- 문서 open/sync, result limit, expensive request limit, warming metadata
- M2 large repo readiness 일부: explicit workspace selection 제약 warning, LSP fault handling, scanner hardening, CLI tuning

### M3: 사용성 정리 - 다음 후보

- Roslyn LS 별도 설치 안내 작성
- Agent CLI 설정 예시 작성
- `roslyn-language-server` 미설치 시 오류 메시지 정리
- 실제 MCP client smoke
- `solution_overview` 필요성 재평가
- 배포 채널은 구현 안정화 뒤 별도 결정

### M4: 품질 강화

- 통합 테스트 추가
- 대형 솔루션 startup 성능 측정
- Roslyn LS crash/restart 처리
- 오류 메시지 정리
- diagnostics notification offload
- opt-in real repo 검증과 default tuning

### M5: 변경 작업 도구

- code action 미리보기
- rename preview
- opt-in 방식의 apply 기능

## 주요 리스크

- `roslyn-language-server` 패키지가 prerelease라 CLI 옵션이나 실행 방식이 바뀔 수 있다.
- 사용자가 `roslyn-language-server`를 별도로 설치해야 하므로, 설치 오류 메시지와 README 안내 품질이 중요하다.
- `roslyn-language-server`의 .NET 10 런타임 요구사항 때문에 사용자의 로컬 환경 준비가 필요할 수 있다.
- LSP 서버는 에디터 클라이언트를 전제로 하므로 Agent CLI 사용 패턴에 맞춘 파일 open/change 이벤트 관리가 필요하다.
- 대형 솔루션에서 초기 로딩 시간이 길 수 있으므로 startup progress와 timeout 정책이 중요하다.

## 참고 링크

- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- MCP SDK 목록: https://modelcontextprotocol.io/docs/sdk
- Roslyn repository: https://github.com/dotnet/roslyn
- roslyn-language-server NuGet: https://www.nuget.org/packages/roslyn-language-server.win-x64/5.8.0-1.26252.1

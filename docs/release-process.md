# 릴리즈 프로세스

이 문서는 `roslyn-mcp-server`의 릴리즈 절차를 고정한다. 현재 릴리즈는
GitHub Release와 GitHub Actions `publish.yml`이 생성하는 platform별 artifact를
기준으로 한다. NuGet/.NET global tool package는 `Kirnot.RoslynMcpServer`
패키지 ID와 `roslyn-mcp-server` command name으로 별도 준비한다.

## 릴리즈 기준

- 기본 브랜치: `main`
- 태그 형식: `v0.x.y`
- 첫 릴리즈 버전: `v0.1.0`
- 릴리즈 위치: GitHub Releases, NuGet.org
- 릴리즈 산출물: platform별 self-contained single-file `dotnet publish` 결과 archive,
  NuGet/.NET global tool package
- 필수 검증: format, build, test, 실제 MCP client/repo smoke test

`roslyn-language-server`는 릴리즈 zip에 번들하지 않는다. 사용자는 계속 별도로
설치해야 한다.

```text
dotnet tool install --global roslyn-language-server --prerelease
```

## 버전 정책

`1.0.0` 전까지는 `0.x.y`를 사용한다. `x`는 사용자-facing 변경, `y`는
호환되는 패치 변경을 뜻한다.

`x`를 올리는 경우:

- MCP tool 추가, 삭제, 이름 변경
- MCP tool signature 변경
- 응답 shape 또는 metadata 변경
- 기본 startup/load/diagnostics 정책 변경
- 기본 옵션값 변경
- release artifact 구성 변경
- 사용자가 체감하는 동작 변경

`y`를 올리는 경우:

- 버그 수정
- 문서 수정
- 테스트 추가 또는 보강
- 로그, 오류 메시지, 관측성 개선
- tool 계약을 바꾸지 않는 성능 개선
- 내부 리팩터링

예시:

```text
0.1.0  첫 수동 릴리즈
0.1.1  diagnostics queue 버그 수정
0.1.2  문서 또는 smoke script 보완
0.2.0  tool option 추가 또는 응답 metadata 변경
0.3.0  multi-OS release artifact 구성 변경
0.4.0  NuGet/.NET global tool package 첫 배포
```

이미 push된 태그는 움직이지 않는다. 릴리즈 후 수정이 필요하면 새 patch 버전을
낸다.

## Artifact 정책

릴리즈 artifact는 GitHub Actions의 대상 OS runner에서 self-contained single-file
publish 결과로 만든다. MCP 서버 실행 파일 자체는 대상 환경의 별도 .NET runtime
설치를 요구하지 않는다. 단, `roslyn-language-server`는 릴리즈 artifact에 포함하지
않으며 사용자가 별도로 설치해야 한다.

기본 artifact:

```text
roslyn-mcp-server-v0.x.y-win-x64.zip
roslyn-mcp-server-v0.x.y-linux-x64.tar.gz
roslyn-mcp-server-v0.x.y-osx-x64.tar.gz
roslyn-mcp-server-v0.x.y-osx-arm64.tar.gz
```

archive에는 MCP 서버 실행 파일, `LICENSE`, 그리고 .NET publish 출력에 필요한
파일만 포함한다. single-file publish에서는 일반적으로 `roslyn-mcp-server` 또는
`roslyn-mcp-server.exe`와 `LICENSE`만 포함된다. 다음 항목은 포함하지 않는다.

- `roslyn-language-server`
- repository clone
- `.local/` 아래 smoke 결과, 로그, 임시 파일
- secrets 또는 개인 MCP client 설정

Unix 계열 artifact는 실행 권한이 중요하므로 대상 OS runner에서 만들고 압축한다.
release note와 설치 문서에는 필요 시 `chmod +x roslyn-mcp-server` 안내를 포함한다.

## NuGet tool package 정책

NuGet package ID는 `Kirnot.RoslynMcpServer`로 고정한다. .NET tool command name은
기존 executable 이름과 같은 `roslyn-mcp-server`로 유지한다.

```text
dotnet tool install --global Kirnot.RoslynMcpServer
roslyn-mcp-server --help
```

NuGet tool package는 framework-dependent package다. platform별 GitHub Release
artifact처럼 self-contained native archive가 아니므로, 사용자는 .NET 10
runtime/SDK 환경을 갖추고 있어야 한다. `roslyn-language-server`는 NuGet tool
package에도 번들하지 않으며 계속 별도로 설치한다.

NuGet API key나 local package output은 repository에 commit하지 않는다. NuGet
publish 자동화는 별도 작업으로 명시적으로 결정하기 전까지 추가하지 않는다.

## 릴리즈 전 준비

현재 위치가 repository root인지 확인한다.

```powershell
git status --short --branch
git remote -v
```

기준 remote는 다음 URL이어야 한다.

```text
https://github.com/kirnot92/roslyn-mcp-server
```

릴리즈는 `main`에서 수행한다. 릴리즈 브랜치는 아직 사용하지 않는다.

```powershell
git switch main
git pull --ff-only origin main
git status --short
```

작업 tree는 비어 있어야 한다. 릴리즈에 포함할 문서나 코드 변경이 있다면 먼저
리뷰 가능한 commit으로 정리한다.

## 릴리즈 노트 준비

릴리즈 노트는 사용자-facing 변경을 먼저 적는다. 내부 구현 변경은 사용자에게
영향이 있을 때만 짧게 포함한다.

형식:

```markdown
## v0.x.y

### Added

### Changed

### Fixed

### Known issues

### Validation

- `dotnet format ... --verify-no-changes`
- `dotnet build ...`
- `dotnet test ...`
- Smoke: `<repo/client/script>`
```

`Known issues`에는 릴리즈 시점의 실제 제약을 적는다. 예를 들어
`roslyn-language-server`가 prerelease dependency라는 점, 대형 repository에서
`WorkspaceWarming`이 오래 지속될 수 있다는 점, diagnostics가 full build 결과가
아니라는 점을 포함할 수 있다.

## 필수 검증

릴리즈 태그를 만들기 전에 다음 검증을 모두 통과해야 한다.

```powershell
$Version = "0.4.0"
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
dotnet pack src\RoslynMcpServer\RoslynMcpServer.csproj -c Release -o .local\nuget-pack -p:Version=$Version -p:InformationalVersion=$Version
dotnet tool install --tool-path .local\tool-check --add-source .local\nuget-pack Kirnot.RoslynMcpServer --version $Version
.local\tool-check\roslyn-mcp-server.exe --help
```

Unix 환경에서 local tool을 검증한다면 마지막 명령은
`.local/tool-check/roslyn-mcp-server --help`를 사용한다.

실제 MCP client/repo smoke test도 필수다. 실행 방식은
`docs/smoke-test-guide.md`를 따른다. 릴리즈마다 최소 하나의 실제 repository와
MCP stdio 흐름을 확인한다.

예시:

```powershell
python scripts/smoke-tests/mcp_powershell_smoke.py
```

대형 repository 관련 변경이 포함된 릴리즈라면 ASP.NET Core 또는 동급 대형
repository smoke test를 추가로 실행한다.

```powershell
python scripts/smoke-tests/mcp_aspnetcore_smoke.py
```

검증이 실패하면 태그를 만들지 않는다. 실패 원인을 수정한 뒤 다시 검증한다.

## Artifact 생성

artifact는 GitHub Actions `Publish` workflow로 생성한다. 태그 없이 artifact 생성만
검증하려면 수동 실행을 사용한다.

```powershell
gh workflow run publish.yml --ref main -f version=0.4.0
```

수동 실행은 GitHub Actions artifact만 업로드하고 GitHub Release를 만들지 않는다.
workflow가 각 artifact에서 `roslyn-mcp-server --help`를 실행해 기본 실행 가능성을
확인한다.

NuGet tool package는 현재 로컬 `dotnet pack`으로 검증한다. NuGet.org에 배포할
때는 GitHub Release 생성과 artifact 확인이 끝난 뒤 같은 버전의 `.nupkg`를
수동으로 push한다.

```powershell
$Version = "0.4.0"
dotnet pack src\RoslynMcpServer\RoslynMcpServer.csproj -c Release -o .local\nuget-pack -p:Version=$Version -p:InformationalVersion=$Version
dotnet nuget push ".local\nuget-pack\Kirnot.RoslynMcpServer.$Version.nupkg" --source https://api.nuget.org/v3/index.json --api-key <NUGET_API_KEY>
```

## 태그 생성

검증과 artifact 확인이 끝난 뒤 annotated tag를 만든다. `v*` tag push는
`publish.yml`을 실행하고, 모든 platform artifact가 성공하면 GitHub Release를
생성한다.

```powershell
$Version = "0.1.0"
git tag -a "v$Version" -m "Release v$Version"
```

태그 생성 후 한 번 더 확인한다.

```powershell
git status --short --branch
git show --stat "v$Version"
```

## Push

`main`과 태그를 push한다.

```powershell
git push origin main
git push origin "v$Version"
```

인증 또는 권한 문제로 push가 실패하면 force push나 history rewrite를 하지
않는다. 로컬 변경, commit, tag를 보존하고 사용자에게 실패 상태를 보고한다.

## GitHub Release 작성

`v*` tag push 뒤 `Publish` workflow가 GitHub Release를 만든다.

- tag: `v0.x.y`
- title: `v0.x.y`
- body: workflow가 생성한 릴리즈 노트. 필요하면 생성 뒤 GitHub Release 본문만 보완한다.
- assets: workflow가 생성한 platform별 artifact 전체

workflow 실패나 인증 문제로 Release가 만들어지지 않았다면 실패 원인을 수정하고
새 patch 버전을 준비한다. 이미 push된 tag를 움직이지 않는다.

## 릴리즈 후 정리

릴리즈 후 다음 항목을 확인한다.

- GitHub Release 페이지에서 platform별 artifact가 모두 보이는지 확인한다.
- README 또는 `docs/usage.md`의 설치 안내가 현재 artifact 정책과 충돌하지 않는지
  확인한다.
- 릴리즈에서 발견한 smoke 결과 요약이 필요하면 `docs/archive/smoke-tests/`에
  보관한다.
- 다음 작업 메모가 필요하면 `docs/implementation-notes.md`에 반영한다.

릴리즈 후 문제가 발견되면 기존 GitHub Release와 tag를 수정하지 않고 새 patch
버전을 준비한다. 단, release note의 오탈자처럼 artifact와 tag 내용이 바뀌지 않는
수정은 GitHub Release 본문만 고칠 수 있다.

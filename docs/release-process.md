# 릴리즈 프로세스

이 문서는 `roslyn-mcp-server`의 수동 릴리즈 절차를 고정한다. 현재 릴리즈는
GitHub Release와 zip artifact를 기준으로 하며, NuGet/.NET global tool 배포와
release 자동화는 아직 하지 않는다.

## 릴리즈 기준

- 기본 브랜치: `main`
- 태그 형식: `v0.x.y`
- 첫 릴리즈 버전: `v0.1.0`
- 릴리즈 위치: GitHub Releases
- 릴리즈 산출물: framework-dependent `dotnet publish` 결과 zip
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
```

이미 push된 태그는 움직이지 않는다. 릴리즈 후 수정이 필요하면 새 patch 버전을
낸다.

## Artifact 정책

릴리즈 zip은 framework-dependent publish 결과로 만든다. 사용자는 대상 환경에
.NET runtime 또는 SDK를 설치해야 한다. self-contained artifact는 아직 만들지
않는다.

기본 artifact:

```text
roslyn-mcp-server-v0.x.y-win-x64.zip
roslyn-mcp-server-v0.x.y-linux-x64.zip
roslyn-mcp-server-v0.x.y-osx-x64.zip
roslyn-mcp-server-v0.x.y-osx-arm64.zip
```

zip에는 MCP 서버 실행 파일과 .NET publish 출력만 포함한다. 다음 항목은 포함하지
않는다.

- `roslyn-language-server`
- repository clone
- `.local/` 아래 smoke 결과, 로그, 임시 파일
- secrets 또는 개인 MCP client 설정

Unix 계열 artifact는 실행 권한이 중요하다. Windows에서 `linux-*` 또는 `osx-*`
zip을 만들면 실행 권한이 보존되지 않을 수 있으므로, 가능하면 대상 OS에서
artifact를 만들고 압축한다. Windows에서 만든 Unix zip을 배포해야 한다면 release
note에 `chmod +x roslyn-mcp-server` 안내를 포함한다.

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
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
```

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

아래 예시는 `v0.1.0` 릴리즈를 만드는 경우다. 다른 버전에서는 `$Version`만
바꾼다.

```powershell
$Version = "0.1.0"
$ReleaseRoot = ".local\release\v$Version"
$Project = ".\src\RoslynMcpServer\RoslynMcpServer.csproj"
$Rids = @("win-x64", "linux-x64", "osx-x64", "osx-arm64")

Remove-Item -LiteralPath $ReleaseRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

foreach ($Rid in $Rids) {
    $Name = "roslyn-mcp-server-v$Version-$Rid"
    $PublishDir = Join-Path $ReleaseRoot "publish\$Name"
    $ZipPath = Join-Path $ReleaseRoot "$Name.zip"

    dotnet publish $Project `
        -c Release `
        -r $Rid `
        --self-contained false `
        -o $PublishDir

    Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath -Force
}

Get-ChildItem $ReleaseRoot -Filter *.zip | Select-Object Name, Length
```

생성된 zip을 하나 이상 풀어서 실행 파일과 `.runtimeconfig.json`, `.deps.json`,
필요한 `.dll` 파일이 포함되어 있는지 확인한다.

```powershell
$CheckDir = Join-Path $ReleaseRoot "check-win-x64"
Remove-Item -LiteralPath $CheckDir -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path (Join-Path $ReleaseRoot "roslyn-mcp-server-v$Version-win-x64.zip") -DestinationPath $CheckDir
Get-ChildItem $CheckDir
```

artifact 생성물은 `.local/` 아래에만 둔다. `.local/` 아래 파일은 commit하지
않는다.

## 태그 생성

검증과 artifact 확인이 끝난 뒤 annotated tag를 만든다.

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
않는다. 로컬 변경, commit, tag, artifact 경로를 보존하고 사용자에게 실패 상태를
보고한다.

## GitHub Release 작성

GitHub Releases에서 새 릴리즈를 만든다.

- tag: `v0.x.y`
- title: `v0.x.y`
- body: 준비한 릴리즈 노트
- assets: 생성한 zip artifact 전체

GitHub CLI를 사용할 수 있다면 준비한 릴리즈 노트를 `$NotesPath`에 저장한 뒤
다음처럼 생성할 수 있다.

```powershell
$Version = "0.1.0"
$NotesPath = ".local\release\v$Version\release-notes.md"
gh release create "v$Version" `
    .local\release\v$Version\*.zip `
    --title "v$Version" `
    --notes-file $NotesPath
```

`gh` 사용은 필수가 아니다. GitHub 웹 UI로 생성해도 된다.

## 릴리즈 후 정리

릴리즈 후 다음 항목을 확인한다.

- GitHub Release 페이지에서 zip artifact가 모두 보이는지 확인한다.
- README 또는 `docs/usage.md`의 설치 안내가 현재 artifact 정책과 충돌하지 않는지
  확인한다.
- 릴리즈에서 발견한 smoke 결과 요약이 필요하면 `docs/archive/smoke-tests/`에
  보관한다.
- 다음 작업 메모가 필요하면 `docs/implementation-notes.md`에 반영한다.

릴리즈 후 문제가 발견되면 기존 GitHub Release와 tag를 수정하지 않고 새 patch
버전을 준비한다. 단, release note의 오탈자처럼 artifact와 tag 내용이 바뀌지 않는
수정은 GitHub Release 본문만 고칠 수 있다.

import os
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
LOCAL_DIR = Path(os.environ.get("ROSLYN_MCP_LOCAL_DIR", PROJECT_ROOT / ".local")).resolve()
REAL_REPOS_DIR = Path(os.environ.get("ROSLYN_MCP_REAL_REPOS_DIR", LOCAL_DIR / "real-repos")).resolve()


def project_root() -> str:
    return str(PROJECT_ROOT)


def local_path(*parts: str) -> str:
    LOCAL_DIR.mkdir(parents=True, exist_ok=True)
    return str(LOCAL_DIR.joinpath(*parts))


def local_dir(*parts: str) -> str:
    path = LOCAL_DIR.joinpath(*parts)
    path.mkdir(parents=True, exist_ok=True)
    return str(path)


def repo_root(environment_variable: str, default_directory_name: str) -> str:
    return str(Path(os.environ.get(environment_variable, REAL_REPOS_DIR / default_directory_name)).resolve())

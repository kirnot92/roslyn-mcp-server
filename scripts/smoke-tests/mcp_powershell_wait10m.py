import importlib.util
import json
import os
import time
from datetime import datetime, timezone

from smoke_paths import local_path, project_root


SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = project_root()
SMOKE_SCRIPT = os.path.join(SCRIPT_DIR, "mcp_powershell_smoke.py")
RAW_FILE = local_path("powershell-smoke-wait10m-raw.json")


def load_smoke_module():
    spec = importlib.util.spec_from_file_location("mcp_powershell_smoke", SMOKE_SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"could not load {SMOKE_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    smoke = load_smoke_module()
    client = smoke.McpClient()
    rows = []
    raw = {
        "startedAt": datetime.now(timezone.utc).isoformat(),
        "waitSeconds": 600,
        "pollSeconds": 60,
        "rows": rows,
        "stderr": client.stderr_lines,
    }

    try:
        raw["initialize"] = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-powershell-wait10m", "version": "0.1"},
            },
            timeout=60,
        )
        client.notify("notifications/initialized")
        raw["tools"] = client.request("tools/list", timeout=30)

        rows.append(smoke.call_tool(client, "list_workspaces", {"refresh": True}, timeout=30))
        rows.append(smoke.call_tool(client, "load_solution", {"path": "PowerShell.sln"}, timeout=120, note="selected top-level solution"))
        rows.append(smoke.call_tool(client, "get_workspace_status", timeout=30, note="immediate"))

        start = time.monotonic()
        next_poll = 60
        while True:
            elapsed = time.monotonic() - start
            if elapsed >= 600:
                break
            sleep_for = min(next_poll - elapsed, 600 - elapsed)
            if sleep_for > 0:
                time.sleep(sleep_for)
            elapsed = time.monotonic() - start
            if elapsed >= 600:
                break
            rows.append(smoke.call_tool(client, "get_workspace_status", timeout=30, note=f"poll +{int(round(elapsed))}s"))
            next_poll += 60

        rows.append(smoke.call_tool(client, "get_workspace_status", timeout=30, note="poll +600s"))
        rows.append(smoke.call_tool(client, "find_symbols", {"query": "ManagedPSEntry", "maxResults": 300}, timeout=60, note="after 10m"))
    finally:
        raw["finishedAt"] = datetime.now(timezone.utc).isoformat()
        raw["serverReturnCodeBeforeClose"] = client.proc.poll()
        raw["stderr"] = client.stderr_lines
        client.close()
        raw["serverReturnCodeAfterClose"] = client.proc.returncode
        with open(RAW_FILE, "w", encoding="utf-8") as f:
            json.dump(raw, f, indent=2)

    print(json.dumps(raw, indent=2))


if __name__ == "__main__":
    main()

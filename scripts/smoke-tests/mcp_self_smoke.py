import json
import os
import shlex
import subprocess
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

from smoke_paths import local_dir, local_path, project_root, server_command


ROOT = project_root()
WORKSPACE_ROOT = str(Path(os.environ.get("ROSLYN_MCP_SELF_SMOKE_ROOT", ROOT)).resolve())
SOLUTION_PATH = os.environ.get("ROSLYN_MCP_SELF_SMOKE_SOLUTION", "roslyn-mcp-server.sln")
SYMBOL_FILE = os.environ.get(
    "ROSLYN_MCP_SELF_SMOKE_SYMBOL_FILE",
    "src/RoslynMcpServer/Workspace/WorkspaceSession.cs",
)
SYMBOL_NAME = os.environ.get("ROSLYN_MCP_SELF_SMOKE_SYMBOL_NAME", "WorkspaceSession")
LOG_FILE = local_path("self-smoke.log")
RAW_FILE = local_path("self-smoke-raw.json")
LS_LOG_DIR = local_dir("self-smoke-ls-logs")
EXTRA_SERVER_ARGS = shlex.split(
    os.environ.get("ROSLYN_MCP_SMOKE_EXTRA_ARGS", ""),
    posix=os.name != "nt",
)
PRINT_RAW = os.environ.get("ROSLYN_MCP_SELF_SMOKE_PRINT_RAW", "").lower() in {
    "1",
    "true",
    "yes",
}

REQUIRED_TOOLS = {
    "list_workspaces",
    "load_solution",
    "get_workspace_status",
    "document_symbols",
    "find_symbols",
    "diagnostics",
}

USABLE_WORKSPACE_STATES = {
    "LspReady",
    "WorkspaceWarming",
    "LoadedWithErrors",
    "Ready",
}


class McpClient:
    def __init__(self):
        self.next_id = 1
        self.responses = {}
        self.messages = []
        self.invalid_stdout = []
        self.stderr_lines = []
        self.proc = subprocess.Popen(
            server_command(
                "--root",
                WORKSPACE_ROOT,
                "--log-file",
                LOG_FILE,
                "--ls-log-dir",
                LS_LOG_DIR,
                *EXTRA_SERVER_ARGS,
            ),
            cwd=ROOT,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        self.reader = threading.Thread(target=self._read_stdout, daemon=True)
        self.reader.start()
        self.err_reader = threading.Thread(target=self._read_stderr, daemon=True)
        self.err_reader.start()

    def _read_stdout(self):
        assert self.proc.stdout is not None
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception as exc:
                self.invalid_stdout.append({"line": line, "error": str(exc)})
                continue
            self.messages.append(msg)
            if "id" in msg:
                self.responses[msg["id"]] = msg

    def _read_stderr(self):
        assert self.proc.stderr is not None
        for line in self.proc.stderr:
            self.stderr_lines.append(line.rstrip())

    def request(self, method, params=None, timeout=60):
        request_id = self.next_id
        self.next_id += 1
        payload = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self._send(payload)

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if request_id in self.responses:
                return self.responses.pop(request_id)
            if self.proc.poll() is not None:
                raise RuntimeError(f"server exited with code {self.proc.returncode}")
            time.sleep(0.02)
        raise TimeoutError(f"{method} timed out after {timeout}s")

    def notify(self, method, params=None):
        payload = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        self._send(payload)

    def _send(self, payload):
        assert self.proc.stdin is not None
        self.proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        self.proc.stdin.flush()

    def close(self):
        try:
            if self.proc.stdin is not None:
                self.proc.stdin.close()
        except Exception:
            pass

        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
            self.proc.wait(timeout=10)


def decode_tool_result(response):
    if "error" in response:
        return {"_rpc_error": response["error"]}

    result = response.get("result")
    if isinstance(result, dict):
        if "structuredContent" in result:
            return result["structuredContent"]
        content = result.get("content")
        if isinstance(content, list) and content:
            text = content[0].get("text") if isinstance(content[0], dict) else None
            if isinstance(text, str):
                try:
                    return json.loads(text)
                except json.JSONDecodeError:
                    return {"_text": text}
    return result


def count_items(value):
    if isinstance(value, dict):
        if isinstance(value.get("items"), list):
            return len(value["items"])
        if isinstance(value.get("solutions"), list) or isinstance(value.get("projects"), list):
            return len(value.get("solutions", [])) + len(value.get("projects", []))
    return 0


def summarize(name, elapsed, decoded, ok=True, note=""):
    if isinstance(decoded, dict):
        error = decoded.get("error") or decoded.get("Error") or decoded.get("_rpc_error")
        return {
            "tool": name,
            "result": "OK" if ok and not error else "FAIL",
            "elapsed": elapsed,
            "count": count_items(decoded),
            "workspaceState": decoded.get("workspaceState") or decoded.get("state") or "",
            "completeness": decoded.get("completeness") or "",
            "truncated": decoded.get("truncated"),
            "totalKnown": decoded.get("totalKnown"),
            "returned": decoded.get("returned"),
            "error": error,
            "message": decoded.get("message"),
            "note": note,
            "decoded": decoded,
        }

    return {
        "tool": name,
        "result": "OK" if ok else "FAIL",
        "elapsed": elapsed,
        "count": 0,
        "workspaceState": "",
        "completeness": "",
        "truncated": None,
        "totalKnown": None,
        "returned": None,
        "error": None,
        "message": None,
        "note": note,
        "decoded": decoded,
    }


def call_tool(client, name, arguments=None, timeout=60, note=""):
    start = time.monotonic()
    try:
        response = client.request(
            "tools/call",
            {"name": name, "arguments": arguments or {}},
            timeout=timeout,
        )
        elapsed = round(time.monotonic() - start, 3)
        decoded = decode_tool_result(response)
        return summarize(name, elapsed, decoded, ok=True, note=note)
    except Exception as exc:
        elapsed = round(time.monotonic() - start, 3)
        return summarize(
            name,
            elapsed,
            {"error": type(exc).__name__, "message": str(exc)},
            ok=False,
            note=note,
        )


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def require_no_rpc_error(response, name):
    require("error" not in response, f"{name} failed: {response.get('error')}")


def extract_tool_names(response):
    require_no_rpc_error(response, "tools/list")
    tools = response.get("result", {}).get("tools", [])
    require(isinstance(tools, list), "tools/list did not return a tools array")
    return {tool.get("name") for tool in tools if isinstance(tool, dict)}


def require_tool_ok(row):
    require(
        row["result"] == "OK",
        f"{row['tool']} failed: {row.get('error') or row.get('message')}",
    )
    return row["decoded"]


def has_solution(workspaces, relative_path):
    if not isinstance(workspaces, dict):
        return False
    for candidate in workspaces.get("solutions", []):
        if isinstance(candidate, dict) and candidate.get("relativePath") == relative_path:
            return True
    return False


def contains_symbol(items, name):
    for item in items:
        if not isinstance(item, dict):
            continue
        item_name = item.get("name")
        if item_name == name or (
            isinstance(item_name, str) and item_name.endswith(f".{name}")
        ):
            return True
        if contains_symbol(item.get("children", []), name):
            return True
    return False


def wait_for_usable_workspace(client, rows, timeout=90):
    deadline = time.monotonic() + timeout
    attempt = 0

    while time.monotonic() < deadline:
        attempt += 1
        row = call_tool(
            client,
            "get_workspace_status",
            timeout=30,
            note=f"poll #{attempt}",
        )
        rows.append(row)
        decoded = require_tool_ok(row)
        state = decoded.get("state")

        require(state != "Failed", f"workspace failed: {decoded.get('failureMessage')}")
        if state in USABLE_WORKSPACE_STATES:
            return decoded

        time.sleep(2)

    raise TimeoutError(f"workspace did not become usable within {timeout}s")


def print_summary(raw, failure):
    summary = {
        "result": "FAIL" if failure is not None else "OK",
        "failure": failure,
        "startedAt": raw.get("startedAt"),
        "finishedAt": raw.get("finishedAt"),
        "rawFile": RAW_FILE,
        "workspaceRoot": WORKSPACE_ROOT,
        "solutionPath": SOLUTION_PATH,
        "rows": [
            {
                "tool": row.get("tool"),
                "result": row.get("result"),
                "elapsed": row.get("elapsed"),
                "workspaceState": row.get("workspaceState"),
                "completeness": row.get("completeness"),
                "truncated": row.get("truncated"),
                "totalKnown": row.get("totalKnown"),
                "returned": row.get("returned"),
                "error": row.get("error"),
                "message": row.get("message"),
                "note": row.get("note"),
            }
            for row in raw.get("rows", [])
        ],
        "invalidStdoutCount": len(raw.get("invalidStdout", [])),
        "stderrTail": raw.get("stderr", [])[-20:],
        "serverReturnCodeAfterClose": raw.get("serverReturnCodeAfterClose"),
    }

    print(json.dumps(summary, indent=2))


def main():
    raw = {
        "startedAt": datetime.now(timezone.utc).isoformat(),
        "configuration": {
            "workspaceRoot": WORKSPACE_ROOT,
            "solutionPath": SOLUTION_PATH,
            "symbolFile": SYMBOL_FILE,
            "symbolName": SYMBOL_NAME,
            "logFile": LOG_FILE,
            "lsLogDir": LS_LOG_DIR,
            "extraServerArgs": EXTRA_SERVER_ARGS,
        },
        "rows": [],
    }
    client = None
    failure = None

    try:
        client = McpClient()

        init = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-self-smoke", "version": "0.1"},
            },
            timeout=60,
        )
        raw["initialize"] = init
        require_no_rpc_error(init, "initialize")
        client.notify("notifications/initialized")

        tools = client.request("tools/list", timeout=30)
        raw["tools"] = tools
        tool_names = extract_tool_names(tools)
        missing_tools = sorted(REQUIRED_TOOLS - tool_names)
        require(not missing_tools, f"missing required tools: {', '.join(missing_tools)}")

        list_row = call_tool(client, "list_workspaces", timeout=30)
        raw["rows"].append(list_row)
        workspaces = require_tool_ok(list_row)
        require(
            has_solution(workspaces, SOLUTION_PATH),
            f"{SOLUTION_PATH} was not found by list_workspaces",
        )

        load_row = call_tool(
            client,
            "load_solution",
            {"path": SOLUTION_PATH},
            timeout=120,
            note="self solution",
        )
        raw["rows"].append(load_row)
        load_status = require_tool_ok(load_row)
        require(
            load_status.get("state") in USABLE_WORKSPACE_STATES,
            f"unexpected load state: {load_status.get('state')}",
        )

        status = wait_for_usable_workspace(client, raw["rows"])
        raw["usableWorkspaceStatus"] = status

        document_symbols_row = call_tool(
            client,
            "document_symbols",
            {
                "file": SYMBOL_FILE,
                "kindFilter": ["class"],
                "query": SYMBOL_NAME,
                "maxResults": 20,
                "timeoutSec": 30,
            },
            timeout=60,
            note=SYMBOL_FILE,
        )
        raw["rows"].append(document_symbols_row)
        document_symbols = require_tool_ok(document_symbols_row)
        require(
            contains_symbol(document_symbols.get("items", []), SYMBOL_NAME),
            f"{SYMBOL_NAME} was not found by document_symbols",
        )

        symbols_row = call_tool(
            client,
            "find_symbols",
            {
                "query": SYMBOL_NAME,
                "kindFilter": ["class"],
                "matchMode": "exact",
                "includePathPrefixes": ["src/RoslynMcpServer/Workspace"],
                "maxResults": 10,
            },
            timeout=60,
            note=SYMBOL_NAME,
        )
        raw["rows"].append(symbols_row)
        require_tool_ok(symbols_row)

        diagnostics_row = call_tool(
            client,
            "diagnostics",
            {"scope": "workspace", "maxResults": 10},
            timeout=45,
        )
        raw["rows"].append(diagnostics_row)
        require_tool_ok(diagnostics_row)
    except Exception as exc:
        failure = {"type": type(exc).__name__, "message": str(exc)}
        raw["failure"] = failure
    finally:
        raw["finishedAt"] = datetime.now(timezone.utc).isoformat()
        if client is not None:
            raw["serverReturnCodeBeforeClose"] = client.proc.poll()
            raw["stderr"] = client.stderr_lines
            raw["invalidStdout"] = client.invalid_stdout
            raw["messages"] = client.messages
            client.close()
            raw["serverReturnCodeAfterClose"] = client.proc.returncode

        with open(RAW_FILE, "w", encoding="utf-8") as f:
            json.dump(raw, f, indent=2)

    if PRINT_RAW:
        print(json.dumps(raw, indent=2))
    else:
        print_summary(raw, failure)

    if failure is not None:
        raise SystemExit(1)


if __name__ == "__main__":
    main()

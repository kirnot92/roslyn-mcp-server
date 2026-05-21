import json
import os
import queue
import subprocess
import threading
import time
from datetime import datetime, timezone

from smoke_paths import local_path, project_root, repo_root, server_command


ROOT = project_root()
POWERSHELL_ROOT = repo_root("ROSLYN_MCP_POWERSHELL_ROOT", "PowerShell")
LOG_FILE = local_path("powershell-smoke.log")
RAW_FILE = local_path("powershell-smoke-raw.json")
EXTRA_SERVER_ARGS = os.environ.get("ROSLYN_MCP_SMOKE_EXTRA_ARGS", "").split()


class McpClient:
    def __init__(self):
        self.next_id = 1
        self.responses = {}
        self.messages = queue.Queue()
        self.stderr_lines = []
        self.proc = subprocess.Popen(
            server_command(
                "--root",
                POWERSHELL_ROOT,
                "--log-file",
                LOG_FILE,
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
                self.messages.put({"invalid": line, "error": str(exc)})
                continue
            self.messages.put(msg)
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
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
            self.proc.wait(timeout=5)


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
        result = "OK" if ok and not error else "FAIL"
        return {
            "tool": name,
            "result": result,
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
        return summarize(name, elapsed, {"error": type(exc).__name__, "message": str(exc)}, ok=False, note=note)


def main():
    client = McpClient()
    rows = []
    raw = {"startedAt": datetime.now(timezone.utc).isoformat(), "rows": rows, "stderr": client.stderr_lines}
    try:
        init = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-powershell-smoke", "version": "0.1"},
            },
            timeout=60,
        )
        raw["initialize"] = init
        client.notify("notifications/initialized")

        tools = client.request("tools/list", timeout=30)
        raw["tools"] = tools

        rows.append(call_tool(client, "list_workspaces", timeout=30))

        selected = "PowerShell.sln"
        rows.append(call_tool(client, "load_solution", {"path": selected}, timeout=120, note="selected top-level solution"))

        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="immediate"))
        time.sleep(3)
        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="poll +3s"))
        time.sleep(7)
        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="poll +10s"))

        file = r"src\powershell\Program.cs"
        rows.append(call_tool(client, "document_symbols", {"file": file}, timeout=45, note=file))
        rows.append(call_tool(client, "hover", {"file": file, "line": 17, "column": 32}, timeout=45, note="ManagedPSEntry"))
        rows.append(call_tool(client, "go_to_definition", {"file": file, "line": 72, "column": 20}, timeout=60, note="UnmanagedPSEntry.Start"))
        rows.append(call_tool(client, "find_references", {"file": file, "line": 17, "column": 32, "includeDeclaration": True, "maxResults": 20}, timeout=60, note="ManagedPSEntry"))
        rows.append(call_tool(client, "find_symbols", {"query": "ManagedPSEntry", "maxResults": 300}, timeout=60))
        rows.append(call_tool(client, "diagnostics", {"file": file}, timeout=45, note=file))
        rows.append(call_tool(client, "diagnostics", {"scope": "workspace", "maxResults": 50}, timeout=45))
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

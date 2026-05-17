import json
import os
import queue
import subprocess
import threading
import time
from datetime import datetime, timezone

from smoke_paths import local_path, project_root, repo_root


ROOT = project_root()
REPO_ROOT = repo_root("ROSLYN_MCP_SEMANTIC_KERNEL_ROOT", "semantic-kernel")
LOG_FILE = local_path("semantic-kernel-smoke.log")
RAW_FILE = local_path("semantic-kernel-smoke-raw.json")


class McpClient:
    def __init__(self):
        self.next_id = 1
        self.responses = {}
        self.stderr_lines = []
        self.messages = queue.Queue()
        self.proc = subprocess.Popen(
            [
                "dotnet",
                "run",
                "--project",
                os.path.join(ROOT, "src", "RoslynMcpServer"),
                "--",
                "--root",
                REPO_ROOT,
                "--log-file",
                LOG_FILE,
            ],
            cwd=ROOT,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()

    def _read_stdout(self):
        assert self.proc.stdout is not None
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception as exc:
                msg = {"invalid": line, "error": str(exc)}
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
    if not isinstance(decoded, dict):
        decoded = {"value": decoded}
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


def call_tool(client, name, arguments=None, timeout=60, note=""):
    start = time.monotonic()
    try:
        response = client.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout=timeout)
        elapsed = round(time.monotonic() - start, 3)
        return summarize(name, elapsed, decode_tool_result(response), ok=True, note=note)
    except Exception as exc:
        elapsed = round(time.monotonic() - start, 3)
        return summarize(name, elapsed, {"error": type(exc).__name__, "message": str(exc)}, ok=False, note=note)


def main():
    client = McpClient()
    rows = []
    raw = {"startedAt": datetime.now(timezone.utc).isoformat(), "rows": rows}
    try:
        raw["initialize"] = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-semantic-kernel-smoke", "version": "0.1"},
            },
            timeout=60,
        )
        client.notify("notifications/initialized")
        raw["tools"] = client.request("tools/list", timeout=30)

        rows.append(call_tool(client, "list_workspaces", {"refresh": True}, timeout=45))
        rows.append(call_tool(client, "load_solution", {"path": r"dotnet\SK-dotnet.slnx"}, timeout=120, note="selected top-level .NET solution"))
        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="immediate"))
        time.sleep(3)
        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="poll +3s"))
        time.sleep(7)
        rows.append(call_tool(client, "get_workspace_status", timeout=30, note="poll +10s"))

        file = r"dotnet\src\SemanticKernel.Abstractions\Kernel.cs"
        rows.append(call_tool(client, "document_symbols", {"file": file}, timeout=45, note=file))
        rows.append(call_tool(client, "hover", {"file": file, "line": 26, "column": 24}, timeout=45, note="Kernel class"))
        rows.append(call_tool(client, "go_to_definition", {"file": file, "line": 85, "column": 56}, timeout=60, note="KernelBuilder"))
        rows.append(call_tool(client, "find_references", {"file": file, "line": 26, "column": 24, "includeDeclaration": True, "maxResults": 20}, timeout=60, note="Kernel class"))
        rows.append(call_tool(client, "find_symbols", {"query": "KernelBuilder", "maxResults": 300}, timeout=60))
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

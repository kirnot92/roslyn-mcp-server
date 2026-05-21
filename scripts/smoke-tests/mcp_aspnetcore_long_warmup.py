import json
import os
import queue
import subprocess
import threading
import time
from datetime import datetime, timezone

from smoke_paths import local_dir, local_path, project_root, repo_root, server_command


ROOT = project_root()
REPO_ROOT = repo_root("ROSLYN_MCP_ASPNETCORE_ROOT", "aspnetcore")
STAMP = os.environ.get("ASPNETCORE_LONG_STAMP") or datetime.now().strftime("%Y%m%d-%H%M%S")
LOG_FILE = local_path(f"aspnetcore-long-warmup-{STAMP}.log")
STDERR_FILE = local_path(f"aspnetcore-long-warmup-{STAMP}-stderr.log")
RAW_FILE = local_path(f"aspnetcore-long-warmup-{STAMP}-raw.json")
LS_LOG_DIR = local_dir(f"aspnetcore-ls-{STAMP}")
WARMUP_SECONDS = int(os.environ.get("ASPNETCORE_LONG_WARMUP_SECONDS", "600"))
POLL_SECONDS = int(os.environ.get("ASPNETCORE_LONG_POLL_SECONDS", "60"))
SYMBOL_CHECKPOINTS = {
    int(value)
    for value in os.environ.get("ASPNETCORE_SYMBOL_CHECKPOINTS", "180,300,600").split(",")
    if value.strip()
}


class McpClient:
    def __init__(self):
        os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
        os.makedirs(LS_LOG_DIR, exist_ok=True)
        self.next_id = 1
        self.responses = {}
        self.stderr_tail = []
        self.stderr_count = 0
        self.messages = queue.Queue()
        self.stderr_file = open(STDERR_FILE, "w", encoding="utf-8")
        self.proc = subprocess.Popen(
            server_command(
                "--root",
                REPO_ROOT,
                "--log-file",
                LOG_FILE,
                "--ls-log-dir",
                LS_LOG_DIR,
                "--log-level",
                "trace",
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
            line = line.rstrip()
            self.stderr_file.write(line + "\n")
            self.stderr_file.flush()
            self.stderr_count += 1
            self.stderr_tail.append(line)
            if len(self.stderr_tail) > 100:
                self.stderr_tail.pop(0)

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
            self.proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        self.stderr_file.close()


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


def summarize(name, elapsed, decoded, ok=True, note="", warmup_second=None):
    if not isinstance(decoded, dict):
        decoded = {"value": decoded}
    error = decoded.get("error") or decoded.get("Error") or decoded.get("_rpc_error")
    return {
        "tool": name,
        "warmupSecond": warmup_second,
        "result": "OK" if ok and not error else "FAIL",
        "elapsed": elapsed,
        "count": count_items(decoded),
        "workspaceState": decoded.get("workspaceState") or decoded.get("state") or "",
        "completeness": decoded.get("completeness") or "",
        "truncated": decoded.get("truncated"),
        "totalKnown": decoded.get("totalKnown"),
        "returned": decoded.get("returned"),
        "pendingLspRequests": decoded.get("pendingLspRequests"),
        "openDocumentCount": decoded.get("openDocumentCount"),
        "knownDiagnosticsFileCount": decoded.get("knownDiagnosticsFileCount"),
        "lastDiagnosticUpdateAt": decoded.get("lastDiagnosticUpdateAt"),
        "error": error,
        "message": decoded.get("message"),
        "note": note,
        "reason": decoded.get("reason"),
        "decoded": decoded,
    }


def call_tool(client, name, arguments=None, timeout=60, note="", warmup_second=None):
    start = time.monotonic()
    try:
        response = client.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout=timeout)
        elapsed = round(time.monotonic() - start, 3)
        return summarize(name, elapsed, decode_tool_result(response), ok=True, note=note, warmup_second=warmup_second)
    except Exception as exc:
        elapsed = round(time.monotonic() - start, 3)
        return summarize(
            name,
            elapsed,
            {"error": type(exc).__name__, "message": str(exc)},
            ok=False,
            note=note,
            warmup_second=warmup_second)


def print_row(row):
    print(
        f"{row['warmupSecond'] if row['warmupSecond'] is not None else '-':>4}s "
        f"{row['tool']:<20} {row['result']:<4} "
        f"elapsed={row['elapsed']:<7} count={row['count']:<4} "
        f"state={row['workspaceState']:<16} completeness={row['completeness']:<8} "
        f"knownDiagFiles={row['knownDiagnosticsFileCount']} openDocs={row['openDocumentCount']} "
        f"error={row['error']}",
        flush=True)


def main():
    client = McpClient()
    rows = []
    raw = {
        "startedAt": datetime.now(timezone.utc).isoformat(),
        "root": ROOT,
        "repoRoot": REPO_ROOT,
        "warmupSeconds": WARMUP_SECONDS,
        "pollSeconds": POLL_SECONDS,
        "symbolCheckpoints": sorted(SYMBOL_CHECKPOINTS),
        "logFile": LOG_FILE,
        "stderrFile": STDERR_FILE,
        "rawFile": RAW_FILE,
        "languageServerLogDirectory": LS_LOG_DIR,
        "rows": rows,
    }
    try:
        raw["initialize"] = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-aspnetcore-long-warmup", "version": "0.1"},
            },
            timeout=60,
        )
        client.notify("notifications/initialized")
        raw["tools"] = client.request("tools/list", timeout=30)

        for row in [
            call_tool(client, "list_workspaces", timeout=45),
            call_tool(client, "load_solution", {"path": "AspNetCore.slnx"}, timeout=120, note="selected top-level solution"),
        ]:
            rows.append(row)
            print_row(row)

        warmup_start = time.monotonic()
        completed_checkpoints = set()
        next_poll = 0
        while True:
            elapsed_warmup = int(time.monotonic() - warmup_start)
            due_checkpoints = sorted(
                checkpoint
                for checkpoint in SYMBOL_CHECKPOINTS
                if checkpoint <= elapsed_warmup and checkpoint not in completed_checkpoints)

            if elapsed_warmup >= next_poll:
                status = call_tool(
                    client,
                    "get_workspace_status",
                    timeout=30,
                    note=f"poll +{elapsed_warmup}s",
                    warmup_second=elapsed_warmup)
                rows.append(status)
                print_row(status)
                next_poll += POLL_SECONDS

            for checkpoint in due_checkpoints:
                symbol = call_tool(
                    client,
                    "find_symbols",
                    {"query": "HttpContext", "maxResults": 300},
                    timeout=60,
                    note="HttpContext",
                    warmup_second=checkpoint)
                rows.append(symbol)
                print_row(symbol)
                completed_checkpoints.add(checkpoint)

            if elapsed_warmup >= WARMUP_SECONDS:
                break

            next_wake = min(
                [next_poll, WARMUP_SECONDS] +
                [checkpoint for checkpoint in SYMBOL_CHECKPOINTS if checkpoint not in completed_checkpoints])
            sleep_for = max(0.5, min(5, next_wake - elapsed_warmup))
            time.sleep(sleep_for)

        for row in [
            call_tool(
                client,
                "document_symbols",
                {"file": r"src\Http\Http.Abstractions\src\HttpContext.cs"},
                timeout=45,
                note="final usability probe",
                warmup_second=WARMUP_SECONDS),
            call_tool(
                client,
                "find_references",
                {
                    "file": r"src\Http\Http.Abstractions\src\HttpContext.cs",
                    "line": 17,
                    "column": 25,
                    "includeDeclaration": True,
                    "maxResults": 20,
                },
                timeout=60,
                note="final usability probe: HttpContext",
                warmup_second=WARMUP_SECONDS),
        ]:
            rows.append(row)
            print_row(row)
    finally:
        raw["finishedAt"] = datetime.now(timezone.utc).isoformat()
        raw["serverReturnCodeBeforeClose"] = client.proc.poll()
        raw["stderrCount"] = client.stderr_count
        raw["stderrTail"] = client.stderr_tail
        client.close()
        raw["serverReturnCodeAfterClose"] = client.proc.returncode
        raw["languageServerLogFiles"] = [
            os.path.join(dirpath, filename)
            for dirpath, _, filenames in os.walk(LS_LOG_DIR)
            for filename in filenames
        ]
        with open(RAW_FILE, "w", encoding="utf-8") as f:
            json.dump(raw, f, indent=2)

    print("", flush=True)
    print(f"raw: {RAW_FILE}", flush=True)
    print(f"server log: {LOG_FILE}", flush=True)
    print(f"stderr log: {STDERR_FILE}", flush=True)
    print(f"Roslyn LS log dir: {LS_LOG_DIR}", flush=True)


if __name__ == "__main__":
    main()

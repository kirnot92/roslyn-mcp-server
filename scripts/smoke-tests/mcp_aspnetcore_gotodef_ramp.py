import json
import os
import queue
import re
import subprocess
import threading
import time
from datetime import datetime, timezone

from smoke_paths import local_path, project_root, repo_root


ROOT = project_root()
REPO_ROOT = repo_root("ROSLYN_MCP_ASPNETCORE_ROOT", "aspnetcore")
STAMP = os.environ.get("ASPNETCORE_GOTODEF_RAMP_STAMP") or datetime.now().strftime("%Y%m%d-%H%M%S")
LOG_FILE = local_path(f"aspnetcore-gotodef-ramp-{STAMP}.log")
STDERR_FILE = local_path(f"aspnetcore-gotodef-ramp-{STAMP}-stderr.log")
RAW_FILE = local_path(f"aspnetcore-gotodef-ramp-{STAMP}-raw.json")
CHECKPOINTS = [
    int(value)
    for value in os.environ.get("ASPNETCORE_GOTODEF_CHECKPOINTS", "0,5,10,15,20,30,45,60,90,120,180,240,300").split(",")
    if value.strip()
]

SOURCE_FILE = r"src\Http\Http\src\DefaultHttpContext.cs"
PROBE_SET = os.environ.get("ASPNETCORE_GOTODEF_PROBE_SET", "mixed").strip().lower()
MIXED_PROBES = [
    {
        "name": "HttpContext",
        "line": 21,
        "expectedFile": "src/Http/Http.Abstractions/src/HttpContext.cs",
    },
    {
        "name": "ItemsFeature",
        "line": 28,
        "expectedFile": "src/Http/Http/src/Features/ItemsFeature.cs",
    },
    {
        "name": "RequestServicesFeature",
        "line": 29,
        "expectedFile": "src/Http/Http/src/Features/RequestServicesFeature.cs",
    },
    {
        "name": "HttpAuthenticationFeature",
        "line": 30,
        "expectedFile": "src/Http/Http/src/Features/Authentication/HttpAuthenticationFeature.cs",
    },
    {
        "name": "HttpRequestLifetimeFeature",
        "line": 31,
        "expectedFile": "src/Http/Http/src/Features/HttpRequestLifetimeFeature.cs",
    },
    {
        "name": "DefaultSessionFeature",
        "line": 32,
        "expectedFile": "src/Http/Http/src/Features/DefaultSessionFeature.cs",
    },
    {
        "name": "HttpRequestIdentifierFeature",
        "line": 34,
        "expectedFile": "src/Http/Http/src/Features/HttpRequestIdentifierFeature.cs",
    },
    {
        "name": "FeatureCollection",
        "line": 52,
        "expectedFile": "src/Extensions/Features/src/FeatureCollection.cs",
    },
    {
        "name": "HttpRequestFeature",
        "line": 54,
        "expectedFile": "src/Http/Http/src/Features/HttpRequestFeature.cs",
    },
    {
        "name": "HttpResponseFeature",
        "line": 55,
        "expectedFile": "src/Http/Http/src/Features/HttpResponseFeature.cs",
    },
]
LOCAL_PROBES = [
    {
        "name": "ItemsFeature",
        "line": 28,
        "expectedFile": "src/Http/Http/src/Features/ItemsFeature.cs",
    },
    {
        "name": "RequestServicesFeature",
        "line": 29,
        "expectedFile": "src/Http/Http/src/Features/RequestServicesFeature.cs",
    },
    {
        "name": "HttpAuthenticationFeature",
        "line": 30,
        "expectedFile": "src/Http/Http/src/Features/Authentication/HttpAuthenticationFeature.cs",
    },
    {
        "name": "HttpRequestLifetimeFeature",
        "line": 31,
        "expectedFile": "src/Http/Http/src/Features/HttpRequestLifetimeFeature.cs",
    },
    {
        "name": "DefaultSessionFeature",
        "line": 32,
        "expectedFile": "src/Http/Http/src/Features/DefaultSessionFeature.cs",
    },
    {
        "name": "HttpRequestIdentifierFeature",
        "line": 34,
        "expectedFile": "src/Http/Http/src/Features/HttpRequestIdentifierFeature.cs",
    },
    {
        "name": "DefaultHttpRequest",
        "line": 38,
        "expectedFile": "src/Http/Http/src/Internal/DefaultHttpRequest.cs",
    },
    {
        "name": "DefaultHttpResponse",
        "line": 39,
        "expectedFile": "src/Http/Http/src/Internal/DefaultHttpResponse.cs",
    },
    {
        "name": "DefaultConnectionInfo",
        "line": 41,
        "expectedFile": "src/Http/Http/src/Internal/DefaultConnectionInfo.cs",
    },
    {
        "name": "DefaultWebSocketManager",
        "line": 42,
        "expectedFile": "src/Http/Http/src/Internal/DefaultWebSocketManager.cs",
    },
]
PROBES = LOCAL_PROBES if PROBE_SET == "local" else MIXED_PROBES


class McpClient:
    def __init__(self):
        os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
        self.next_id = 1
        self.responses = {}
        self.messages = queue.Queue()
        self.stderr_tail = []
        self.stderr_count = 0
        self.stderr_file = open(STDERR_FILE, "w", encoding="utf-8")
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


def call_tool(client, name, arguments=None, timeout=60):
    start = time.monotonic()
    try:
        response = client.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout=timeout)
        elapsed = round(time.monotonic() - start, 3)
        decoded = decode_tool_result(response)
        if not isinstance(decoded, dict):
            decoded = {"value": decoded}
        error = decoded.get("error") or decoded.get("Error") or decoded.get("_rpc_error")
        return "OK" if not error else "FAIL", elapsed, decoded
    except Exception as exc:
        return "FAIL", round(time.monotonic() - start, 3), {"error": type(exc).__name__, "message": str(exc)}


def add_columns():
    source_path = os.path.join(REPO_ROOT, SOURCE_FILE)
    lines = open(source_path, encoding="utf-8").read().splitlines()
    for probe in PROBES:
        text = lines[probe["line"] - 1]
        match = re.search(rf"\b{re.escape(probe['name'])}\b", text)
        if match is None:
            raise RuntimeError(f"{probe['name']} not found on line {probe['line']}: {text}")
        probe["column"] = match.start() + 1 + (len(probe["name"]) // 2)


def normalize_path(value):
    return value.replace("\\", "/") if isinstance(value, str) else value


def map_definition_result(probe, result, status, elapsed, checkpoint, started, completed):
    items = result.get("items") if isinstance(result.get("items"), list) else []
    expected = normalize_path(probe["expectedFile"])
    matched = [
        item
        for item in items
        if normalize_path(item.get("file")) == expected
    ]
    return {
        "checkpointSecond": checkpoint,
        "startedWarmupSecond": round(started, 3),
        "completedWarmupSecond": round(completed, 3),
        "probe": probe["name"],
        "file": SOURCE_FILE,
        "line": probe["line"],
        "column": probe["column"],
        "expectedFile": expected,
        "result": status,
        "elapsed": elapsed,
        "count": len(items),
        "matchedExpected": len(matched) > 0,
        "workspaceState": result.get("workspaceState", ""),
        "completeness": result.get("completeness", ""),
        "retryAfterMs": result.get("retryAfterMs"),
        "error": result.get("error") or result.get("Error") or result.get("_rpc_error"),
        "message": result.get("message"),
        "items": items,
    }


def print_row(row):
    print(
        f"{row['checkpointSecond']:>4}s "
        f"{row['probe']:<30} {row['result']:<4} "
        f"elapsed={row['elapsed']:<6} count={row['count']:<2} "
        f"matched={str(row['matchedExpected']):<5} "
        f"state={row['workspaceState']:<16} completeness={row['completeness']:<8} "
        f"error={row['error']}",
        flush=True)


def main():
    add_columns()
    client = McpClient()
    rows = []
    first_resolved = {}
    raw = {
        "startedAt": datetime.now(timezone.utc).isoformat(),
        "root": ROOT,
        "repoRoot": REPO_ROOT,
        "sourceFile": SOURCE_FILE,
        "probeSet": PROBE_SET,
        "checkpoints": CHECKPOINTS,
        "probes": PROBES,
        "logFile": LOG_FILE,
        "stderrFile": STDERR_FILE,
        "rawFile": RAW_FILE,
        "rows": rows,
    }
    try:
        raw["initialize"] = client.request(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "roslyn-mcp-aspnetcore-gotodef-ramp", "version": "0.1"},
            },
            timeout=60,
        )
        client.notify("notifications/initialized")
        raw["tools"] = client.request("tools/list", timeout=30)

        for name, args, timeout in [
            ("list_workspaces", {"refresh": True}, 45),
            ("load_solution", {"path": "AspNetCore.slnx"}, 120),
        ]:
            status, elapsed, decoded = call_tool(client, name, args, timeout=timeout)
            setup_row = {
                "tool": name,
                "result": status,
                "elapsed": elapsed,
                "workspaceState": decoded.get("workspaceState") or decoded.get("state") or "",
                "decoded": decoded,
            }
            rows.append(setup_row)
            print(f"setup {name:<16} {status:<4} elapsed={elapsed:<6} state={setup_row['workspaceState']}", flush=True)

        warmup_start = time.monotonic()
        raw["loadedAt"] = datetime.now(timezone.utc).isoformat()
        for checkpoint in CHECKPOINTS:
            while time.monotonic() - warmup_start < checkpoint:
                time.sleep(min(0.5, checkpoint - (time.monotonic() - warmup_start)))

            for probe in PROBES:
                if probe["name"] in first_resolved:
                    continue

                started = time.monotonic() - warmup_start
                status, elapsed, decoded = call_tool(
                    client,
                    "go_to_definition",
                    {
                        "file": SOURCE_FILE,
                        "line": probe["line"],
                        "column": probe["column"],
                    },
                    timeout=60)
                completed = time.monotonic() - warmup_start
                row = map_definition_result(probe, decoded, status, elapsed, checkpoint, started, completed)
                rows.append(row)
                print_row(row)
                if row["matchedExpected"]:
                    first_resolved[probe["name"]] = row

            if len(first_resolved) == len(PROBES):
                break

        raw["firstResolved"] = first_resolved
        raw["allResolved"] = len(first_resolved) == len(PROBES)
        raw["allResolvedAt"] = max((row["completedWarmupSecond"] for row in first_resolved.values()), default=None)

        status, elapsed, decoded = call_tool(client, "get_workspace_status", timeout=30)
        raw["finalStatus"] = decoded
        rows.append({
            "tool": "get_workspace_status",
            "result": status,
            "elapsed": elapsed,
            "workspaceState": decoded.get("state", ""),
            "warningCount": len(decoded.get("warnings") or []),
            "decoded": decoded,
        })
    finally:
        raw["finishedAt"] = datetime.now(timezone.utc).isoformat()
        raw["serverReturnCodeBeforeClose"] = client.proc.poll()
        raw["stderrCount"] = client.stderr_count
        raw["stderrTail"] = client.stderr_tail
        client.close()
        raw["serverReturnCodeAfterClose"] = client.proc.returncode
        with open(RAW_FILE, "w", encoding="utf-8") as f:
            json.dump(raw, f, indent=2)

    print("", flush=True)
    print(f"allResolved={raw['allResolved']} allResolvedAt={raw['allResolvedAt']}", flush=True)
    print(f"raw: {RAW_FILE}", flush=True)
    print(f"server log: {LOG_FILE}", flush=True)
    print(f"stderr log: {STDERR_FILE}", flush=True)


if __name__ == "__main__":
    main()

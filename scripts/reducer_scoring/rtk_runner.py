"""Runs rtk-proxy's CCR preview over each payload and prints one JSON line per timed run.

Starts a fake Anthropic upstream and two private rtk-proxy instances on spare loopback ports,
one with CCR on and one with --ccr false, each with its own HOME and databases so the live
instance, its config and its stats are never touched:
  python3 rtk_runner.py <runs> <payload name>...
"""
import json
import os
import shutil
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CCR_PORT, PLAIN_PORT, CCR_UPSTREAM, PLAIN_UPSTREAM = 18081, 18082, 18997, 18998
PIPELINE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rtk_pipeline.toml")
REPLY = json.dumps({"id": "msg_1", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
                    "content": [{"type": "text", "text": "ok"}], "stop_reason": "end_turn",
                    "stop_sequence": None, "usage": {"input_tokens": 1, "output_tokens": 1}}).encode()


def request_body(tool_output: str) -> bytes:
    """An Anthropic Messages request whose only tool result is the payload."""
    return json.dumps({"model": "claude-opus-5-5", "max_tokens": 16, "messages": [
        {"role": "assistant", "content": [{"type": "tool_use", "id": "toolu_1", "name": "Bash", "input": {}}]},
        {"role": "user", "content": [{"type": "tool_result", "tool_use_id": "toolu_1", "content": tool_output}]},
    ]}).encode()


def tool_result_text(body: dict) -> str:
    """Every character of the tool result as it reached the upstream, preview and marker included."""
    content = body["messages"][1]["content"][0]["content"]
    if isinstance(content, str):
        return content
    return "".join(block.get("text", "") for block in content)


def ccr_latencies(ccr_trips: list[float], plain_trips: list[float]) -> list[float]:
    """Each CCR round trip less the median plain round trip, floored at zero."""
    baseline = statistics.median(plain_trips)
    return [round(max(0.0, trip - baseline), 9) for trip in ccr_trips]


class Upstream(ThreadingHTTPServer):
    """A fake Anthropic upstream that keeps the last body it received."""

    def __init__(self, port: int):
        self.last = None
        upstream = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                upstream.last = json.loads(self.rfile.read(int(self.headers.get("content-length", 0))))
                self.send_response(200)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(REPLY)))
                self.end_headers()
                self.wfile.write(REPLY)

            def log_message(self, *args):
                pass

        super().__init__(("127.0.0.1", port), Handler)
        threading.Thread(target=self.serve_forever, daemon=True).start()


def _wait_for(port: int, seconds: float = 20.0) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        with socket.socket() as s:
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return
        time.sleep(0.1)
    raise RuntimeError(f"rtk-proxy did not start listening on {port}")


def _start_rtk(home: str, port: int, upstream: int, ccr: bool, pipeline: str = PIPELINE) -> subprocess.Popen:
    config = os.path.join(home, "Library", "Application Support", "rtk")
    os.makedirs(config, exist_ok=True)
    shutil.copyfile(pipeline, os.path.join(config, "pipeline.toml"))
    env = {"HOME": home, "PATH": os.environ["PATH"]}
    proc = subprocess.Popen(
        ["rtk-proxy", "start", "--bind", f"127.0.0.1:{port}",
         "--anthropic-upstream", f"http://127.0.0.1:{upstream}", "--ccr", "true" if ccr else "false",
         "--db", os.path.join(home, "rtk-proxy.db"), "--icm-db", os.path.join(home, "rtk-proxy-memory.db")],
        env=env, cwd=home, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _wait_for(port)
    return proc


def _round_trip(port: int, body: bytes) -> float:
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/messages", data=body, headers={
        "content-type": "application/json", "x-api-key": "sk-ant-harness-not-a-key",
        "anthropic-version": "2023-06-01"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    started = time.perf_counter()
    with opener.open(request, timeout=60) as response:
        response.read()
    return time.perf_counter() - started


def main() -> None:
    from preregistration import payload_text

    runs, names = int(sys.argv[1]), sys.argv[2:]
    ccr_upstream, plain_upstream = Upstream(CCR_UPSTREAM), Upstream(PLAIN_UPSTREAM)
    with tempfile.TemporaryDirectory(prefix="reducer-scoring-rtk-") as root:
        homes = [os.path.join(root, n) for n in ("ccr", "plain")]
        for home in homes:
            os.makedirs(home)
        procs = [_start_rtk(homes[0], CCR_PORT, CCR_UPSTREAM, True),
                 _start_rtk(homes[1], PLAIN_PORT, PLAIN_UPSTREAM, False)]
        try:
            for name in names:
                body = request_body(payload_text(name))
                _round_trip(CCR_PORT, body)
                _round_trip(PLAIN_PORT, body)
                ccr_trips, outputs, plain_trips = [], [], []
                for _ in range(runs):
                    ccr_trips.append(_round_trip(CCR_PORT, body))
                    outputs.append(tool_result_text(ccr_upstream.last))
                    plain_trips.append(_round_trip(PLAIN_PORT, body))
                for run, (output, latency) in enumerate(zip(outputs, ccr_latencies(ccr_trips, plain_trips))):
                    print(json.dumps({"payload": name, "run": run, "output": output, "latency_s": latency}), flush=True)
        finally:
            for proc in procs:
                proc.terminate()
                proc.wait(timeout=10)
            ccr_upstream.shutdown()
            plain_upstream.shutdown()


if __name__ == "__main__":
    main()

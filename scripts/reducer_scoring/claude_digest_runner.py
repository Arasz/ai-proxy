"""Runs the Haiku digest through `claude -p`, one isolated call per payload and run, and prints
one JSON line per run with the digest and the model's own time (duration_api_ms).

  python3 claude_digest_runner.py <runs> <payload name>...

The proxy variables are cleared so the call does not pass through ai-proxy, which would
compress the payload before Haiku sees it.
"""
import json
import os
import subprocess
import sys
import time

from pi_runner import DIGEST_SYSTEM

MODEL = "claude-haiku-5-5"


def command(system: str) -> list[str]:
    """The isolated call: Haiku, no tools, no settings, no MCP servers, no saved session."""
    return ["claude", "-p", "--model", MODEL, "--tools", "", "--setting-sources", "",
            "--strict-mcp-config", "--no-session-persistence", "--system-prompt", system,
            "--output-format", "json"]


def parse_result(raw: str) -> tuple[str, float]:
    """The reply and the model's own time in seconds; raises when the call did not produce a digest."""
    result = json.loads(raw)
    text = result.get("result", "")
    if result.get("is_error") or not text.strip():
        raise RuntimeError(f"claude -p did not produce a digest: {text[:300]!r}")
    models = set(result.get("modelUsage", {MODEL: {}}))
    if models != {MODEL}:
        raise RuntimeError(f"the digest came from {sorted(models)}, not {MODEL}")
    return text, result["duration_api_ms"] / 1000


def main() -> None:
    from preregistration import payload_text

    runs, names = int(sys.argv[1]), sys.argv[2:]
    env = {k: v for k, v in os.environ.items()
           if k.upper() not in ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS")}
    env["NO_PROXY"] = "*"
    for name in names:
        text = payload_text(name)
        for run in range(runs):
            started = time.perf_counter()
            done = subprocess.run(command(DIGEST_SYSTEM), input=text, env=env,
                                  capture_output=True, text=True, timeout=600)
            wall = time.perf_counter() - started
            if done.returncode != 0:
                raise RuntimeError(f"claude -p failed on {name} (exit {done.returncode}): {done.stderr[:300]}")
            output, api_s = parse_result(done.stdout)
            print(json.dumps({"payload": name, "run": run, "output": output, "latency_s": api_s,
                              "wall_s": round(wall, 3), "model": MODEL}), flush=True)


if __name__ == "__main__":
    main()

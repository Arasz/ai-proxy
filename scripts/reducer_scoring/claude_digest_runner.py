"""Runs the Haiku digest through `claude -p`, one isolated call per payload and run, and prints
one JSON line per run with the digest and the model's own time (duration_api_ms).

  python3 claude_digest_runner.py <candidate> <runs> <payload name>...

The proxy variables are cleared so the call does not pass through ai-proxy, which would
compress the payload before Haiku sees it, and the session's effort is cleared so only the
candidate's own effort applies.
"""
import json
import os
import subprocess
import sys
import time

from pi_runner import DIGEST_SYSTEM

MODEL = "claude-haiku-5-5"
CHECKED_SYSTEM = (
    "You compress tool output for a coding agent that will act on it. Rewrite the output as short as "
    "possible while keeping every fact the agent may need: names, identifiers, paths, numbers, versions, "
    "error codes and messages, exactly as written. First, find all {facts}. Next, compress content ensuring "
    "{facts} will persist. As a final step check compresed output for {facts} and fix it if any is missing. "
    "Reply with the compressed output only.")
DIGESTS = {
    "haiku_digest": (DIGEST_SYSTEM, None),
    "haiku_digest_checked": (CHECKED_SYSTEM, "low"),
}


def command(system: str, effort: str | None = None, model: str = MODEL) -> list[str]:
    """The isolated call: the model (Haiku by default), no tools, no settings, no MCP servers, no saved session."""
    return ["claude", "-p", "--model", model, "--tools", "", "--setting-sources", "",
            "--strict-mcp-config", "--no-session-persistence", "--system-prompt", system,
            "--output-format", "json", *(["--effort", effort] if effort else [])]


def child_env(env: dict[str, str]) -> dict[str, str]:
    """The caller's environment without the proxy variables and the session's effort."""
    dropped = ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS", "CLAUDE_EFFORT")
    return {k: v for k, v in env.items() if k.upper() not in dropped} | {"NO_PROXY": "*"}


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

    candidate, runs, names = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
    system, effort = DIGESTS[candidate]
    env = child_env(dict(os.environ))
    for name in names:
        text = payload_text(name)
        for run in range(runs):
            started = time.perf_counter()
            done = subprocess.run(command(system, effort), input=text, env=env,
                                  capture_output=True, text=True, timeout=600)
            wall = time.perf_counter() - started
            if done.returncode != 0:
                raise RuntimeError(f"claude -p failed on {name} (exit {done.returncode}): {done.stderr[:300]}")
            output, api_s = parse_result(done.stdout)
            print(json.dumps({"payload": name, "run": run, "output": output, "latency_s": api_s,
                              "wall_s": round(wall, 3), "model": MODEL, "effort": effort,
                              "model_usage": json.loads(done.stdout).get("modelUsage")}), flush=True)


if __name__ == "__main__":
    main()

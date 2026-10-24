"""Edit spike: run each edit task on each model, with and without the retrieve hint, and record what the agent did."""
import json, os, shutil, subprocess, sys, itertools
from concurrent.futures import ThreadPoolExecutor

SP = os.path.dirname(os.path.abspath(__file__))
ORIGINAL = open(os.path.join(SP, "..", "reducer_scoring", "payloads", "code_file.txt")).read()
TASKS = {
    "add_rule": ('In DialectDetector.cs, add the path rule ("/v1/completions", Dialect.OpenAi) to PathRules, right after the /v1/models rule.',
                 ORIGINAL.replace('        ("/v1/models", Dialect.OpenAi),\n',
                                  '        ("/v1/models", Dialect.OpenAi),\n        ("/v1/completions", Dialect.OpenAi),\n')),
    "reword": ('In DialectDetector.cs, change the header detection reason from "headers say {headerDialect}" to "header says {headerDialect}".',
               ORIGINAL.replace('$"headers say {headerDialect}"', '$"header says {headerDialect}"')),
    "rename": ("In DialectDetector.cs, rename the method HasCacheControl to HasCacheControlMarker, including every call to it.",
               ORIGINAL.replace("HasCacheControl(", "HasCacheControlMarker(")),
}
for name, (_, want) in TASKS.items():
    assert want != ORIGINAL, name
MODELS = sys.argv[1].split(",")
RUNS = int(sys.argv[2])
TOOLS = "mcp__fs__read_file,mcp__fs__retrieve_original,mcp__fs__edit_file"


def one(job):
    model, task, hint, run = job
    d = os.path.join(os.environ.get("SPIKE_RUNS", "/tmp/code_reducer_spike_runs"), f"{model}-{task}-hint{hint}-{run}")
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
    open(os.path.join(d, "DialectDetector.cs"), "w").write(ORIGINAL)
    log = os.path.join(d, "calls.jsonl"); open(log, "w").close()
    cfg = {"mcpServers": {"fs": {"command": sys.executable, "args": [os.path.join(SP, "fs_server.py")],
                                 "env": {"SPIKE_ROOT": d, "SPIKE_LOG": log, "SPIKE_HINT": str(hint)}}}}
    open(os.path.join(d, "mcp.json"), "w").write(json.dumps(cfg))
    env = {k: v for k, v in os.environ.items() if k.upper() not in
           ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS", "CLAUDE_EFFORT")} | {"NO_PROXY": "*"}
    done = subprocess.run(["claude", "-p", "--model", model, "--tools", "", "--setting-sources", "",
                           "--strict-mcp-config", "--mcp-config", os.path.join(d, "mcp.json"),
                           "--allowedTools", TOOLS, "--no-session-persistence", "--output-format", "json",
                           "--system-prompt", "You are a coding agent. Use the tools to complete the task, then reply done."],
                          input=TASKS[task][0], env=env, capture_output=True, text=True, timeout=600, cwd=d)
    calls = [json.loads(l) for l in open(log)]
    names = [c["tool"] for c in calls]
    edits = [c for c in calls if c["tool"] == "edit_file"]
    first_edit = names.index("edit_file") if "edit_file" in names else None
    final = open(os.path.join(d, "DialectDetector.cs")).read()
    return {"model": model, "task": task, "hint": hint, "run": run, "exit": done.returncode,
            "calls": names,
            "retrieved_before_edit": first_edit is not None and "retrieve_original" in names[:first_edit],
            "edits": len(edits), "failed_edits": sum(e["error"] for e in edits),
            "first_edit_ok": bool(edits) and not edits[0]["error"],
            "correct": final == TASKS[task][1]}


jobs = list(itertools.product(MODELS, TASKS, (0, 1), range(RUNS)))
with ThreadPoolExecutor(4) as pool:
    for r in pool.map(one, jobs):
        print(json.dumps(r), flush=True)

"""Sends tool results of chosen sizes through a proxy and reports what reached the fake upstream."""
import json, sys, urllib.request, pathlib, hashlib
proxy, bodies = sys.argv[1], pathlib.Path(sys.argv[2])
def text(chars, seed):
    lines, i = [], 0
    while sum(len(l) + 1 for l in lines) < chars:
        h = hashlib.sha256(f"{seed}-{i}".encode()).hexdigest()
        lines.append(f"src/mod{i % 37}/file{i}.cs({i},{i % 80}): warning CS{1000 + i % 900}: value {h[:24]} is never used")
        i += 1
    return "\n".join(lines)[:chars]
def send(blocks, tag):
    msgs = [{"role": "user", "content": f"probe {tag}"}]
    for k, chars in enumerate(blocks):
        tid = f"toolu_{tag}_{k}"
        msgs.append({"role": "assistant", "content": [{"type": "tool_use", "id": tid, "name": "Bash", "input": {"command": f"build {tag} {k}"}}]})
        msgs.append({"role": "user", "content": [{"type": "tool_result", "tool_use_id": tid, "content": text(chars, f"{tag}-{k}")}]})
    req = urllib.request.Request(f"{proxy}/v1/messages", data=json.dumps({"model": "claude-haiku-5-5", "max_tokens": 16, "messages": msgs}).encode(),
        headers={"content-type": "application/json", "x-api-key": "sk-ant-probe-not-a-key", "anthropic-version": "2023-06-01"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(req, timeout=30) as r: r.read()
    newest = sorted(bodies.glob("*.json"))[-1]
    sent = json.loads(newest.read_text())
    out = []
    for m in sent["messages"]:
        if isinstance(m["content"], list):
            for b in m["content"]:
                if b.get("type") == "tool_result":
                    c = b["content"] if isinstance(b["content"], str) else json.dumps(b["content"])
                    out.append((len(c), "rtk_retrieve" in c or "compressed" in c.lower()))
    return out
for spec in sys.argv[3:]:
    blocks = [int(x) for x in spec.split("+")]
    print(spec, send(blocks, spec.replace("+", "p")))

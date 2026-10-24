"""Token spike: Haiku input tokens for a code file as-is and after each compaction, via claude -p.

  python3 tokens.py <code file>
"""
import json, os, re, subprocess, sys
t = open(sys.argv[1]).read()
L = t.split("\n")
keep = lambda l: l.strip() and not l.strip().startswith(("using ", "//"))
kept = [(i + 1, l) for i, l in enumerate(L) if keep(l)]
def enc(l):
    n = len(l) - len(l.lstrip(" "))
    return (f"{n}_" if n else "") + l.strip()
V = {
    "original": t,
    "lossy, indent kept": "\n".join(l.rstrip() for _, l in kept),
    "lossy, indent stripped": "\n".join(l.strip() for _, l in kept),
    "lossy, line no + indent stripped": "\n".join(f"{i}|{l.strip()}" for i, l in kept),
}
env = {k: v for k, v in os.environ.items() if k.upper() not in ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NODE_EXTRA_CA_CERTS")} | {"NO_PROXY": "*"}
def tokens(text):
    r = json.loads(subprocess.run(["claude", "-p", "--model", "claude-haiku-5-5", "--tools", "", "--setting-sources", "",
        "--strict-mcp-config", "--no-session-persistence", "--system-prompt", "Reply ok.", "--output-format", "json"],
        input=text, env=env, capture_output=True, text=True, timeout=300).stdout)
    u = r["modelUsage"]["claude-haiku-5-5"]
    return u["inputTokens"] + u["cacheCreationInputTokens"] + u["cacheReadInputTokens"]
base = tokens("x")
full_c, full_t = len(t), tokens(t) - base
for name, text in V.items():
    tk = tokens(text) - base
    print(f"{name:34} chars {len(text):5} ({1-len(text)/full_c:5.1%})  tokens {tk:5} ({1-tk/full_t:5.1%})")

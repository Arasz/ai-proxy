"""Edit spike MCP server: read_file returns a compacted view, retrieve_original the exact file,
edit_file applies an exact-match replace on disk. Every call is logged to $SPIKE_LOG."""
import json, os, re, sys

ROOT, LOG, HINT = os.environ["SPIKE_ROOT"], os.environ["SPIKE_LOG"], os.environ.get("SPIKE_HINT") == "1"


def compact(text):
    out = []
    for line in text.split("\n"):
        s = line.strip()
        if not s or s.startswith("using ") or s.startswith("//"):
            continue
        out.append(s)
    return "\n".join(out)


def path_of(p):
    full = os.path.realpath(os.path.join(ROOT, os.path.basename(p)))
    if not os.path.exists(full):
        raise ValueError(f"no such file: {p}")
    return full


def call(name, args):
    full = path_of(args["path"])
    text = open(full).read()
    if name == "read_file":
        body = compact(text)
        note = ("\n\n[compacted view: imports, comments, blank lines and indentation removed. "
                "Call retrieve_original for the exact text before editing.]") if HINT else ""
        return body + note
    if name == "retrieve_original":
        return text
    if name == "edit_file":
        old, new = args["old_string"], args["new_string"]
        n = text.count(old)
        if n != 1:
            raise ValueError(f"old_string found {n} times in the file; it must match the file exactly once")
        open(full, "w").write(text.replace(old, new, 1))
        return "edited"
    raise ValueError(name)


TOOLS = [
    {"name": "read_file", "description": "Read a source file.",
     "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}},
    {"name": "retrieve_original", "description": "Return the exact, unmodified text of a file.",
     "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}},
    {"name": "edit_file", "description": "Replace old_string with new_string in a file. old_string must match the file on disk exactly, once.",
     "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}, "old_string": {"type": "string"},
                                                       "new_string": {"type": "string"}},
                     "required": ["path", "old_string", "new_string"]}},
]

for line in sys.stdin:
    msg = json.loads(line)
    mid, method = msg.get("id"), msg.get("method")
    if mid is None:
        continue
    if method == "initialize":
        result = {"protocolVersion": msg["params"].get("protocolVersion", "2025-06-18"),
                  "capabilities": {"tools": {}}, "serverInfo": {"name": "fs", "version": "0"}}
    elif method == "tools/list":
        result = {"tools": TOOLS}
    elif method == "tools/call":
        name, args = msg["params"]["name"], msg["params"].get("arguments", {})
        try:
            text, err = call(name, args), False
        except Exception as e:
            text, err = f"error: {e}", True
        with open(LOG, "a") as f:
            f.write(json.dumps({"tool": name, "args": args, "error": err, "result_chars": len(text)}) + "\n")
        result = {"content": [{"type": "text", "text": text}], "isError": err}
    else:
        result = {}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": mid, "result": result}) + "\n")
    sys.stdout.flush()

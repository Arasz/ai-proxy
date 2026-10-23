import json, sys, urllib.request, urllib.error
port, marker = sys.argv[1], sys.argv[2]
body = json.dumps({"model": "claude-haiku-5-5", "max_tokens": 8, "messages": [{"role": "user", "content": marker}]}).encode()
req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/messages", data=body, headers={"content-type": "application/json", "x-api-key": "sk-ant-probe-not-a-key", "anthropic-version": "2023-06-01"})
try:
    with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=60) as r: print(port, r.status, r.read()[:200])
except urllib.error.HTTPError as e: print(port, e.code, e.read()[:200])
except Exception as e: print(port, "ERR", repr(e)[:200])

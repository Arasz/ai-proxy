"""Records every request body it receives and answers like a minimal Anthropic Messages API."""
import json, sys, pathlib, itertools
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
out = pathlib.Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
seq = itertools.count(1)
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("content-length", 0)))
        n = next(seq)
        (out / f"{n:03d}.json").write_bytes(body)
        (out / f"{n:03d}.meta").write_text(json.dumps({"path": self.path, "headers": dict(self.headers)}))
        reply = json.dumps({"id": f"msg_{n}", "type": "message", "role": "assistant", "model": "claude-haiku-5-5",
            "content": [{"type": "text", "text": "ok"}], "stop_reason": "end_turn", "stop_sequence": None,
            "usage": {"input_tokens": 1, "output_tokens": 1}}).encode()
        self.send_response(200); self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(reply))); self.end_headers(); self.wfile.write(reply)
    def log_message(self, *a): pass
ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()

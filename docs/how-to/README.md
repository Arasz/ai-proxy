# how-to/

Task-oriented recipes: the reader has a goal (point an agent at the proxy, read the usage
stats) and follows the steps. Filenames start with an imperative verb.

## Contents

| File | What it is |
|---|---|
| [running-the-proxy.md](running-the-proxy.md) | Install and start the proxy, what it decrypts and what it leaves alone, restart, status, stop, and crash recovery. |
| [connecting-an-agent.md](connecting-an-agent.md) | Start Claude Code or Codex through the proxy with `ai-proxy harness`, use the `aph` shortcut, and check the setup with `ai-proxy doctor`. |
| [reading-gain-and-retrieving-originals.md](reading-gain-and-retrieving-originals.md) | Read what the proxy saved with `ai-proxy gain`, split it by project, and let an agent read originals back with `ai-proxy mcp`. |
| [turning-on-the-compression-stage.md](turning-on-the-compression-stage.md) | What the compression stage does to a request, how to turn it on, how to see that it ran, and how to get the original back. |

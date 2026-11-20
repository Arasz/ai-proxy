# Replaying stored requests

`ai-proxy replay` runs the requests the proxy already stored through today's compression pipeline
and prints what it would have saved. Use it to see the effect of a setting before you change the
proxy. It does not use the running proxy: it reads the data directory directly and changes nothing.

## What it reads

| Source | File | Table | Holds |
|---|---|---|---|
| `content` | `content.db` | `content_request.original` | The original of each request the proxy compressed. |
| `capture` | `capture.db` | `capture_request.body` | Each captured request body; zstd bodies are decoded. |

Both files are opened read-only. A request found in both sources is counted once; the second copy
shows as `duplicate`. Capture is off by default, so `capture.db` exists only when you turned it on.

## Run it

```
ai-proxy replay
ai-proxy replay --minimum 300 --since 2026-11-06
ai-proxy replay --source capture --json
```

| Option | Meaning |
|---|---|
| `--minimum <tokens>` | The smallest region, in estimated tokens, that becomes a preview. Without it, the proxy's own value (3,000) is used. |
| `--since <date>` | Replay only requests from this date on, in UTC. |
| `--source content\|capture\|both` | Which database to read. The default is `both`. |
| `--home <dir>` | The data directory. The default is the one the proxy uses (`Proxy__DataDirectory`, then `~/.ai-proxy`). |
| `--json` | Print the result as JSON instead of tables. |

## Read the result

The first table counts rows before it shows any saving, so you can see how much of the traffic the
numbers describe: rows read, undecodable (a zstd body that does not decode), not JSON, with no tool
region, misaligned (the regions of the forwarded body could not be paired with the original),
duplicate, replayed, and with a preview. Next to them are the capture rows read and the gain
requests in the same window.

The second table has one row per content kind: regions seen and previewed, and the estimated
tokens before and after. The last line sums the saving per preview id. A client that keeps no state
sends the same old tool result again on each turn, so one preview saves on every later request
that carries it.

Replay cannot see recall: it does not know how often the agent reads an original back. The saved
tokens are therefore an upper bound on the saving a preview gives.

Replay runs every request in stateless mode, so nothing is kept for retrieval. Token counts use the
same characters-per-token estimate as `ai-proxy gain`.

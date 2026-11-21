# Replaying stored requests

`ai-proxy replay` runs the requests the proxy already stored through today's compression pipeline
and prints what it would have saved. Use it to see the effect of a setting before you change the
proxy. It does not use the running proxy: it reads the data directory directly and changes nothing.

## What it reads

| Source | File | Table | Holds |
|---|---|---|---|
| `content` | `content.db` | `content_request.original` | The original of each request the proxy compressed. |
| `capture` | `capture.db` | `capture_request.body` | Each captured request body; zstd bodies are decoded. |

Both files are opened read-only. A body already counted (the same decoded bytes, so a zstd capture
copy of a content body too) shows as `duplicate`. Capture is off by default, so `capture.db`
exists only when you turned it on.

`content.db` keeps only the requests the pipeline changed. Without capture rows the sample misses
the requests a lower floor would newly preview, so replay prints a `coverage is content-only`
warning and sets `"coverage": "content-only"` in the JSON (`"capture"` otherwise). Turn capture on
(`Proxy__Capture=true`) and let it collect before you tune a floor.

The proxy never compresses a body that carries a content encoding (zstd), so replay leaves those
out by default and counts them as `encoded`. `--include-encoded` replays them anyway.

## Run it

```
ai-proxy replay
ai-proxy replay --minimum 300 --since 2026-11-06
ai-proxy replay --source capture --json
```

| Option | Meaning |
|---|---|
| `--minimum <tokens>` | The smallest region, in estimated tokens, that becomes a preview. Without it, the proxy's own value (3,000) is used. |
| `--budget-share <share>` | The share of a region's original tokens a preview may hold, above 0 and at most 1. The budget is the smaller of this share of the block's original tokens and 1,500 tokens. A preview that is not shorter than the reduced text is never sent. Without it, the proxy's own value (0.4) is used. |
| `--minimum-saving <tokens>` | The least a preview must save over the reduced text, in estimated tokens. Without it, the proxy's own value (0) is used. |
| `--skip-under <tokens>` | A numbered file read or a diff whose reduced text is under this many estimated tokens is not previewed, but is still reduced. `0` turns the skip off. Without it, the proxy's own value (0, off) is used. Regions under `--minimum` are not counted as skipped. The report counts these regions as `blocksSkipped`, in total and per kind. |
| `--since <date>` | Replay only requests from this date on, in UTC. |
| `--source content\|capture\|both` | Which database to read. The default is `both`. |
| `--include-encoded` | Replay bodies that carry a content encoding too. They are left out by default. |
| `--home <dir>` | The data directory. The default is the one the proxy uses (`Proxy__DataDirectory`, then `~/.ai-proxy`). |
| `--json` | Print the result as JSON instead of tables. |

## Read the result

The first table counts rows before it shows any saving, so you can see how much of the traffic the
numbers describe: rows read, undecodable (a zstd body that does not decode), not JSON, with no tool
region, misaligned (the regions of the forwarded body could not be paired with the original),
duplicate (a body already counted), encoded (left out), replayed, and with a preview. Next to them are the capture rows read and the gain
requests in the same window.

The second table has one row per content kind: regions seen and previewed, the estimated tokens
as sent, the tokens the reducers removed, the tokens the previews removed on top, and the tokens
forwarded. A region counts as previewed only when a preview changed it; a code view or a reducer
alone does not. A preview can be larger than the reduced text it replaces, and then its saving is
negative. The last line sums the preview saving per preview id. A client that keeps no state
sends the same old tool result again on each turn, so one preview saves on every later request
that carries it.

Replay cannot see recall: it does not know how often the agent reads an original back. The saved
tokens are therefore an upper bound on the saving a preview gives.

Replay runs every request in stateless mode, so nothing is kept for retrieval. It runs each request
twice, once with the preview floor out of reach and once with the floor you chose, to split the
reducer saving from the preview saving. Token counts are estimates of the tool output regions only,
at the same characters per token as `ai-proxy gain`; they are not comparable one to one with the
`gain` totals, which count whole requests.

The JSON also lists the rows per host (`hosts`; a content row has none, shown as `unknown`).

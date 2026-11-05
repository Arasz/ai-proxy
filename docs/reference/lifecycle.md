# lifecycle

`ai-proxy status` and `ai-proxy stop` look at, and end, the proxy that owns a data directory.
Both talk to the owner over its same-user control pipe (see
[ADR 0010](../adr/0010-status-and-stop-reuse-the-control-pipe.md)). Neither kills a process,
neither guesses from a listening port, and neither starts a proxy.

## Options

| Option | Default | Meaning |
|---|---|---|
| `--data-directory <PATH>` | `Proxy__DataDirectory`, then `~/.ai-proxy` | Which instance to address. |
| `-f, --format <text\|json>` | `text` | `status` only. |

## status

Prints the pid, start time and uptime, proxy URL, API URLs and data directory. The JSON form
has `running`, `pid`, `startUtc`, `proxyUrl`, `apiUrls` and `dataDirectory`. It never shows the
owner's shutdown nonce or process start token, and it takes no lock, so it is safe to run at
any moment.

| Exit | Meaning |
|---|---|
| 0 | An ai-proxy owner answered. |
| 3 | Nothing accepted a connection on the control pipe. An owner from before the pipe existed, or one still starting, reads as not running. On Windows the pipe takes one client at a time, so a running owner whose pipe another client holds for 3 seconds also reads as not running. |
| 1 | A peer connected but stayed silent for 3 seconds, or is not a compatible ai-proxy owner. |

During shutdown, `status` can still report an owner that has already accepted a stop request.

## stop

Asks the owner to shut down, then waits up to 30 seconds for that exact process to exit. The
normal shutdown path restores the system proxy. `stop` holds the startup claim for the whole
operation, so a start or restart launched meanwhile fails fast instead of racing it.

| Exit | Meaning |
|---|---|
| 0 | The owner stopped, or none was running (`ai-proxy is not running`). |
| 1 | Another start, restart or stop is in progress; the owner refused; its identity changed; it did not answer or exit in time; or its process is gone but something still holds the lease. Stop it manually. |

When the process lease is free, `stop` reports "not running" and leaves anything listening on
the proxy port alone, because a port cannot identify an ai-proxy owner.

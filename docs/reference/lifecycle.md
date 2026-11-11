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

## Crash recovery

When system-proxy integration is enabled, ai-proxy writes a private marker before changing the
current user's HTTP and HTTPS proxy. The marker stores the prior platform settings so a guard
process or the next start can restore them after a crash. Recovery runs only while ai-proxy
still owns the settings. Settings changed by another app are left alone.

- **Windows:** every manual proxy value, the PAC URL and the bypass list must hold either the value
  ai-proxy set or the saved value, and at least one must still be ai-proxy's. A restore that stopped
  partway can therefore be retried. An FTP or SOCKS entry that differs from the saved one counts as
  an outside change.
- **macOS and Linux:** both the HTTP and the HTTPS proxy must still point at the recorded endpoint.
  The bypass list is not compared.

At startup the log names the outcome: 1312 when the saved settings were restored, 1313 when they
were left alone because the settings changed, and 1314 when a marker from an older version (endpoint
only, no saved settings) was found and the proxy was cleared. An older marker is cleared only while
the proxy still points at its endpoint.

After restoration, ai-proxy reads the current settings again and compares them with the saved
snapshot. A mismatch keeps the marker and reports the failure so a later start can retry. Normal
shutdown uses the same ownership check. When the settings changed while ai-proxy ran, shutdown
restores nothing, logs 1315, and turns off Titanium's own restore so stopping or exiting cannot
overwrite the newer settings. If setting the proxy fails partway through startup, ai-proxy leaves
the marker so the next start can retry recovery.

The marker is written in a single flushed write before the proxy is set. A record cut short by a
crash during that write is discarded with warning 1316, because the proxy was never changed behind
it. On Windows the marker carries an access list that allows only the current user.

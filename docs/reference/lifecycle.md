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
process or the next start can restore them after a crash. Recovery runs only while the ownership
check below finds the settings still ai-proxy's. A change another app made is left alone when that
check can see it; a change it cannot see, such as a value set back to exactly what ai-proxy or the
saved settings hold, is restored over.

- **Windows:** every manual proxy value, the PAC URL and the bypass list must hold either the value
  ai-proxy set or the saved value, and at least one must still be ai-proxy's. A restore that stopped
  partway can therefore be retried. An FTP or SOCKS entry that differs from the saved one counts as
  an outside change.
- **macOS and Linux:** right after setting the proxy, ai-proxy reads the settings back and adds them
  to the marker. Each value the platform restores (HTTP and HTTPS proxy, their on/off state, the
  bypass list, and on macOS the SOCKS and PAC settings of every network service) must then hold
  either the value ai-proxy applied or the saved one, and at least one must still be ai-proxy's.
  A restore that stopped between HTTP and HTTPS is retried, while an outside change to any value,
  the bypass list included, is left alone. The process environment is not compared. A marker
  without the applied settings (written by an older version, or by a run that crashed before it
  recorded them) falls back to the older rule: HTTP and HTTPS must both still point at the
  recorded endpoint. So does a Linux desktop with neither GNOME nor KDE settings, where the applied
  settings record no system value.

At startup the log names the outcome: 1312 when the saved settings were restored, 1313 when they
were left alone because the settings changed, and 1314 when a marker from an older version (endpoint
only, no saved settings) was found and the proxy was cleared. An older marker is cleared only while
the proxy still points at its endpoint. Warning 1317 names the endpoints of an earlier ai-proxy run
that the settings about to be saved still pointed at; see below.

After restoration, ai-proxy reads the current settings again and compares them with the saved
snapshot. On macOS the host and port of a proxy that is switched off are not compared, because
macOS only flips the switch and keeps them. A mismatch keeps the marker and reports the failure so
a later start can retry. Normal
shutdown uses the same ownership check. When that check finds the settings changed while ai-proxy
ran, shutdown restores nothing, logs 1315, and turns off Titanium's own restore so stopping or
exiting cannot overwrite the newer settings. A change the check cannot see is restored over, as in
crash recovery.

If setting the proxy fails partway through startup, Titanium tries to put the saved settings back
itself, and ai-proxy leaves the marker so the next start can retry recovery. On Windows that retry works
for any mix of saved and applied values. On macOS and Linux the next start retries automatically
whenever the marker holds the applied settings (a restore cut short at shutdown or during
recovery), and also whenever HTTP and HTTPS both still point at the recorded endpoint. It does not
retry, and logs 1313, when only one of them still points there and the marker has no applied
settings: a startup that failed between HTTP and HTTPS, a marker from an older version, a crash
between setting the proxy and recording what was applied, or a Linux desktop without GNOME or KDE
settings.

Every start also cleans the settings it saves as the ones to restore. Each HTTP or HTTPS value that
is in effect and still points at an ai-proxy endpoint is replaced before the settings are saved: by
the same value from the settings the previous run saved, or, when there is none (or it points at
ai-proxy too), by turning that protocol off. A value points at ai-proxy when its port matches and
its host is `localhost`, a loopback address or the endpoint's own address. A proxy on another
machine that happens to use the same port is not touched. The endpoints checked are the previous
run's, from the marker, and this run's own. A value that is not in effect (a macOS proxy switched
off, GNOME outside manual mode, KDE with `ProxyType` other than 1, Windows with the proxy off or a
PAC URL set) is left as it is. The live settings are not changed at startup, because the proxy is
set right after; the cleaned settings are what shutdown, the guard and a failed set put back. When
something was replaced the log shows warning 1317 with the endpoints that matched. So a half-restored
proxy from a crashed run no longer outlives the next clean shutdown, and the manual fix is not
needed.

Two consequences follow. First, a proxy you set by hand to ai-proxy's own port is turned off at
shutdown, because it cannot be told apart from one ai-proxy left behind. Second, a shutdown that
logged 1315 deletes the marker, so the settings saved before that run are gone. If HTTPS still
pointed at ai-proxy then, the next start turns HTTPS off instead of putting back the proxy you had
before ai-proxy (a corporate proxy, say), and you set that one again by hand.

The marker is written in a single flushed write before the proxy is set. A record cut short by a
crash during that write is discarded with warning 1316, because the proxy was never changed behind
it. The applied settings are added after the proxy is set by rewriting the record in place, so the
longer record starts with the shorter one. A crash during that second write leaves the endpoint and
saved settings readable, and recovery uses the older rule. On Windows the marker carries an access list that allows only the current user.

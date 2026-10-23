# Research: reading and restoring the system proxy after a crash

**Date:** 2026-10-23
**Question:** How can ai-proxy read the current per-user system proxy on macOS, Windows and Linux (directly or through .NET), and what do existing tools do to recover the setting after the proxy process is killed?

Context: issue #21. After SIGKILL the system proxy still points at `127.0.0.1:<port>`, and the
next start snapshots that stale entry as the original. The chosen direction is option (c): a
marker file, and clearing the proxy when a previous run left it set.

## Findings

### F1 — .NET returns one cached proxy object per process, and env vars override the OS setting [MEASURED]

`HttpClient.DefaultProxy` was `System.Net.Http.MacProxy` with no proxy env vars set. It became
`HttpEnvironmentProxy` once `HTTPS_PROXY` was set. `WebRequest.GetSystemWebProxy()` returned the
same instance as `DefaultProxy` on both calls. So .NET cannot tell ai-proxy what the OS proxy is
when the caller's environment carries a proxy variable.

**Evidence:** scratchpad `probe.cs` run with `dotnet run probe.cs` (SDK 10.0.402, macOS 27.0.1).
It ran once with all `*_proxy` vars unset and once with `HTTPS_PROXY=http://127.0.0.1:9`.
It printed the type, `ReferenceEquals` and `GetProxy(https://api.anthropic.com/)`.

### F2 — The runtime builds the system proxy lazily, once, and `GetSystemWebProxy` returns it [READ]

`HttpClient.DefaultProxy` initializes from `SystemProxyInfo.Proxy`, a `Lazy<IWebProxy>`.
`WebRequest.GetSystemWebProxy()` returns `HttpClient.DefaultProxy`. Every OS factory tries
`HttpEnvironmentProxy` first.

**Evidence:** dotnet/runtime `main` (per research lane):
- `src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs` (~18, 39-45)
- `.../SocketsHttpHandler/SystemProxyInfo.cs:8-10`
- `src/libraries/System.Net.Requests/src/System/Net/WebRequest.cs:548`
- `SystemProxyInfo.OSX.cs`, `.Windows.cs`, `.Unix.cs`

### F3 — How live the cached object is depends on the OS [READ]

- **macOS:** `MacProxy` calls `CFNetworkCopySystemProxySettings` on every `GetProxy`, so it sees
  changes live.
- **Windows:** `HttpWindowsProxy` watches the Internet Settings key with `RegNotifyChangeKeyValue`
  and reads it through `WinHttpGetIEProxyConfigForCurrentUser`.
- **Linux:** .NET reads only env vars, never gsettings or KDE, and freezes them at first use.

None of these exposes raw settings (host, port, enable flag). They only resolve one URI at a time.

**Evidence:** dotnet/runtime `main`, `.../SocketsHttpHandler/MacProxy.cs:84`,
`HttpWindowsProxy.cs` ctor (~35-45), `WinInetProxyHelper.cs` (~28), `SystemProxyInfo.Unix.cs`
(per research lane).

### F4 — On macOS the setting is per network service; reading needs no privilege [MEASURED]

`scutil --proxy` printed the merged global view (here only `ExceptionsList` and `FTPPassive`).
`networksetup -listallnetworkservices` listed three services. `-getwebproxy` and
`-getsecurewebproxy Wi-Fi` each printed `Enabled: No`. All of these ran as a normal user.

**Evidence:** run on this Mac (macOS 27.0.1), standard user shell, no proxy configured.

### F5 — `networksetup` writes need an admin-group user, or root when system preferences are locked [READ]

**Evidence:** `man networksetup` (per research lane): "requires at least admin privileges to
change network settings".

### F6 — Titanium.Web.Proxy 7.0.26 has a public `TryDisableSystemProxy` but no public read of the current system proxy [MEASURED]

Its public members are `SetAsSystemProxy`/`TrySetAsSystemProxy`,
`DisableSystemProxy`/`TryDisableSystemProxy`, `DisableSystemHttp(s)Proxy` and
`RestoreOriginalProxySettings`/`TryRestoreOriginalProxySettings`. None of them returns the
current setting.

**Evidence:** scratchpad `meta.cs` used `System.Reflection.Metadata` to list the type and method
definitions of `src/AiProxy/bin/Release/net10.0/Titanium.Web.Proxy.dll`, filtered on `Proxy`.

### F7 — Titanium keeps its original-settings snapshot in memory only, on all three backends [READ]

Each backend restores on ProcessExit, unhandled exceptions or signals:
- **Windows:** `originalValues`.
- **macOS:** `MacOsSystemProxyBackend._original`.
- **Linux:** gsettings/KDE snapshots plus `_originalEnv`.

A SIGKILL skips all of these restores. No persisted snapshot was found (keyword scan, not a full
read), and no upstream issue covers a stale proxy after a crash.

**Evidence:** justcoding121/titanium-web-proxy `develop`,
`src/Titanium.Web.Proxy/Helpers/{SystemProxy,MacOsSystemProxyBackend,LinuxSystemProxyBackend}.cs`.
Issue search used the queries "proxy crash", "system proxy restore" and "proxy still set"
(per research lane).

### F8 — No .NET package offers cross-platform, crash-safe system-proxy handling [READ]

NuGet search found only Windows WinINet bindings:
- `Vanara.PInvoke.WinINet` 5.0.7 (~193k downloads)
- `WinInetHelper`
- `Riverside.Win32.Networking.Internet`

None persists state. Outside .NET, `sysproxy-rs` (used by Clash Verge) and `getlantern/sysproxy`
only get and set. Neither documents crash safety.

**Evidence:** nuget.org search API, github.com/zzzgydi/sysproxy-rs README,
github.com/getlantern/sysproxy README (per research lane).

### F9 — Charles documents the same failure and says it recovers on next launch [READ]

"[Charles] doesn't have an opportunity to reset your proxy settings. It should notice that when
it is restarted." Its mechanism is closed source.

**Evidence:** https://www.charlesproxy.com/documentation/faqs/can-no-longer-browse-without-charles-running/

### F10 — HTTP Toolkit avoids the problem by never setting the system proxy [READ]

It launches preconfigured browsers and injects env vars into terminals.

**Evidence:** https://httptoolkit.com/docs/reference/intercept-page/

### F11 — Detecting the crash needs nothing from the OS proxy APIs [INFERRED]

From F3 and F6: there is no portable read of the OS setting. From F7: the original is gone after
SIGKILL anyway. So the stale state can be detected from ai-proxy's own data instead:
- Before `Set`, write a marker under `~/.ai-proxy`.
- Hold it open with `FileShare.None`. On Unix .NET backs that with `flock`, which the kernel drops
  when the process dies.
- Delete the marker on clean restore.

At the next start, a marker that exists but is not locked means the previous run died with the
proxy set. A locked marker means another instance is live. Clear the proxy through
`TryDisableSystemProxy` before Titanium takes its snapshot.

The `flock` behaviour is from memory and is checked by a test in the implementation (Still open).

### F12 — Full-fidelity restore (option a) means three OS backends ai-proxy would own [INFERRED]

From F4, F5 and F7 plus the lane's notes:
- **macOS:** read and write every network service.
- **Windows:** write the registry values and the `DefaultConnectionSettings` blob, then broadcast
  `INTERNET_OPTION_SETTINGS_CHANGED`.
- **Linux:** handle both gsettings and KDE.

Titanium already contains this code privately. Duplicating it is the cost of keeping a corporate
proxy that a crash overwrote.

### F13 — A child reading a pipe from its parent sees end-of-file the moment the parent is SIGKILLed [MEASURED]

The child was started in its own session and blocked on reading stdin. Its read returned 1.00s
after it started, which equals the parent's sleep before `SIGKILL`, so there was no detection
delay. The child was then owned by launchd (`ppid 1`). Pipe end-of-file is therefore a portable
"parent died" signal that needs no polling and cannot be fooled by PID reuse.

**Evidence:** scratchpad `parent.py`/`child.py` (Python 3.15, macOS 27.0.1).
- `Popen(stdin=PIPE, start_new_session=True)`.
- The parent then ran `os.kill(os.getpid(), SIGKILL)`; exit code 137.
- The child wrote `EOF after 1.00s, ppid now 1`.

Not yet run from a .NET `Process.Start` pipe.

### F14 — Titanium sets the system proxy only for an endpoint registered in the same running `ProxyServer` [READ]

`TrySetAsSystemProxy` calls `ValidateEndPointAsSystemProxy`. That throws "Cannot set endPoints
not added to proxy as system proxy" unless `ProxyEndPoints.Contains(endPoint)` and
`ProxyRunning` both hold. A sidecar that does not own the listener cannot call `Set`. Setting
HTTPS also calls `CertificateManager.EnsureRootCertificate()` and silently drops HTTPS when the
root is not validated.

**Evidence:** justcoding121/titanium-web-proxy `v7.0.26`:
- `src/Titanium.Web.Proxy/ProxyServer.cs:1748-1756`
- `src/Titanium.Web.Proxy/ProxyServer.SystemProxy.cs:108-133`

### F15 — Titanium's backends snapshot only as a side effect of a write, and the side effect differs by OS [READ]

`TryDisableSystemProxy(protocol)` calls the backend's `RemoveProxy`, which snapshots first. With
`ProxyProtocolType.None`:
- **macOS:** it snapshots and then writes nothing. Each write is guarded by a protocol flag.
- **Windows:** it rewrites `ProxyEnable=1` whenever `ProxyServer` holds a value. That turns on a
  proxy the user had configured but disabled.
- **Linux:** it disables GNOME, KDE and the env vars whatever the protocol.

So a side-effect-free snapshot through Titanium's public API exists on macOS only.
`TryDisableSystemProxy` itself needs no registered endpoint and no running server, so a sidecar
can always *clear*.

The macOS backend also restores its snapshot on `ProcessExit` and `UnhandledException`, and
writes through `networksetup` with an elevation fallback.

**Evidence:** titanium-web-proxy `v7.0.26`:
- `Helpers/MacOsSystemProxyBackend.cs:30-38, 82-100, 220-234`
- `Helpers/SystemProxy.cs:189-219`
- `Helpers/LinuxSystemProxyBackend.cs:155-222`
- `ProxyServer.SystemProxy.cs:276-292`

### F16 — A sidecar can restore the true original only if ai-proxy captures the snapshot itself [INFERRED]

From F14 and F15: the sidecar cannot `Set`. On Windows and Linux it cannot snapshot through
Titanium without changing settings, and the parent's snapshot is private and dies with it. So the
choice is between:
- a sidecar that clears, using Titanium's public API on all OSes;
- a sidecar that restores from ai-proxy's own snapshot. The per-OS reads and writes are F4
  (macOS), the registry values (Windows) and gsettings/KDE (Linux). Written to `~/.ai-proxy`,
  that snapshot file can also serve as the crash marker.

## Still open

- The Windows registry, `DefaultConnectionSettings` and `InternetSetOption` details come from
  the lane's own knowledge and were not fetched from Microsoft Learn. Settle that before any
  option (a) work.
- Whether `TryDisableSystemProxy` on macOS clears every service or only the ones Titanium touched,
  and whether it needs admin (F5). A manual run on a test machine would settle it, but tests must
  never change the OS proxy.
- Whether .NET's `FileShare.None` lock is released on SIGKILL on macOS and Linux
  (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` turns it off). A test that kills a child process holding
  the marker would settle it.
- How Charles, Proxyman, Fiddler, Clash Verge (its "sysproxy guard") and v2rayN detect the stale
  state. They were not read.
- Whether mitmproxy sets the system proxy at all.

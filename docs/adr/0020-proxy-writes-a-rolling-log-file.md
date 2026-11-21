# 0020 - The proxy writes a rolling log file

Status: Accepted (owner rulings 2026-11-21, project review)

Amends: none. It adds a log destination beside the console; no earlier ADR rules logging output.

## Context

The proxy builds its host with `WebApplication.CreateBuilder`
(`src/AiProxy/Hosting/Proxy/ProxyInstance.cs:15`) and keeps the default console logger. It
configures no other provider. Only the MCP child sets up logging on purpose, to stderr
(`src/AiProxy/Mcp/McpServerProcess.cs:47-52`). The proxy is not detached, so when its terminal
closes, or when it runs with output discarded, every warning reaches nobody.

That is the case today. The live instance has run with output discarded since 2026-11-21
10:49 UTC, so the capture-stop warning (EventId 1502, `src/AiProxy.Storage/SqliteCaptureStore.cs:249`)
and capture eviction failures (EventId 1622,
`src/AiProxy/Hosting/Integration/CaptureEvictionWorker.cs:48`) go nowhere
([project review](../work/2026-11-21-project-review.md) F25). This is how F3 would have gone
unnoticed.

The source has 56 `[LoggerMessage]` methods (`grep -rn "\[LoggerMessage" src`, 2026-11-21). They
need a sink, not a new logging API.

## Decision

1. The proxy writes every log event that reaches its console to `<data-directory>/proxy.log` as
   well. The console output does not change.
2. At a fixed size the file rolls to `proxy.1.log`, which replaces any earlier `proxy.1.log`. Two
   files is the whole history, so the two together never exceed twice the fixed size. The size
   is a constant, not a setting, and `docs/reference/lifecycle.md` states it.
3. Each line holds the UTC time, the level, the EventId, the category and the message, then the
   exception text when there is one.
4. On POSIX the file is created with mode 0600 inside the 0700 data directory.
5. A small in-house `ILoggerProvider` in `src/AiProxy/Hosting/Proxy/` writes the file. It adds no
   package and no configuration section.
6. After this ships, the live proxy starts without a shell redirect to `~/.ai-proxy/proxy.log`.
   A redirect to the same file would write each line twice.

## Consequences

Positive: capture-stop, eviction and relay warnings survive a closed terminal. Package P4's
eviction events and P13's capture state have a place to be checked after the fact.

Negative: one more file in the data directory, and log write cost on every event. A burst of
warnings rolls the file and loses older lines; the fixed size bounds disk use, not history.

Neutral: the log can hold host names, paths and request ids. It holds no headers or bodies,
because no log method takes them, and the 0600 mode keeps it user-only on POSIX.

## Alternatives considered

- Serilog with a file sink: rejected. It adds two packages and a second configuration surface for
  one file, and the 56 source-generated log methods need only a sink.
- Detach the proxy and leave logging to the service manager (launchd, systemd): rejected for now.
  It changes the lifecycle of ADR 0007 and differs per platform.
- Keep the console and tell users to redirect it: rejected. That is the setup that lost the
  warning today.

## Sources

- [Project review of 0.34.0](../work/2026-11-21-project-review.md), F25 and F3
- [Review fix plan](../work/2026-11-21-project-review-plan.md), package P3 and release step V
- Code: `src/AiProxy/Hosting/Proxy/ProxyInstance.cs`, `src/AiProxy/Mcp/McpServerProcess.cs`,
  `src/AiProxy.Storage/SqliteCaptureStore.cs`, `src/AiProxy/Hosting/Integration/CaptureEvictionWorker.cs`

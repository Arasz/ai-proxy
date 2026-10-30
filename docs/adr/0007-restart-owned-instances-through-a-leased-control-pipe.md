# 0007 - Restart owned instances through a leased control pipe

Status: Accepted (2026-10-27)

## Context

An installed proxy may already be running when a user starts a newer executable. A listening
port or a saved PID does not establish that the process is ours. The system proxy marker also
cannot identify instances that disabled operating system proxy integration.

## Decision

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/Proxy/ProxyInstanceLifetime.cs:40#RestartFlag -->
The executable acquires a nonwaiting, exclusive `startup.lock` before observing the process
lease. Normal starts and restarts share this claim and release it after the host and pipe are
ready. Concurrent contenders fail instead of discovering and stopping a new replacement.

An independent `process.lock` uses `FileShare.None` and outlives host and pipe disposal. Acquire
it before stores, certificates, listeners, or pipe construction. Both lock files stay on disk;
deleting a locked inode would let contenders lock a different file at the same path.

`--restart` with no owner follows normal startup. Otherwise it validates the supplied settings
and requested endpoints, checks the control peer, requests graceful shutdown, waits for the
exact old process to exit, and acquires the process lease before starting its replacement.
Absent explicit keys, inherit the actual proxy port and API URLs. The existing system proxy
marker and guard retain their restoration responsibilities.
<!-- /trust:trustchecked -->

<!-- trust:trustchecked evidence=src/AiProxy/Hosting/Proxy/ProxyControl.cs:11#ProxyControl -->
The control transport is one reusable duplex byte-mode named pipe. Its short name uses BCL
SHA-256 over the normalized absolute data directory. Both ends require `CurrentUserOnly`.
The server reports product, protocol, nonce, PID, UTC start time, and actual listener URLs.
The client validates identity before sending a stop request with the expected nonce on the
same connection. The server flushes its acknowledgement before calling `StopApplication`.
An acknowledgement alone never completes restart.

Frames have a 16 KiB byte limit and each connection has a three-second deadline. Malformed,
incomplete, oversized, or stalled clients lose their connection; the same server disconnects
and accepts the next client. Process exit has a separate thirty-second deadline. Failures
require operator action; the executable never signals or kills proxy processes.
<!-- /trust:trustchecked -->

## Consequences

A loopback shutdown endpoint, persistent secret, and PID record are unnecessary. There is no
background supervisor. Unix pipe constructors can unlink stale socket paths, so lease ordering
is part of correctness. Symlink aliases are not treated as equivalent data directories.

Legacy versions require manual stopping when their system proxy marker is held. Unix `lsof`
or Windows Restart Manager can report the PID of a single process with that marker open;
unavailable or ambiguous results produce no PID. Without that
marker, occupied known endpoints can reveal conflicts, but unknown legacy ports cannot be
identified without scanning. Port checks authorize no shutdown and cannot prevent a later bind race.

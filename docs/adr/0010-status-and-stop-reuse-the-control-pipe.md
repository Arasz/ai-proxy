# 0010 - Status and stop reuse the control pipe

Status: Accepted

## Context

[ADR 0007](0007-restart-owned-instances-through-a-leased-control-pipe.md) lets `--restart`
replace an owned instance, but a user who only wants to see or end it must find the process by
hand. A listening port or a saved PID does not prove a process is ours, and killing a guess is
worse than doing nothing.

## Decision

`ai-proxy status` reads the control pipe's `info` reply and takes no lock. It shows a view
without the nonce or process start token. Exit 0 means an owner answered, 3 means no peer, and
1 means a silent or incompatible peer.

`ai-proxy stop` takes the nonwaiting startup claim for the whole operation, so a concurrent
start or restart fails fast. If the process lease is free it reports "not running" and exits 0.
Otherwise it reuses `ProxyControl.StopOwnedAsync`: compatibility and identity checks, the
nonce-bearing stop request, then a wait for that exact process to exit. Refusal, identity
mismatch, timeout or a missing peer exit 1 and ask for a manual stop. It never kills, never
probes ports and never builds a pipe server.

## Consequences

Both commands inherit ADR 0007's guarantees and its limit: an owner that predates the pipe is
invisible and must be stopped by hand. `status` can report an owner that has already accepted
a stop. Waiting for exit keeps the startup claim held for up to 30 seconds. The exit codes are
part of the contract scripts can rely on.

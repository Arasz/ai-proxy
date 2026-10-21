# Security policy — ai-proxy

**Do not open a public issue for a security problem.** Report through GitHub's private vulnerability reporting, or by email.

What to include: what an attacker can do, the affected file, a reproduction command, the version (root `VERSION` file or commit). What to expect: one maintainer, no on-call, best effort, fix as a version bump.

Supported versions: only the latest tagged release. There is no shipped version yet — the proxy is not implemented — so there is nothing to report against until `VERSION` is tagged.

## What this project is, security-wise

ai-proxy is a local proxy that sits between an agent and its LLM provider, and forwards the caller's own provider credential upstream. That makes credential handling the central concern.

**F4 (credential substitution, and the refusal that goes with it).** When a request's provider is detected from the body rather than the path, the proxy must substitute its own credential (`PROXY_ANTHROPIC_KEY` etc.) for that dialect, or refuse with `routing_credential_ambiguous`. A request whose provider was guessed must never carry a caller's credential to the wrong host. Measured against a fake Anthropic upstream: the path-named case kept the caller's key (`Bearer sk-ant-CALLER`), the body-detected case with a proxy key sent `Bearer PROXY-ANTHROPIC-KEY`, and the case with no proxy key returned 404. A path and a body naming different providers are refused the same way, and a path the proxy cannot place is refused with a 404 naming what was unknown — the refusal is the point, because the alternative is a credential on a host nobody chose. The proxy is not yet implemented, so this is the design rule, not a claim of current behaviour.

**F3 (leak measurement).** The sibling `rtk-proxy` 1.0.1 (BUSL-1.1, no code vendored from it) forwards a body that contradicts its path, so the caller's OpenAI credential (`sk-openai-KEY`) lands on the Anthropic host. ai-proxy must refuse that case rather than replicate it.

**F6 (path-prefix rules).** Traffic that belongs to no provider — an MCP server such as ai-raccoon — is carried by an operator-configured path-prefix rule, not by a dialect rule. The operator names the prefix and its upstream, so nothing about the destination is guessed.

The proxy binds loopback (single-operator local process) and holds no account, no hosted service, and no network surface beyond `localhost`. No rtk code is vendored; the design is from the research record only.

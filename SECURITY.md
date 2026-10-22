# Security policy — ai-proxy

**Do not open a public issue for a security problem.** Report through GitHub's private vulnerability reporting, or by email.

What to include: what an attacker can do, the affected file, a reproduction command, the version (root `VERSION` file or commit). What to expect: one maintainer, no on-call, best effort, fix as a version bump.

Supported versions: only the latest tagged release. There is no shipped version yet — the proxy is not implemented — so there is nothing to report against until `VERSION` is tagged.

## What this project is, security-wise

ai-proxy is a local proxy that sits between an agent and its LLM provider, and forwards the caller's own provider credential upstream. That makes credential handling the central concern.

**Credential handling.** The proxy does not hold provider credentials. It forwards credential headers from the incoming request unchanged, regardless of whether the provider was identified by path, body or headers. If the path and body name different providers, the proxy refuses the request before forwarding it. Operators must configure clients with credentials for the provider the request resolves to.

**F3 (leak measurement).** The sibling `rtk-proxy` 1.0.1 (BUSL-1.1, no code vendored from it) forwards a body that contradicts its path, so the caller's OpenAI credential (`sk-openai-KEY`) lands on the Anthropic host. ai-proxy must refuse that case rather than replicate it.

**F6 (path-prefix rules).** Traffic that belongs to no provider — an MCP server such as ai-raccoon — is carried by an operator-configured path-prefix rule, not by a dialect rule. The operator names the prefix and its upstream, so nothing about the destination is guessed.

The proxy binds loopback (single-operator local process) and holds no account, no hosted service, and no network surface beyond `localhost`. No rtk code is vendored; the design is from the research record only.

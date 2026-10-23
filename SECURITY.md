# Security policy: ai-proxy

Report security problems through GitHub's private vulnerability reporting or by email. Include
what an attacker can do, the affected file, a reproduction command, and the version from the
root `VERSION` file. Only the latest tagged release is supported.

## Trust boundary

ai-proxy listens on loopback and changes the current user's system HTTP and HTTPS proxy while
the process runs. It restores the previous settings during normal shutdown. A forced kill or
machine crash can prevent that cleanup, so check the system proxy settings if the process did
not exit normally.

The proxy decrypts HTTPS only for `api.openai.com`, `api.anthropic.com`,
`generativelanguage.googleapis.com`, and `openrouter.ai`. It creates a per-user root certificate
and asks the operating system to trust it. This lets the proxy read provider request bodies,
including credentials, and means the generated root private key must remain protected on the
local machine. HTTPS to other hosts stays in a CONNECT tunnel, and other HTTP destinations are
forwarded without request-body inspection.

The proxy does not own provider API keys. It forwards the caller's credential headers to the
addressed provider. There is no host allowlist for other destinations: software using the
system proxy can send arbitrary HTTP traffic through it. Clients that ignore system proxy
settings, pin provider certificates, or do not trust user-installed roots may bypass the proxy
or fail their HTTPS request.

Compression is opt-in. When enabled, original request bodies that were changed are stored in
the local content database and can be retrieved through the local content endpoint. Protect
that database as agent data.

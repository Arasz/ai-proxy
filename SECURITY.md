# Security policy: ai-proxy

Report security problems through GitHub's private vulnerability reporting or by email. Include
what an attacker can do, the affected file, a reproduction command, and the version from the
root `VERSION` file. Only the latest tagged release is supported.

## Trust boundary

ai-proxy listens on loopback and changes the current user's system HTTP and HTTPS proxy while
the process runs. It restores the previous settings during normal shutdown. After a forced
kill, a small guard process clears the system proxy; after a reboot or power loss, the next
start clears it before setting it again. In those cases the proxy is cleared and not restored,
so a proxy you had configured before the run is gone and you must set it again.
The clear is unconditional: a proxy you set again after the crash and before the next start is
cleared as well. A restore that fails on shutdown is logged at error level and leaves the
marker in place, so the guard or the next start clears the proxy; the macOS backend swallows
some of its own errors, so a few failed restores cannot be seen.

The proxy decrypts HTTPS only for the provider API hosts listed in
[environment variables](docs/reference/environment-variables.md): `api.openai.com` and its
documented regional hosts, `api.anthropic.com`, `generativelanguage.googleapis.com`,
`openrouter.ai` and `eu.openrouter.ai`. Hosts match exactly, so `mcp.openrouter.ai` stays a
tunnel. It creates a per-user root certificate
and asks the operating system to trust it. This lets the proxy read provider request bodies,
including credentials, and means the generated root private key must remain protected on the
local machine. HTTPS to other hosts stays in a CONNECT tunnel, and other HTTP destinations are
forwarded without request-body inspection.

The root certificate and its private key are kept in `~/.ai-proxy/root-ca.pfx`. On Linux and
macOS the directory is created (or tightened) to mode `0700` and the file to `0600`, so file
permissions are the main protection. Set `PROXY__ROOTCERTIFICATEPASSWORD` to also encrypt the
pfx; changing or removing the password later makes the existing file unreadable, and ai-proxy
generates a new root that has to be trusted again. Per-host leaf certificates live in memory
only. ai-proxy adds the root to the operating-system trust store only when root trust is
enabled; the test suite never touches the trust store or the system proxy settings.

The proxy does not own provider API keys. It forwards the caller's credential headers to the
addressed provider. There is no host allowlist for other destinations: software using the
system proxy can send arbitrary HTTP traffic through it. Clients that ignore system proxy
settings, pin provider certificates, or do not trust user-installed roots may bypass the proxy
or fail their HTTPS request.

Compression is opt-in. When enabled, original request bodies that were changed are stored in
the local content database and can be retrieved through the local content endpoint. Protect
that database as agent data.

Request capture is opt-in too. With `PROXY__CAPTURE=true`, the full body of every provider request
the proxy reads is stored locally in `~/.ai-proxy/capture.db`, whether or not compression changed
it. That is prompts, code and tool output, and any secret pasted into them. Request headers and
the query string are not stored, so credentials sent as headers or a `?key=` parameter are not
kept. The directory is `0700` and the file `0600` on Linux and macOS. Turn capture off and delete
the file when you no longer need it.

## Third-party proxy engine

HTTPS interception and system proxy changes are done by `Titanium.Web.Proxy` 7.0.26, built
from the MIT-licensed justcoding121/titanium-web-proxy repository; its NuGet id prefix is not
verified and its nuspec carries no repository or license metadata (see
[ADR 0003](docs/adr/0003-machine-wide-forward-proxy-and-provider-inspection.md#dependency-provenance)).
It has access to the root CA key and to decrypted provider traffic. Report problems that
come from it here as well.

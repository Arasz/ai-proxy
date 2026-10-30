# 0009 - ai-proxy makes its own name-constrained root

Status: Accepted (owner ruling R2, 2026-10-30)

Supersedes, in [ADR 0003](0003-machine-wide-forward-proxy-and-provider-inspection.md):

- §Decision, "using a per-user root certificate". The root is still per user, but ai-proxy now
  makes it, and it is constrained to the provider domains.
- §Dependency provenance, the implication that Titanium makes and stores the root CA key. Titanium
  still signs the leaf certificates. It no longer creates, loads or saves the root.

Every other part of ADR 0003 stands.

## Context

Titanium made the inspection root and saved it with an empty password. The root had no name
constraint, so the trusted key could sign a certificate for any site the machine visits. When
loading the pfx failed, Titanium silently made a new root and saved it over the old file
(project-scope review, F8). Its leaf certificates were wildcards such as `*.anthropic.com`.

## Decision

1. ai-proxy generates the root with `CertificateRequest`: CN `ai-proxy local inspection CA`.
   It carries a critical NameConstraints extension that:
   - permits the registrable parent of each host in the injected `IProviderHosts` source;
   - excludes all IPv4 and IPv6 space, except addresses the host source itself names. Only
     loopback test sources name addresses.
2. The root is handed to Titanium before Titanium's `CreateRootCertificate` can run, so Titanium
   never writes a root. A pfx that no known password opens stops startup and stays on disk.
3. Leaf certificates are never wildcards.
4. The pfx has a random password, kept beside it with mode 0600. A configured
   `Proxy:RootCertificatePassword` wins.
5. Migration from an old root is one-time and by thumbprint: trust the new root, then untrust
   that one old certificate.

## What was measured, and what was not

- Measured on macOS: a leaf for a provider host chain-builds; a leaf for `evil.example` or for an
  IP address fails. macOS reports `InvalidNameConstraints` for both.
- Not measured: the Linux chain engine. The test expects `HasNotPermittedNameConstraint` there,
  and CI on `ubuntu-latest` is the first run. Windows is not tested.
- The migration was tested against a recording trust fake. No test touched a real store, so the
  real keychain prompt and `security delete-certificate` run first on the owner's machine.

## Consequences

- Upgrading costs one trust prompt and a restart of every harness session.
  `NODE_EXTRA_CA_CERTS` is read once, when a client starts.
- Adding a provider domain later needs a root rotation: delete `root-ca.pfx` and restart.
  A constrained root that is still valid is kept as it is.
- On Linux, NSS databases keep the old root until it is removed by hand.

## Rejected

- **A `ca remove-stale` command that untrusts roots by common name.** Titanium's own helpers
  match by name, so it would also remove other Titanium tools' roots.
- **Exact-host constraints built at compile time.** Loopback inspection tests inject their own
  host source. A compile-time list would remove that seam or force tests through the real
  catalog.
- **Keeping Titanium's root and adding only a password.** The password protects the file at rest,
  but the root still vouches for every site.

# 0017 - The root names exact hosts and rotates once

Status: Accepted (owner rulings 2026-11-21, project review)

Amends: [ADR 0009](0009-ai-proxy-owns-a-name-constrained-root.md), rule 1 (the root permits the
registrable parent of each host) and the rejected-alternatives text about the pfx password
(0009, line 76).

## Context

The live root carries `Permitted: DNS:openai.com, DNS:anthropic.com, DNS:googleapis.com,
DNS:openrouter.ai, DNS:chatgpt.com` ([project review](../work/2026-11-21-project-review.md) F5,
read with `openssl x509` on `~/.ai-proxy/root-ca.pem`). A process that reads `root-ca.pfx` and its
password can mint a certificate for `oauth2.googleapis.com`, `auth.openai.com` or any page of
`chatgpt.com`, which the proxy never decrypts.

The parent comes from `PermittedDomains`, which keeps the last two labels of each host
(`src/AiProxy/Hosting/Integration/RootCertificateAuthority.cs:128-137`, `TakeLast(2)` at `:133`).
The hosts come from the injected `IProviderHosts.Hosts`
(`src/AiProxy/Hosting/IProviderHosts.cs:12`), which the canonical source fills with the 16 hosts
of `src/AiProxy.Core/Providers/ProviderHostCatalog.cs:9-24`, `chatgpt.com` included. ADR 0009
chose the parent on purpose and rejected only a compile-time host list (0009, lines 72-74). Exact
hosts from the injected source were not considered, and the seam that ADR wanted to keep is the
one this record uses.

Rotation already has a path. `RootCertificateFiles.Load` keeps a root only when
`PermitsExactly(current, hosts)` holds (`src/AiProxy/Hosting/Integration/RootCertificateFiles.cs:34`),
and otherwise creates a new one and returns the old thumbprint (`:40-42`). At start the service
trusts the new root and untrusts that one thumbprint
(`src/AiProxy/Hosting/Integration/TitaniumProxyService.cs:139-147`), which is ADR 0009 rule 5. So
a change of the constraint rule makes every existing root fail the check once.

ADR 0009 says of the pfx password: "The password protects the file at rest" (0009, line 76). It
does not. The generated password is written to `root-ca.password` in the same 0700 directory as
the pfx (`RootCertificateFiles.cs:51-61`), so any process that can read the pfx can read the
password too.

## Decision

1. The root's NameConstraints extension permits each host that `IProviderHosts.Hosts` returns,
   exactly as named, lower case, without a trailing dot. Provider hosts and harness-only hosts
   are both in that set, so `chatgpt.com` stays. It permits no registrable parent and no
   wildcard. The IP exclusions of ADR 0009 rule 1 stay as they are.
2. At start, a root whose permitted subtrees differ from the current host set is replaced. The
   existing ADR 0009 rule 5 path does the work: trust the new root (one prompt on macOS), then
   untrust the old root by its thumbprint, then delete its files. A start with a matching root
   does nothing.
3. Leaf certificates signed by the old root are not reused after a rotation. The service clears
   Titanium's leaf cache when it replaces the root.
4. A new decrypted host means a new root and one more trust prompt. Adding a host to the catalog
   is therefore a release note item.
5. `doctor` reports a warning when the trusted root permits a name outside the current host set.
6. Correction to ADR 0009: the stored pfx password gives no protection at rest against a process
   that runs as the same user. It only stops a tool that opens the pfx with an empty password,
   as Titanium did. The 0700 directory is the real boundary.

## Consequences

Positive: a stolen pfx can sign only for the 16 hosts the proxy already decrypts. An auth host
such as `auth.openai.com` is out of reach.

Negative: every user sees one more trust prompt on the first start of the release. Each new
provider host costs a prompt too, where today a new subdomain of a known parent costs none. A
regional host that the catalog does not name (for example a new `xx.api.openai.com`) is not
inspected until the catalog and the root both change.

Neutral: the leaf cache behaviour of Titanium 8.0.2 across a root change is not yet checked
against its source. Rule 3 states the outcome, and the P7 gate proves it either way.

## Alternatives considered

- Narrow new roots only and tell the user to delete the old one: rejected by the owner's ruling
  (detect and replace). The wide root would stay trusted beside the narrow one.
- Keep the registrable parent and add `excludedSubtrees` for known auth hosts: rejected. An
  exclusion list is open-ended, and a missed host is a silent gap.
- A compile-time host list: still rejected, for the ADR 0009 reason. Tests inject their own host
  source.

## Sources

- [Project review of 0.34.0](../work/2026-11-21-project-review.md), F5
- [Review fix plan](../work/2026-11-21-project-review-plan.md), "Decision records before code" and
  package P7
- Code: `src/AiProxy/Hosting/Integration/RootCertificateAuthority.cs`,
  `src/AiProxy/Hosting/Integration/RootCertificateFiles.cs`,
  `src/AiProxy/Hosting/Integration/TitaniumProxyService.cs`, `src/AiProxy/Hosting/IProviderHosts.cs`,
  `src/AiProxy.Core/Providers/ProviderHostCatalog.cs`

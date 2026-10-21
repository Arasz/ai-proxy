# Releasing ai-proxy

A `git push` is not a release. The release is a deliberate, separate step, and this file describes
the whole of it.

## What ships

ai-proxy ships as a .NET tool. A release produces a NuGet package that installs as a global tool:

```bash
dotnet tool install -g ai-proxy
```

A RID-specific payload may be needed per runtime identifier, so the pack step has to say which
runtimes the published package carries. A package that installs but does not run on a common
platform is a failed release, not a partial one.

## Cutting a release

1. **Bump `VERSION`.** The root `VERSION` file holds the version. Pre-1.0 the minor slot is the
   breaking slot, so a change to what the proxy does is a minor bump.
2. **Add the changelog entry.** Write `docs/changelog/{version}-{slug}.md` describing what changed.
   One file per version; this repository does not keep a single root `CHANGELOG.md`.
3. **Merge to `main`.** The version bump and the changelog entry travel in the same merge as the
   change they describe.
4. **Dispatch `publish.yml`.** The workflow publishes to nuget.org and does nothing else.

## How the publish works

The publish goes to nuget.org through Trusted Publishing, which is OIDC. No API key lives in this
repository, and none should ever be added to it. A credential in a workflow file is a credential in
a git history.

The trust policy on the nuget.org side is keyed to **owner + repository + workflow file**. It is not
keyed to the package id. That distinction matters the moment the package id changes, because a
trust configuration that names one workflow file does not silently follow the package to another.

## The package id

The intended package id is `ai-proxy`. Check it against NuGet's reserved-prefix list at publish
time. A reserved prefix rejects submissions from accounts that do not own the prefix, and the
failure is easy to misread as a broken workflow or a bad token.

This project hit exactly that on a sibling package. The fallback when the id is reserved is
`arasz.ai-proxy`, keeping the same `ToolCommandName` so the installed command does not change under
the operator. If the fallback is taken, the trust configuration is updated to name the same
workflow file, and the install line above changes with it.

## After the publish

Verify content, not metadata. Check that the version on nuget.org is the version in `VERSION`, and
that a package that introduces a new runtime identifier actually carries it.

Do not re-point or reuse a tag. A version denotes exactly one commit, forever.

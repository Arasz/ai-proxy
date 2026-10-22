# changelog/

One file per release, `{version}-{slug}.md`, written in the same PR that bumps the root
`VERSION` file. Each entry says what changed for a user of the installed tool, and names the
PRs it ships.

A PR that leaves `VERSION` alone cuts no release and adds no file: the entry of the release
that next bumps `VERSION` describes and names it, so several PRs can ship as one release.

The root `VERSION` file is the version source of truth. The build reads it
(`Directory.Build.props`), and the release workflow tags on it. This directory holds the
per-release entries and nothing else — it has no `VERSION` file of its own, because a second
copy of the version is a second thing to keep in step.

## Index

Newest first; add each release's line in the same PR that adds its file.

| Version | What changed | PRs |
|---|---|---|
| [0.3.1](0.3.1-xml-reducer-compacts-again.md) | The XML reducer compacts again: it was falling back to the line rule for every ordinary document, and the whitespace that is content now survives the reduction. | #14 |
| [0.3.0](0.3.0-content-compression-pipeline.md) | The content compression pipeline: the tool output in a request is reduced before the provider sees it, and the original is kept and served back. | #4 |
| [0.2.0](0.2.0-v1-phase1-proxy-skeleton.md) | The proxy skeleton: the provider is detected, the request is compressed and forwarded to the right upstream, and other traffic is carried through unchanged. | #2 |

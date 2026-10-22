# Contributing to ai-proxy

Thanks for looking. This is a small project — one maintainer, no committee — so the process is
short, but the gates are real and CI enforces them. Read this once and you will not be surprised
by a red build.

If something here does not match what the repository actually does, that is a bug in this file.
Open an issue.

## First time here?

You do not need to understand the whole proxy to make a useful change. Good first contributions:

- A test that pins behaviour the proxy already gets right.
- A missing test for a case the routing rules already describe.
- A documentation correction, especially one that names a file path that no longer exists.

Two things to know before you start, because they shape everything below.

1. **Write the failing test first.** This is not aspirational; a CI gate checks that a code change
   touched tests.
2. **A `git push` is not a release.** Releases are a separate, deliberate step. See
   [`RELEASING.md`](RELEASING.md).

## Setup

The .NET SDK is the only prerequisite. Clone, restore, build.

```bash
git clone https://github.com/Arasz/ai-proxy
cd ai-proxy
dotnet build
```

`dotnet build` restores the packages and compiles the solution. CI runs the same two commands the
gates describe, so a green local run means a green build. If your machine has several SDKs
installed, `dotnet --list-sdks` tells you which one the build will use.

Run the tests with:

```bash
dotnet test
```

Do not add a package dependency without a very good reason, and say so in the pull request body
when you do. The routing and credential rules this proxy depends on are security-relevant, so a
new dependency that touches HTTP handling deserves a paragraph, not a line.

## The workflow

### 1. Branch. Never push to `main`.

```bash
git checkout -b task/short-description
```

**One task, one PR.** Do not bundle unrelated work into a single change; it makes review and
rollback lose their scope.

### 2. Write the failing test first

No production code without a test that demanded it.

```bash
dotnet test --filter FullyQualifiedName~TheThingYouAreWorkingOn   # red
# ...implement...
dotnet test                                                   # green
```

The CI gate checks the one thing a machine can: that a change to the production code came with a
change to a test file. It is a signal, not a proof. It cannot tell a real test from an empty one,
so passing it is not the point. Writing the test first is.

There is an escape hatch for changes that genuinely cannot be tested, and it is visible in CI
output rather than silent. Use it only when you have a reason, and put that reason in the PR body.

### 3. Commit small, push often, open a draft PR early

Open a draft PR from your first commit so the work is visible in flight rather than arriving as
one large diff at the end.

### 4. Decide whether this is a release

If your change ships behaviour, the version contract applies. The root `VERSION` file holds the
version, and `docs/changelog/{version}-{slug}.md` holds one file per version describing what
changed. There is no single root `CHANGELOG.md` in this repository.

See [`RELEASING.md`](RELEASING.md) for the whole sequence, including what to do when the package id
turns out to be reserved on nuget.org.

### 5. Run the gates before you ask for review

Two commands, and they are the ones CI runs:

```bash
dotnet build
dotnet test
```

What each one is for:

| Gate | Fails when |
|---|---|
| `dotnet build` | The solution does not compile. Warnings are errors here, so a new warning fails the build on purpose. |
| `dotnet test` | Any test fails. |

### 6. Open the PR

State in the body:

- What was red before you implemented, and what is green now.
- Any pre-existing test you rewrote to a new contract, by name. A rewritten test is a changed
  promise, and it must be visible in review rather than buried in the diff.
- Whether this is a release, and if so which version and which changelog file.
- Anything you deliberately did not do, and why.

Then wait for CI. Do not merge with a red build.

## Conventions this repository holds to

These come from the agent instruction files and the scoped instruction files in this repository, so
a change that breaks one of them is a change that breaks a gate.

- **Braces always.** Every `if`, `for`, `while`, `foreach` and `try` body is braced. There is no
  single-statement form in this codebase.
- **Warnings are errors.** The build treats warnings as errors, so an unused variable or an
  un-awaited call is a red build rather than a note in the log.
- **`VERSION` plus `docs/changelog/`.** A release bumps the root `VERSION` file and adds a
  changelog file under `docs/changelog/`. One file per version.
- **Third-party GitHub Actions are pinned to a full commit SHA.** A mutable tag is not acceptable
  in a workflow, because the tag can move under the repository's feet between one run and the next.

## Security

If you find a security problem, do not open a public issue. Read [`SECURITY.md`](SECURITY.md).

That document is not a formality for this project. The proxy forwards provider credentials from
the caller's request, so a routing mistake can expose a credential to the wrong upstream. The
refusal for conflicting provider signals in `SECURITY.md` is a key security behavior.

## Licence

The project is MIT licensed. See [`LICENSE`](LICENSE).

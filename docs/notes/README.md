# notes/

Standalone design notes not yet folded into an ADR or an explanation doc.

## Contents

| File | What it is |
|---|---|
| [a-delegation-lane-that-dies-on-a-stale-tool-path.md](a-delegation-lane-that-dies-on-a-stale-tool-path.md) | The other way a lane dies in a second with no output: the harness resolved the runtime's path when the session started, a package manager moved the runtime, and every later spawn asked for a file that was gone. |
| [dispatching-a-subagent.md](dispatching-a-subagent.md) | Why a delegated lane can die in a second with no output: the persona's `model:` pin is refused as a bare alias, the `level:`-resolved registry id decides, and every Anthropic model is blocked by this OpenRouter workspace's guardrail. What the two failure walls look like and which one a given symptom means. |

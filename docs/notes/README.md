# notes/

Standalone design notes not yet folded into an ADR or an explanation doc.

## Contents

| File | What it is |
|---|---|
| [dispatching-a-subagent.md](dispatching-a-subagent.md) | Why a delegated lane can die in a second with no output: the persona's `model:` pin is refused as a bare alias, the `level:`-resolved registry id decides, and every Anthropic model is blocked by this OpenRouter workspace's guardrail. What the two failure walls look like and which one a given symptom means. |

# Dispatching a subagent from this repository

**Date:** 2026-10-21

A note for whoever dispatches a lane from here. The first attempt at the scaffold dispatched
three lanes and one of them died in a second with no output. The cause was environmental, not
a defect in this repository, and it is worth writing down because the error message points
somewhere else.

## What the failure looks like

The lane exits 0, produces a session header and no assistant output, and the receipt carries
a warning that reads like the cause but is not:

```
explicit model "sonnet" fails /^[A-Za-z0-9][A-Za-z0-9._-]*(?:\/[A-Za-z0-9~@][A-Za-z0-9._~@:+-]*)+$(?![\s\S])/
— refusing to emit it into --model argv
```

That message is about the **persona's** `model:` pin, and it is a warning. A bare alias like
`sonnet` has no provider prefix, so the harness refuses it and the pin never decides anything.
The real decision is made one rank down, by the persona's `level:`.

## The chain that actually decides

1. The persona's `model:` pin (`sonnet`, `opus`) is a bare alias. It is refused with the
   warning above and treated as absent.
2. The persona's `level:` (`medium`, `high`) decides instead. It resolves against
   **the session project's** `.ai-badger/model-groups.json` — not the lane's working
   directory, and not this repository's copy if the session is running elsewhere.
3. That registry's `medium` preferred id is what reaches `--model`.

So the id a lane runs on is set by the registry of whichever project the **session** is
running in.

## The queue's group `level:` beats the persona's own `level:`

The queue tool's group parameter outranks the persona file:

```ts
...(groupLevel ?? persona.level) !== undefined ? { level: (groupLevel ?? persona.level) as string } : {},
```

Dispatching a group with `level: "medium"` therefore sends **every** lane in it to the medium
tier, including lanes whose persona asks for `high`. In the first dispatch here, the two
`architect` lanes — whose persona says `model: opus` / `level: high` — ran on the medium id
(`claude-sonnet-5.5`) rather than opus. That is the harness working as documented, and it is
worth knowing when a lane's model does not match the tier you expected.

The rule that follows: when a dispatch mixes personas of different tiers, do not set a group
`level:` and let each persona choose its own.

## What broke it

At dispatch time the session project's registry was the older ai-badger scaffold, whose
`medium` preferred id was `openrouter/anthropic/claude-sonnet-5.5`. Every Anthropic model is
blocked by this OpenRouter workspace's guardrail:

```
$ curl -s https://openrouter.ai/api/v1/chat/completions -d '{"model":"anthropic/claude-sonnet-5.5",...}'
404  0 endpoints out of 2 requested are available matching your guardrail restrictions
```

`claude-opus-5.5` and `claude-haiku-5.5` behave the same. The lane therefore died at the
guardrail, in about a second, having produced nothing.

This repository's own registry is current (`medium` → `openrouter/openai/gpt-6.1-sol`), so a
lane dispatched from here resolves to a model this account can route.

## The second wall, and how the harness gets past it

Every **paid** model on this account answers `402 Insufficient credits` — `gpt-6.1-sol`,
`glm-5.3`, `muse-spark-1.2` and `gpt-6-luna` were all measured. The free tier is separately
rate-limited (`429 free-models-per-day-high-balance`).

The harness handles this on its own: the primary pin 402s, and its built-in fallback retries
the lane on the parent's model, which answers. A lane dispatched from this repository
completed with `exit 0` and a real answer on that path.

**So: a lane that returns nothing after about a second is the guardrail.** A lane that starts
and then dies on a 402 is out of credits. Neither is a defect in the work being delegated.

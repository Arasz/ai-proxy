# Refinement feedback — CCR design: ADR 0005 decisions

<!-- refinement-form: refinement:2026-10-25-ccr-design:v1 · saved 2026-10-25T10:24:02.242Z · answered 14/16 -->

Source document: `docs/adr/0005-tool-result-previews-with-in-request-retrieval.md, docs/work/2026-10-24-provider-cache-strategies.md (F13)`

## O5 — The first CCR release covers Anthropic Messages only, thread and stateless clients

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O2 — A preview fires only at 3,000 estimated tokens or more, and is at most 1,000 tokens

**Verdict:** CHANGE

**Notes:**

> 1500 tokens

---

## O3 — The preview keeps original lines: signal lines first, then head and tail, then an identifier overlay

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O15 — Tool name <code>proxy_retrieve</code>, marker text <code>[proxy preview &lt;id&gt;: …]</code>

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O12 — <code>lines</code> and <code>pattern</code> results are capped at 4,000 tokens; <code>whole</code> has no cap

**Verdict:** APPROVE

**Notes:**

> 3000

---

## O1 — The retrieval tool is appended to <code>tools</code> on every tools-bearing request, preview or not

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F13 — An unrecognised provider gets the universal fallback: exact byte-prefix caching assumed, whole-region pure rewrites only

**Verdict:** CHANGE

**Notes:**

> so how expected compression looks for those parameters?

---

## O6 — Thread clients get a ledger of previews per thread, and ids are aliased first-to-last across hidden turns

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O8 — Streamed <code>tool_use</code> blocks are held until the message ends; text and thinking stream live

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O9 — At most 3 retrieval rounds, then 1 <code>limit_reached</code> round: at most 5 upstream calls per client request

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O13 — Usage shown to the client: input and cache fields from the last upstream call, output summed

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O14 — For stateless clients, hidden retrieval turns are not spliced back into later requests

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O10 — Originals go to a synchronous content-addressed table, deleted 14 days after first keep; reads do not extend

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## O11 — Stateless requests never depend on the store: retrieval is answered from the request's own originals

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

- O7 — Client tool calls mixed into a retrieving message are answered with <code>is_error</code> &ldquo;issue again&rdquo; (note only, no verdict)

  > Client tool calls mixed into a retrieving message - when that can happen?

- O4 — A kind ships CCR only if, re-scored with claude-opus-5-5, it keeps every answer, retrieves on at most 25%, and sends at most 80% of original tokens net (note only, no verdict)

  > we ship if we are at least on par with rtk but our goal is to improve


<!-- end refinement feedback -->
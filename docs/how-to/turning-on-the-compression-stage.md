# Turning on the compression stage

The stage reduces the tool output in a request before the provider sees it, and keeps what
it dropped. It is off until you turn it on, because it changes what the provider is sent.

## What it does to a request

The proxy finds the tool output in the request, splits it into the parts it carries, reduces
each part with the rule that fits it, and puts the result back. The bytes outside the tool
output are what the client sent, byte for byte.

| the tool output | what happens to it |
|---|---|
| a log | the lines that carry a signal, the failure, the timeout or the refusal, plus two lines at each end as anchors |
| a JSON document | compacted, and every value in it survives, down to the scale of a number |
| an XML document | compacted, and the whitespace that is content rather than formatting survives |
| a build log (`dotnet build`, `msbuild`) | every error and warning kept whole, repeats dropped, and the compiler command lines cut to their first 200 characters with a marker saying how much was dropped |
| a directory listing | left alone, because nothing compresses a listing without losing file names |
| a mixed payload | split by structure, and each part reduced by the rule that fits it |

Nothing is rewritten. Every reducer keeps a subset of the text it was given, so a fact that
survives is intact, and a fact that does not survive is in the store.

## Turning it on

Set the variable and start the proxy:

```bash
export PROXY__COMPRESSION__ENABLED=true
export PROXY__COMPRESSION__DATABASE=~/.ai-proxy/content.db
```

`PROXY__COMPRESSION__DATABASE` is optional, and the default is the path above. The database is
created on the first request, and it holds the text your tools returned, so it belongs on the
machine the proxy runs on.

## Seeing that it ran

The proxy logs one line for every request it changed: the paths of the reducers that ran, how
many parts there were, the sizes and the ratio.

```
compressed the body with log.signal-filter over 3 parts, 4821 bytes to 1244, ratio 0.258
```

The response carries the id the original was kept under, in the `x-ai-proxy-content-id`
header. The id is the request's, so one request has one id.

## Getting the original back

The original of every request the stage changed is in the database, and the endpoint serves it
back:

```bash
curl http://127.0.0.1:17432/content/0198f3c2-1a4b-7c8d-9e0f-2b3c4d5e6f70
```

The body comes back exactly as the client sent it. An id nobody stored is answered with a
problem details response and a 404, not with an empty body.

## The thresholds

Each kind has its own smallest region worth compressing: 200 characters for a log, 100 for JSON,
512 for prose. The threshold is checked against the whole tool result, using the kind that holds
most of its characters, and not against each fragment. A result under it is left alone, because
compressing a small result costs more than it saves. Once a result passes, every fragment is
reduced, except fragments of a kind that is switched off, such as directory listings. `PROXY__COMPRESSION__MINIMUM` sets one threshold for every kind and
overrides them all.

## The model

`PROXY__COMPRESSION__MODEL` points at the extractive model's artifact. The model is not in this
release, and the rule-based prose path runs until it is. The measurements that chose the rules
are in the
[compression research record](../work/2026-10-22-on-device-compression-models.md), and the
corrections the implementation found in them are at the end of that record.

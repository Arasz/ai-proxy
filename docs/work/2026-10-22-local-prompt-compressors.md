# Research: a local prompt compressor in front of the proxy

**Date:** 2026-10-22
**Question:** If `ai-proxy` compressed a request before forwarding it to the provider, which model and which artifact would it run, what would that cost in wall time on the two target machines, and what would it change about the request it forwards?

This is the answer to the open question F10 of the origin record, which asked whether this project
should carry a compression pipeline at all and left it untested. What follows settles the
operational half of that question: the model, the artifact, the latency and the fidelity cost. The
governance half, whether a .NET proxy should mutate the prompts it relays, is still a decision for
the owner, and the last section says what the numbers imply about it.

**Provenance.** The measurements were taken in the sibling project `models-conversion`, on the same
M4 machine, and are copied here with their conditions intact. The full record with all 25 findings
is at `models-conversion/docs/work/2026-10-21-kompress-v2-local-compression.md`; the scripts and
the downloaded artifacts live outside both repositories, in `~/kompress-work`. Nothing in this
record was measured on the Intel plus NVIDIA machine, so every claim about CUDA is read from ONNX
Runtime's operator documentation rather than observed.

## The short answer

Use ONNX, because ONNX is what the model already ships and what its ecosystem already loads.

On the Intel plus NVIDIA box, that means the shipped fp32 artifact through `onnxruntime-gpu`. On
the M4 it means a static-shape fp16 re-export of the same weights, run through ONNX Runtime's Core
ML provider with `MLComputeUnits=CPUAndNeuralEngine`. On a 4061-token document that path takes
0.97 s where the shipped graph on the CPU provider takes 10 to 25 s.

Three facts should shape the design before any code is written. First, the model compresses prose
and leaves logs and JSON alone: on an 807-token agent log it kept every single token, so a content
router belongs in front of it. Second, compression changes the request. Scoring a document in
512-token chunks flips about 19% of the keep decisions compared with scoring it whole, because the
model loses the context on either side of the chunk. A proxy that compresses must be able to say
what it changed. Third, every published quality number for this model comes from the vendor. There
is no independent benchmark, and the one large independent study of prompt compression found that
the compressor only pays for itself when the prompt length, the compression ratio and the hardware
are matched.

```chart:matrix
title: what runs where, and what it costs
, Apple M4, Intel plus NVIDIA
shipped ONNX, CPU provider, 1 thread: 5.8-8.9 s per 3.3k tokens, fp32 ONNX through onnxruntime-gpu, not measured here
shipped ONNX, Core ML provider, dynamic shapes: unusable, minutes per call, no Core ML provider for CUDA
static fp16 re-export, CPU provider: 0.76 s per 512 tokens, same graph on the CUDA provider, plausible
static fp16 re-export, Core ML provider, CPUAndNeuralEngine: 0.096 s per 512 tokens, no Apple silicon on that box
```

```chart:range
title: M4 latency for a 4061-token document, 1 thread (low..median..high, seconds)
shipped ONNX whole, CPU provider: 10.3..10.3..24.7
static fp16 chunked at 512, CPU provider: 8.3..10.8..17.5
static fp16 chunked at 512, Core ML provider: 0.97..1.07..1.11
static fp16 chunked at 2048, Core ML provider: 2.51..2.56..3.33
```

## Findings

### F1 — The candidate model is `chopratejas/kompress-v2-base`, and it predicts one keep-or-drop score per token [READ]

The model is ModernBERT-base (150M parameters) with a LoRA adapter (4.4M trainable) and a custom
dual head, a token classifier and a one-dimensional span convolution. It scores every subword of
the input, the surviving tokens form the compressed output, and the output is always a subsequence
of the input in the original order. The score is a number between 0 and 1, and a token is kept when
its score is at or above the threshold, 0.5 by default. The threshold is the compression dial: the
vendor's own held-out table shows a keep rate of 0.946 at threshold 0.3 and 0.752 at 0.7.

**Evidence:** `chopratejas/kompress-v2-base` → `README.md` — "Extractive prompt compressor for LLM
proxies. Predicts a keep/drop label per token"; "Based on ModernBERT-base (150M params) with a LoRA
adapter (4.4M trainable params, 2.9%) plus a custom dual head (token classifier + 1-D span conv)";
the threshold table (0.30 → keep_rate 0.946, must_keep_recall 0.997; 0.50 → 0.866 / 0.989 / F1
0.920; 0.70 → 0.752 / 0.953), "Measured on the held-out test split (n=12,697 rows, 16,827,490 scored
subwords)".

### F2 — The model already ships the two ONNX artifacts its ecosystem loads [READ]

The repository carries `onnx/kompress-int8-wo.onnx` (274 MB, weight-only int8 through the
`com.microsoft.MatMulNBits` contrib op, which needs `onnxruntime>=1.24`) and `onnx/kompress-fp32.onnx`
(601 MB, the lossless reference), plus the PyTorch weights and the tokenizer. The Headroom proxy,
the model's main consumer, loads the int8 artifact first and falls back to fp32 when a runtime
lacks the 8-bit kernel. So ONNX is not a conversion project here, it is the vendor's own
distribution format, and a proxy does not have to invent a pipeline to run the model.

**Evidence:** `chopratejas/kompress-v2-base` → `README.md` — "`onnx/kompress-int8-wo.onnx` | 274 MB |
weight-only int8 (MatMulNBits), the default artifact; needs `onnxruntime>=1.24`"; "`onnx/kompress-fp32.onnx`
| 601 MB | lossless reference". `headroomlabs-ai/headroom` → `headroom/transforms/kompress_compressor.py`
— the artifact order `("onnx/kompress-int8-wo.onnx", "onnx/kompress-fp32.onnx", "onnx/kompress-int8.onnx")`
and the note that "older onnxruntime builds without the 8-bit kernel fail at session load and fall
through to fp32".

### F3 — The shipped ONNX graph is faithful to the shipped PyTorch weights [MEASURED]

The fp32 graph takes `input_ids` and `attention_mask` as int64 tensors of shape `[batch, seq]` and
returns `final_scores` as float32 of shape `[batch, seq]`. The whole scoring rule, including the
combination of the token probability with the span probability, is inside the graph, so a caller
needs no Python model code. Rebuilding the PyTorch reference from `merged.pt` exactly as the model
card documents and comparing it against the fp32 ONNX on the same inputs, from 317 to 5090 tokens,
gave a largest score difference of 2.8e-06 to 2.1e-04 and identical keep decisions in every case.
The attention masking survives the export, so the graph is not a reinterpretation of the model.

**Evidence:** `models-conversion` record F2, measured on the M4 with onnxruntime 1.31.0 and
torch 2.14.1 in eager attention. Per length, max absolute difference 2.83e-06 (317 tokens), 7.70e-05
(637), 2.10e-04 (1274), 8.86e-05 (2546), 6.63e-05 (5090), keep-decision agreement 1.000 in all five
cases. Graph inspection: 2739 nodes in the fp32 model, no contrib ops; 2738 nodes in the int8 model
with 89 `MatMulNBits` (bits 8, block size 128).

### F4 — On the M4 the int8 artifact is a size win and not a speed win [MEASURED]

The weight-only int8 file is 2.3 times smaller, which matters for a proxy that ships a model
directory. It is not faster, though. Interleaved A/B runs of the same input on the same machine
gave fp32 1.277 s against int8 1.395 s at 1419 tokens, and a tie at 4739 tokens (10.0 s against
9.8 s). The int8 kernel dequantizes on the fly and does not beat the AVX fp32 path on this CPU.

**Evidence:** `models-conversion` record F3, M4, onnxruntime 1.31.0, one warm-up call then six
timed runs each: fp32 [1.199, 1.176, 1.264, 1.291, 1.403, 1.339] s (median 1.277), int8-wo
[1.416, 1.395, 1.334, 1.450, 1.394, 1.346] s (median 1.395). At 4739 tokens, three runs each, int8
[11.45, 9.65, 9.80] s and fp32 [10.58, 10.00, 9.45] s. File sizes 274,049,435 and 600,561,247 bytes.
The two artifacts also made the same decision on every token scored, 19117 of 19117, with a largest
score difference of 0.205 (record F4).

### F5 — The cost grows faster than the input, so a proxy needs a size budget [MEASURED]

1419 tokens cost about 1.3 s, 4739 tokens cost about 10 s, and 8192 tokens cost 31 to 56 s on the
same machine. That is 3.3 times the tokens for 7.8 times the time. A 30k-token tool output is not a
problem that a 30k-token budget solves, it is a minutes-long blocking call in the request path. The
practical shape on CPU is to cap the input per call.

**Evidence:** `models-conversion` record F5: 1419 tokens fp32 median 1.277 s and int8 1.395 s; 4739
tokens fp32 10.0 s and int8 9.8 s; 5667 tokens int8 17.633 s and fp32 13.955 s; 8192 tokens int8
median 31.336 s (18.828 to 34.138) and fp32 median 55.903 s (32.245 to 331.965, one 332 s outlier).
The PyTorch reference on CPU took 21.5 s for 5090 tokens, so the slowness belongs to the model
rather than to the export.

### F6 — The model compresses prose and refuses to compress logs [MEASURED]

On a 1419-token natural-language passage it kept 71.2% of the subwords and the surviving text
reads as fluent English with the facts intact. On an 807-token agent log, with timestamps, levels,
`worker=`, SQL, `file=`, `line=` and `trace_id=` fields, it kept 100% of the tokens and dropped
nothing at all. Both artifacts behaved the same way. A proxy that points this model at a tool
stream will compress prose and pass everything else through untouched, which is the opposite of what
a token-saving feature sounds like.

**Evidence:** `models-conversion` record F9, M4, onnxruntime 1.31.0: prose 1419 tokens keep rate
0.7125 (int8) and 0.7174 (fp32); prose 4739 tokens 0.7696 for both; agent log 807 tokens 1.0000 for
both, 773 ms and 727 ms on a cold first call. The vendor agrees, in the v1 card: "Best on natural
language text; structured data (JSON, code, logs) should use specialized compressors."
`headroomlabs-ai/headroom` → `README.md` routes content types to SmartCrusher for JSON,
CodeCompressor for code and kompress for prose.

### F7 — The shipped graph is unusable through ONNX Runtime's Core ML provider [MEASURED]

The first thing any Apple-silicon deployment tries is to let ONNX Runtime use its Core ML
execution provider. On the shipped dynamic-shape graph that path is a disaster: at 1653 tokens a
single call took 12 to 120 s, median 103 s, against 0.99 s on the CPU provider, and at 3307 tokens
it took 197 to 525 s. The provider recompiles the Core ML model per input shape, and the shipped
graph has a free sequence dimension.

**Evidence:** `models-conversion` record F6, M4, onnxruntime 1.31.0: CPU provider fp32 827 tokens
0.325 s, 1653 tokens 0.986 s, 3307 tokens 4.144 s; Core ML provider fp32 827 tokens median 3.163 s,
1653 tokens median 103.280 s (12.431, 103.280, 120.641), 3307 tokens median 381.193 s (196.831,
381.193, 525.204); the run was cut off before 6614 and 8192 tokens.

### F8 — A static-shape fp16 re-export fixes it and runs 5 to 10 times faster than the CPU provider [MEASURED]

Re-exporting the same `merged.pt` weights with a fixed sequence length per bucket produces a graph
that scores identically to the shipped dynamic graph, with a largest score difference of exactly
zero and identical keep decisions at all four bucket sizes. Run through the Core ML provider with
`MLComputeUnits=CPUAndNeuralEngine`, that graph is 5 to 10 times faster than the same graph on the
CPU provider. `MLComputeUnits=ALL` is about twice as slow, because the GPU is a poor fit for this
graph and the Neural Engine is where the work belongs.

| bucket | CPU provider | Core ML, `ALL` | Core ML, `CPUAndNeuralEngine` |
|---|---|---|---|
| 256 | 0.327 s | 0.095 s | 0.037 s |
| 512 | 0.757 s | 0.167 s | 0.096 s |
| 1024 | 2.344 s | 0.470 s | 0.232 s |
| 2048 | 6.537 s | 3.422 s | 1.322 s |

**Evidence:** `models-conversion` records F18 and F19, M4, onnxruntime 1.31.0, one thread, thread
spinning off, fp16 static graphs, medians. The export used the legacy TorchScript exporter at
opset 17 (`torch.onnx.export(..., dynamo=False, opset_version=17)`), which takes 1.5 to 13.5 s per
graph; fp32 graphs are 602 to 635 MB and fp16 graphs 301 to 309 MB. The recipe is not new: it comes
from `ai-raccoon` → `docs/adr/0117-coreml-execution-provider-stays-out.md` and
`docs/work/2026-09-25-ane-layout-reexport.md`, whose own record found the same thing for a
granite encoder, including that fp32 graphs never reach the Neural Engine and fp16 ones do.

### F9 — The .NET path for all of this already exists in a sibling project [READ]

`ai-raccoon` ships a Core ML session factory in C#, at
`src/AiRaccoon.Infrastructure/Embedding/NeuralEngine/CoreMlSessionFactory.cs`. It sets
`MLComputeUnits=CPUAndNeuralEngine`, `RequireStaticInputShapes=1`, a per-bucket
`ModelCacheDirectory`, a free-dimension override for the batch, the sequence and the attention
mask's own dimension, and turns off both spinning settings. It keeps a CPU session for rows longer
than the largest bucket, which is the overflow pattern a proxy would want. The same project
documents the gotcha that a shared compiled-model cache serves the wrong compiled model back on the
next differently sized row, and that ONNX Runtime's own thread spinning roughly doubles CPU per
row unless it is turned off.

**Evidence:** `ai-raccoon` → `src/AiRaccoon.Infrastructure/Embedding/NeuralEngine/CoreMlSessionFactory.cs`
(`ProviderOptions`, `CreateBucket`, `CreateOverflow`); `ai-raccoon` → `docs/adr/0117-coreml-execution-provider-stays-out.md`
("static shapes require a free-dimension override on the attention mask's own dimension name, not
just batch and sequence, or the model refuses to build at all; a compiled-model cache shared across
bucket shapes serves the wrong compiled model back and crashes on the next differently-sized row;
and ONNX Runtime's own thread spinning roughly doubles CPU per row on every device, CoreML
included, unless it is turned off").

### F10 — The Neural Engine takes most of the work, and a third of the graph does not [MEASURED]

The compute plan of the 512-token fp16 graph reports 23 Core ML partitions with 1126 of 1173 nodes
supported by Core ML, and places 1245 operation usages on the Neural Engine against 609 on the GPU
and 406 on the CPU. The residue on the CPU is the part of ModernBERT that does not map onto an
ANE-friendly operation set, chiefly the rotary embedding arithmetic and the per-layer mask
construction. The fix that `ai-raccoon` found for its own encoder was a re-layout of the model, the
`ml-ane-transformers` style with one-by-one convolutions for every linear, channel-axis layer
normalisation, per-head matrix-multiply attention and the rotary fold moved into extra
convolution channels. That took its graph from 13 partitions to 1, with 1405 of 1407 operations on
the Neural Engine. For this model that rewrite does not exist yet, and ModernBERT's local versus
global attention pattern and its two custom heads make it a port rather than a configuration
change.

**Evidence:** `models-conversion` record F20, M4, `ProfileComputePlan=1` on the 512-token fp16
graph: "number of partitions supported by CoreML: 23 number of nodes in the graph: 1173 number of
nodes supported by CoreML: 1126", and the device usage counts 1245 NeuralEngine, 609 GPU, 406 CPU;
at the 2048 bucket, 1313 ANE, 541 GPU, 406 CPU. `ai-raccoon` →
`docs/work/2026-09-25-ane-layout-reexport.md` F2 and F3.

### F11 — The Core ML path has real running costs [MEASURED]

Each bucket needs its own compiled model. Compiling took 24 to 29 s at 256 to 1024 tokens and 42 to
51 s at 2048, and the compiled cache is 588 to 650 MiB per bucket on disk, against a 1 GiB ceiling
that `ai-raccoon` set for itself. Four buckets would put it near 2.4 GiB, so a shipping
configuration has to prune to the buckets it actually uses. fp16 weights cost a little accuracy as
well: whole-input fp16 against whole-input fp32 agrees on 0.9994 of keep decisions, and through
Core ML the agreement against the shipped fp32 graph was 0.9980 at the 512 bucket, 1.0000 at 1024
and 0.9990 at 2048.

**Evidence:** `models-conversion` record F21, M4: cold build 23.94 s, 24.04 s, 29.7 s, 42.3 s and
50.5 s across the buckets and compute-unit settings; cache sizes 588 MiB (512), 604 MiB (1024),
650 MiB (2048) by `du` on the per-bucket `ModelCacheDirectory`; warm rebuild 1.1 to 1.4 s.

### F12 — Chunking is what makes the Core ML path affordable, and it changes the output [MEASURED]

A 4061-token document takes 10.3 s on the shipped graph with the CPU provider and 0.97 s through
Core ML with 512-token chunks, which is the number that makes a compression stage plausible in a
request path. The catch is that chunking is not output-neutral. On a 1754-token input, the shipped
fp32 graph scored whole agrees with itself chunked at 512 tokens on only 0.8073 of keep decisions,
and at 1024 tokens on 0.8603. The keep rate rises from 0.7098 for the whole input to 0.8546 and
0.8016 for the two chunk sizes. The model is context-sensitive, so a chunk scored without its
neighbours keeps more of its text. This reproduces with the vendor's own dynamic fp32 graph and no
padding anywhere, so it is a property of the model rather than of any export.

**Evidence:** `models-conversion` records F22 and F23, M4, onnxruntime 1.31.0, one thread: "A)
shipped dynamic ONNX, whole input, CPU EP: 24.662s keep=0.7112"; "coreml_chunk512: median=1.070s
keep_agree_vs_A=0.8530"; "coreml_chunk1024: median=1.432s"; "coreml_chunk2048: median=2.560s
keep_agree_vs_A=0.9074". The chunking comparison with the shipped graph: "dynamic fp32 chunk512 vs
whole: keep_agree=0.8073 keep_chunk=0.8546 keep_whole=0.7098", "dynamic fp32 chunk1024 vs whole:
keep_agree=0.8603 keep_chunk=0.8016 keep_whole=0.7098".

### F13 — The alternatives exist, and the closest one has a 512-token context [READ]

LLMLingua-2 from Microsoft is the reference implementation of the same idea, a token classifier
trained by data distillation, with public mBERT and XLM-RoBERTa-large weights and a paper that
claims 3 to 6 times the speed of earlier compressors and 1.6 to 2.9 times the end-to-end
latency at compression ratios of 2 to 5. Its context is the catch: the mBERT configuration
declares 512 position embeddings and the XLM-R variant 514, so long inputs need chunking, and
chunking is exactly what changes the output (F12). Third-party ONNX conversions exist for both
variants, one an int8 dynamic-quantized XLM-R build of about 116 MB and one a browser build for
transformers.js, so ONNX is not specific to this model. EFPC claims a 4.8% relative F1 gain over
LLMLingua-2 at 4x compression, LongCodeZip targets code contexts at up to 5.6x, and the
KV-cache-compression family (SlimKV, SnapKV, H2O, PyramidKV) solves a different problem, shrinking
the attention cache rather than the prompt.

**Evidence:** arXiv 2403.12968 (LLMLingua-2) for the claims; `microsoft/llmlingua-2-bert-base-multilingual-cased-meetingbank`
→ `config.json` for `max_position_embeddings: 512` and
`microsoft/llmlingua-2-xlm-roberta-large-meetingbank` → `config.json` for 514;
`danielfcramos/llmlingua-2-onnx-quantized` → `README.md` for the int8 ONNX build;
arXiv 2503.07956 (EFPC) and 2510.00446 (LongCodeZip).

### F14 — Every quality number for this model comes from the vendor [READ]

There is no independent benchmark of `kompress-v2-base`. The F1 figures are the vendor's own, on
their own held-out split, against labels their own pipeline produced. Headroom's evaluation table,
the same author's project, reports GSM8K 0.870 against 0.870, TruthfulQA 0.530 against 0.560, SQuAD
v2 at 97% with 19% compression and BFCL at 97% with 32% compression, each on 100 examples. The
independent study to lean on is "Prompt Compression in the Wild", which ran thousands of requests
across three GPU classes and found LLMLingua delivering up to 18% end-to-end speed-up only when
prompt length, compression ratio and hardware are well matched, with the compression step itself
cancelling the gain outside that window.

**Evidence:** `chopratejas/kompress-v2-base` → `README.md` for the training and split description;
`headroomlabs-ai/headroom` → `README.md` for the evaluation table and its own caveat that at N=100 a
delta of 0.03 falls inside the confidence interval; arXiv 2604.02985 for the independent study.

### F15 — Nothing about the NVIDIA box has been observed [UNVERIFIED]

There was no NVIDIA machine, no CUDA runtime and no GPU wheel in the environment where these
measurements were taken, so the CUDA behaviour of both artifacts is read from ONNX Runtime's
operator documentation rather than seen. The documentation lists `com.microsoft.MatMulNBits` under
the CPU, CUDA and DirectML providers, and its CUDA guide describes 2, 4 and 8 bit weights, which
covers the int8 artifact's bit width. The fp32 artifact uses standard operations only. What the
int8 path actually does on a GPU is unknown, and the CUDA implementation of that operator is
primarily a 4-bit weight-only path, so a large speed-up from int8 over fp32 on a GPU should not be
assumed.

**Evidence:** `microsoft/onnxruntime` → `docs/OperatorKernels.md`, `MatMulNBits` under "Operators
implemented by CPUExecutionProvider", "…by CUDAExecutionProvider" and "…by DmlExecutionProvider";
`microsoft/onnxruntime` → `docs/contrib_ops/cuda/matmul_nbits.md` — "quantized to 2, 4, or 8 bits".

## What this means for ai-proxy

If the proxy gains a compression stage, five things have to be true for it, and the measurements
above say what each one costs.

A content router belongs in front of the model. The model drops nothing from logs, JSON or code
(F6). Routing those to a pass-through, and prose to the compressor, is what makes the feature worth
having, and it is the split Headroom uses.

A deadline with a pass-through fallback keeps the promise. A slow compressor must give up rather
than hold the request open. The cost is bounded but not small: 0.97 s for a 4k-token document on
the M4 through Core ML, 5.8 to 8.9 s for 3.3k tokens on the CPU provider (F5, F12). A request
deadline of a few seconds with an explicit pass-through on timeout is enough to hold that line.

The chunk policy has to be deliberate. Chunking is what makes the fast path fast, and it flips
about 19% of the keep decisions at 512-token chunks against a whole-input score (F12). Chunk size
is a quality dial as much as a performance one, and the owner should pick it knowing that a
2048-token chunk keeps more of the original decisions (0.9074 agreement) at 2.56 s for a 4k
document than a 512-token chunk does at 0.97 s.

The proxy needs observability, because it mutates what it relays. The forwarded request is no
longer the client's request. The proxy should log the model id, the threshold, the keep rate, the
chunk size and the estimated saving, and it should keep the original available the way Headroom's
retrieval store does, so a user who needs the dropped text can get it back.

The hosting path already exists. The Core ML provider is reachable from .NET through
`Microsoft.ML.OnnxRuntime`, and a sibling .NET project ships a working bucket-session factory with
the provider options this model needs (F9). The NVIDIA side is the simpler half, one package and
one provider option, and it is also the half nobody has measured yet (F15).

## What the implementation changed

The model is not in the first release of the proxy. The 274 MB artifact cannot live in the
repository, and the numbers in this record are the model's, so the rule-based path runs until
the artifact is delivered. The rule-based path's own measurements are in
[2026-10-22-on-device-compression-models.md](2026-10-22-on-device-compression-models.md), and
the correction to them is in that record: the measured deduplication was a no-op, so the rule
path's size figures came from the signal filter and the anchors alone.

The rest of this record stands, and it is the case for the model when it arrives: a content
router in front of it, a deadline with a pass-through fallback, a deliberate chunk policy, and
observability on every request the proxy changes.

## Still open

- Whether the Intel plus NVIDIA box loads and runs the int8 artifact, and whether it beats fp32
  there. Settles it by installing `onnxruntime-gpu` on that machine and timing both artifacts on
  the same input. The operator support is already read (F15).
- The keep rate and the quality on real traffic, at thresholds 0.3, 0.5 and 0.7, against the
  vendor's own labels. The measurements here used one prose paragraph and one log excerpt written
  for the purpose.
- Whether chunking can be made less destructive, since the 19% decision flip comes from the model
  losing context rather than from the format. A sliding window with overlap, or scoring only the
  interior of each chunk, might keep more of the original decisions.
- The peak physical plus neural footprint of the Core ML path on this model, and the Neural Engine
  power draw while it runs. `powermetrics` needs `sudo`, which the measuring session did not have.
- How many of the 406 CPU-resident operations can be moved onto the Neural Engine by porting
  `ai-raccoon`'s `ml-ane-transformers` layout to ModernBERT's attention pattern and to this model's
  two custom heads, and what the resulting single-partition graph does to the 0.096 s per
  512-token bucket.
- The governance half of the origin record's F10: whether a proxy should mutate the prompts it
  relays at all, and whether it belongs in this project rather than in the client. The numbers
  above speak to the cost, not to the ownership.

## Method note

The measurements come from the sibling project `models-conversion`, on an Apple M4 with ten CPU
cores and 25.7 GB of RAM, macOS 27.0.1, with `onnxruntime==1.31.0`, `tokenizers==0.23.3`,
`onnx==1.20`, `torch==2.14.1`, `transformers==5.20.2` and `coremltools==9.0` in one virtualenv.
Both ONNX artifacts, `merged.pt` and the tokenizer were downloaded from
`chopratejas/kompress-v2-base` on 2026-10-21. The scripts and the artifacts live in
`~/kompress-work`, outside this repository, so the numbers are reproducible but the tooling is not
carried here. The full record with all 25 findings, including the two that this digest does not
use, is at `models-conversion/docs/work/2026-10-21-kompress-v2-local-compression.md`.

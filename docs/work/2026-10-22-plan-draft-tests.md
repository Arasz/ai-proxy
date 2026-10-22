# Plan draft (test lens)

## Steps
### step-1 — decomposer
- Files: src/AiProxy.Core/Decomposer.cs + tests/DecomposerTests.cs
- Depends on: none
- Acceptance criteria:
  - AC1 parts split — check: Assert.Equal(expected, result.Parts)
  - AC2 chunk ids round trip — check: Assert.Equal(id, compositor.Rebuild(...).ChunkId)
  - AC3 unsplittable payload — check: Assert.True(result.IsUnsplittable)
- Test list:
  - Decomposer_Parts — targets: missing split — red when: split loop omitted
  - ChunkIds_RoundTrip — targets: id loss — red when: id not propagated
  - Unsplittable_Payload — targets: false positive split — red when: split forced

### step-2 — must-keep overlay (5 rules + no-drop)
- Files: src/AiProxy.Core/MustKeepOverlay.cs
- Depends on: step-1
- Test list: Each rule (literal line), plus NoReduction_DroppedNothing

### step-3 — log reducer (signal, level, 2 anchors, dedup, 503)
- Files: src/AiProxy.Core/LogReducer.cs
- Depends on: none
- Test list: SignalRule, LevelRule, Anchor1, Anchor2, Dedup, Status503_NotDropped

### step-4 — JSON reducer (compaction, scale survival 49.90)
- Files: src/AiProxy.Core/JsonReducer.cs
- Depends on: none
- Test list: Compaction, ScaleSurvives_49_90

### step-5 — region finder (3 shapes, empty body, byte-identical rest)
- Files: src/AiProxy.Core/RegionFinder.cs
- Depends on: step-1
- Test list: ShapeToolOutput_A, ShapeB, ShapeC, EmptyBody_None, ByteIdentical_Rest

### step-6 — router (claims + unclaimed)
- Files: src/AiProxy.Core/Router.cs
- Depends on: step-5
- Test list: Handler_ClaimsType, Type_Unclaimed

### step-7 — store (round trip, ratio, path taken, unknown id)
- Files: src/AiProxy.Storage/Store.cs + tests/StoreTests.cs
- Depends on: step-2..step-4
- Test list: RoundTrip, Ratio, PathTaken, UnknownRequestId

### step-8 — e2e through ProxyHost
- Files: tests/AiProxy.Tests/EndToEndTests.cs
- Depends on: step-6, step-7
- Test list: Compressed_And_Forwarded, NotCompressed_PassedThrough

## Joins
step-8 integrates decomposer → must-keep → log/JSON reducers → region → router → store → host; proves compressed body forwarded and uncompressed body untouched.

## Parallel groups
- Group A (independent leaf): step-1, step-3, step-4
- Group B (depends on 1): step-2, step-5
- Group C (depends on 2,5): step-6; (depends on 2,3,4): step-7
- Integration: step-8

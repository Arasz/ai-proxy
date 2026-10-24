# No null checks on values that cannot be null

With nullable reference types enabled, the type says whether a value can be null: `string` cannot,
`string?` can. Do not add `ArgumentNullException.ThrowIfNull(x)`, `x ?? throw ...` or
`if (x is null)` for a parameter, field or property whose type is non-nullable — the compiler
already enforces it, and the check is dead code that reads as doubt about the contract. Guard only
values whose type is nullable, or values that cross an untyped boundary (deserialized input,
reflection, interop) before they are given a non-nullable type. Project rule; it narrows
`guard-clauses`, which still applies to the checks that remain.

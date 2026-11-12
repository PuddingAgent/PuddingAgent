# Context

| File | Responsibility |
|---|---|
| `ContextContributionValidator.cs` | PA-05D fence/contribution validation: canonical history must end with the current user turn matching the fence hash and keep strictly ascending order; unique ids; external/memory/tool/caller content can only be Tool-role (never impersonate user/system); canonical-source contributions rejected; SHA-256 content hash must match when supplied. |
| `ContextAssembler.cs` | PA-05D ordered assembly: canonical order preserved byte-for-byte, contributions ordered priority-desc then id-ordinal, trimming only from the lowest priority, canonical-over-budget and oversized contributions are typed failures (no silent truncation), tokenizer failure/cancellation not swallowed. |

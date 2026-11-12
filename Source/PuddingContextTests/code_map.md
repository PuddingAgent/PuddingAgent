# Context tests

`ContextTestHost.cs`: deterministic tokenizer (fails on demand) and frozen canonical/contribution/request fixtures.

`ContextTests.cs`: fence presence and hash mismatch, non-ascending canonical order, external impersonation of user/system, caller-supplied non-tool role, canonical-source contribution and bad field bounds, duplicate ids and hash mismatch, deterministic contribution ordering, trim only low priority while preserving canonical fence, canonical-over-budget and oversized contribution typed, tokenizer failure and pre-cancellation, validation before tokenizer work.

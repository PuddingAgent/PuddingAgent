# Context ports

`ContextPorts.cs`: PA-05D BCL-only context ports — role/source enums, canonical message, external contribution (priority/lifetime/max tokens/hash/target turn/external marker), bounded assembly request with the current-user fence hash, result with used/dropped ids and trim flag, tokenizer port, and typed errors. Memory storage, tokenizer/compaction models and provider adapters stay outside.

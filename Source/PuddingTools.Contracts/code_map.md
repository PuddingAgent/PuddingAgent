# Tool facade ports

`ToolFacadePorts.cs`: PA-05B BCL-only tool-facade ports — descriptor (id/name/category/description/side-effect/visibility/revision), bounded query (text/category/side-effect/cursor/max count), search page with cursor/completeness, schema and state records (availability as string so adapters can report an explicit unknown), and typed errors. No execution port exists here.

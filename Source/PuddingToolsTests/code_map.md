# Tool facade tests

`ToolTestHost.cs`: in-memory catalog with visible/hidden tools, schemas and states including an explicit unknown availability.

`ToolFacadeTests.cs`: invisible tools hidden and deterministic ordering, text/category/side-effect filters and max-count cap, invalid bounds failing before the source, inspect visible schema and hidden/unknown NotFound, unknown availability preserved, source failure typed for search/inspect/state, invalid tool ids rejected.

# Tests/GoalDecisionPresenter.Tests

| File | Purpose |
|---|---|
| `GoalDecisionPresenter.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every snapshot is an in-memory `GoalDecisionSnapshot`, no real Goal store, no network. |
| `GoalDecisionPresenterTests.cs` | Golden coverage for `GoalDecisionPresenter.Format`: completed-with-evidence, blocked-with-unmet-criteria (`"; "`-joined criterion pairs in input order), budget-exhausted with blank next action and empty lists, unknown reason code preserved verbatim, blank reason code → `未提供`, empty criteria/refs → `无`, Chinese and escaped characters verbatim, exactly five ordered `Environment.NewLine` lines with no leading/trailing blank line and no appended acceptance conclusion, input snapshot/collections unchanged, and null snapshot → `ArgumentNullException`. Expected strings match the frozen golden fixture verbatim. |
| `code_map.md` | This index. |

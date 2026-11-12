# Samples/GoalDecisionPresenter

| File | Purpose |
|---|---|
| `GoalDecisionPresenter.csproj` | net10.0 class library sample (TFM inherited from the root build properties; not an Exe; no packages); only `ProjectReference` is `Pudding.Api.Contracts`. |
| `GoalDecisionPresenter.cs` | Pure presentation entry point `Format(GoalDecisionSnapshot snapshot)` returning exactly five `Environment.NewLine`-separated lines in fixed order: `Phase=<Phase>`, `ReasonCode=<ReasonCode或未提供>`, `UnmetCriteria=<无或按输入顺序 "; " 连接的 "<CriterionId>:<Outcome>">`, `NextAction=<原值或未提供下一步>`, `EvidenceRefs=<无或按输入顺序 ", " 连接的引用>`. Unknown reason codes are preserved verbatim; blank reason code becomes `未提供`, blank next action becomes `未提供下一步`; empty criteria/refs become `无`. It never writes a GoalPhase, derives a next state, parses natural language or appends an "accepted/completed" conclusion, and it never mutates the input snapshot. |
| `code_map.md` | This index. |

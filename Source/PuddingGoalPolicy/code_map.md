# Goal policy

| File | Responsibility |
|---|---|
| `GoalContinuationPolicy.cs` | PA-04 pure decision function: cancel/pause/revocation win, Chat ends after one turn, stale versions never reuse an old pass, required reviews missing/pending/unknown wait, budget/round exhaustion stops without granting more, completion needs sufficient current evidence and no unresolved execution fact, repair is bound by explicit limits/no-progress, and the next-round key is stable (GoalRunId + policy version + activation epoch + iteration). Never writes state, calls a model or runs tools. |

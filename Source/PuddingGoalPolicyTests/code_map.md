# Goal policy tests

`GoalFixtures.cs`: frozen policy/facts builders used by the decision-matrix and trace tests.

`GoalPolicyTests.cs`: decision matrix — cancel/pause/revocation first, Chat single-turn, stale/changed versions, missing/pending/unknown required review, review-not-required completion, budget/round exhaustion, NeedsUser wait, evidence-required completion, model Accept without evidence is not completion, repair within/without limits, no-progress block, no executable action, typed invalid input, stable next-round key.

`GoalPolicyTraceTests.cs`: minimal-loop traces — Chat ends after the first round, Goal missing deliverable continues once then completes, old-version satisfied review cannot complete the new goal, repeated settlement yields the same decision and key, unknown review is neither accept nor reject, budget exhaustion stops without infinite retry, truncated/insufficient evidence never passes.

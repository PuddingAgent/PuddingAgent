# Invocation pipeline

| File | Responsibility |
|---|---|
| `ExtensionRegistry.cs` | PA-07 07B registry load: per-point immutable snapshot, duplicate/invalid descriptor rejection, observed-generation fence, source failure typed (never an empty plugin set), second pre-commit generation check. |
| `ExtensionPipeline.cs` | PA-07 07B plan/fence: Guard → Transform → Advice → Observer ordering by descending priority then stable id; ungranted extensions are reported as skipped (never silently rewritten); a transform can never change tool identity/authorization/real exit code/budget owner/failure code nor flip failure into success. |

using Microsoft.VisualStudio.TestTools.UnitTesting;

// All tests in this project are pure functions over their inputs (no shared state, no IO),
// so method-level parallelism is safe and keeps the suite fast.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

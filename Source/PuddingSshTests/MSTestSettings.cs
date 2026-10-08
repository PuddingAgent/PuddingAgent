using Microsoft.VisualStudio.TestTools.UnitTesting;

// 显式声明并行策略（MSTest 分析器 MSTEST0001）：这些测试只读 fixture、无共享可变状态，
// 方法级并行是安全的，也能让「组件可在宿主运行中独立快速测试」这条收益更明显。
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

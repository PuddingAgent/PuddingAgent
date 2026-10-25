using Microsoft.VisualStudio.TestTools.UnitTesting;

// 显式声明并行策略（MSTEST0001）：组件测试互相独立、无共享静态状态，按方法级并行。
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

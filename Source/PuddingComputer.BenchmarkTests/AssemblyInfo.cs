using Microsoft.VisualStudio.TestTools.UnitTesting;

// MSTEST0001 要求显式声明并行策略。本工程全是不碰共享状态的纯函数测试，
// 方法级并行最合适。
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

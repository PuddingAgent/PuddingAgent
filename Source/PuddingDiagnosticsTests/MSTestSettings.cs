// 显式声明测试并行化（MSTEST0001）：本组件的用例是纯函数测试，无共享状态、无 IO、无端口，
// 因此按方法级并行执行是安全的，并且能保证「宿主运行中也能快速迭代诊断规则」。
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

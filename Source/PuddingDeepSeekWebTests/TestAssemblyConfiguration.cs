using Microsoft.VisualStudio.TestTools.UnitTesting;

// 组件测试显式串行：Live 用例共享一次真实 Search 的结果缓存，串行执行既省调用费用也更可断言。
// 同时满足 MSTEST0001（显式声明并行策略），避免分析器告警淹没真正的边界断言。
[assembly: DoNotParallelize]

using Microsoft.VisualStudio.TestTools.UnitTesting;

// 显式关闭并行化（MSTEST0001）：
// 本项目的用例会跑真实 Lucene 写入**系统临时目录**、并依赖进程级共享状态（JiebaSegmenterPool 单例、
// 日志/诊断文件），逐条串行执行才能保证"磁盘快照差 == 0"这类断言不被别的用例的 IO 干扰。
[assembly: DoNotParallelize]

using Microsoft.VisualStudio.TestTools.UnitTesting;

// 这组用例各自使用独立的临时库与方法内状态，串行执行只为让失败时的库文件与日志保持一一对应。
[assembly: DoNotParallelize]

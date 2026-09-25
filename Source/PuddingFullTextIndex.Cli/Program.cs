using PuddingFullTextIndex.Cli;

// 进程入口只做一件事：把参数与标准输出/错误交给可注入的核心。
// 参数解析、组合根装配、命令执行与退出码全部在 SupplyCli.RunAsync —— 单测因此不必起进程。
return await SupplyCli.RunAsync(args, Console.Out, Console.Error);

using PuddingMemoryEngine;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Demo;
using PuddingRuntime.Services.Sandbox;
using PuddingRuntime.Services.Skills;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// -- Runtime 核心服务 --------------------------------------------------
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSingleton<InMemoryRuntimeSessionStore>();
builder.Services.AddSingleton<SandboxExecutor>();

// -- Sandbox（Docker 隔离容器） -----------------------------------------
builder.Services.AddSingleton<AgentContainerRegistry>();
builder.Services.AddSingleton<ISandboxProvider, DockerSandboxProvider>();

// -- Skill Runtime（Agent 可调工具套件） ---------------------------------
builder.Services.AddSingleton<IAgentSkill, BashSkill>();
builder.Services.AddSingleton<IAgentSkill, PythonSkill>();
builder.Services.AddSingleton<IAgentSkill, ReadFileSkill>();
builder.Services.AddSingleton<IAgentSkill, WriteFileSkill>();
builder.Services.AddSingleton<IAgentSkill, HttpFetchSkill>();
builder.Services.AddSingleton<SkillRuntime>();

// -- Agent Loop Hooks（生命周期可观测性扩展点） -------------------------
builder.Services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();

// -- Agent Loop 护栏与执行控制 ------------------------------------------
builder.Services.AddSingleton<AgentExecutionGuardrails>();
builder.Services.AddSingleton<ExecutionControlRegistry>();
builder.Services.AddSingleton<ExecutionJournal>();
builder.Services.AddSingleton<CompletionPolicy>();

// -- 记忆引擎 -----------------------------------------------------------
builder.Services.AddSingleton<SessionMemoryStore>();
builder.Services.AddSingleton<WorkspaceMemoryStore>();
builder.Services.AddSingleton<MemoryBoundaryService>();
builder.Services.AddSingleton<MemoryEngine>();

builder.Services.AddSingleton<AgentExecutionService>();

// -- 知识访问桥接（调用 Controller 知识 API） ----------------------------
var controllerBase = builder.Configuration["Pudding:ControllerEndpoint"]
    ?? "http://localhost:5000";
builder.Services.AddHttpClient<KnowledgeAccessRuntime>(c =>
    c.BaseAddress = new Uri(controllerBase));

// -- LLM 路由桥接（Runtime -> Controller -> LLM Provider） --------------
builder.Services.AddHttpClient<IRuntimeLlmClient, ControllerRoutedLlmClient>(c =>
    c.BaseAddress = new Uri(controllerBase));

// -- HttpFetchSkill 所需命名 HttpClient ----------------------------------
builder.Services.AddHttpClient("HttpFetchSkill", c =>
    c.Timeout = TimeSpan.FromSeconds(15));

// -- 嵌入式宿主原生能力桥接 ---------------------------------------------
// 注册所有实现了 INativeHostBridge 的桥接器
builder.Services.AddSingleton<INativeHostBridge, DemoDesktopHostBridge>();
// NativeCapabilityExecutor 聚合所有 bridge，供执行与注册上报使用
builder.Services.AddSingleton<NativeCapabilityExecutor>();

// -- 后台心跳清理服务 ---------------------------------------------------
builder.Services.AddHostedService<HeartbeatService>();
// -- Runtime 自注册服务（向 Controller 注册并定期续约） -------------------
builder.Services.AddHostedService<RuntimeSelfRegistrationService>();

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.Run();

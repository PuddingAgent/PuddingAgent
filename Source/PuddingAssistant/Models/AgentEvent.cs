namespace PuddingAssistant.Models;

/// <summary>Agent 事件流（用于 UI 渲染）</summary>
public abstract record AgentEvent;

public sealed record ThinkingEvent(string Thought) : AgentEvent;
public sealed record ToolCallEvent(string ToolName, string Arguments) : AgentEvent;
public sealed record ToolResultEvent(string ToolName, string Result) : AgentEvent;
public sealed record AnswerEvent(string Content) : AgentEvent;
public sealed record ErrorEvent(string Message) : AgentEvent;

/// <summary>Reasoning / thinking chain delta (DeepSeek Reasoner, o1, etc.)</summary>
public sealed record ReasoningEvent(string Delta) : AgentEvent;

/// <summary>Streaming answer token delta</summary>
public sealed record StreamingAnswerEvent(string Delta) : AgentEvent;

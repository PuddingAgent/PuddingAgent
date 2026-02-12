using PuddingAssistant.Abstractions;

namespace PuddingAssistant.Core;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? GetTool(string name) =>
        _tools.GetValueOrDefault(name);

    public IReadOnlyList<ITool> GetAllTools() =>
        [.. _tools.Values];
}

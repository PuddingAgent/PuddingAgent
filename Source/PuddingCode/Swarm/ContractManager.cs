using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// 契约管理器实现。用于定义契约并验证 Worker 实现是否匹配。
/// </summary>
public sealed class ContractManager : IContractManager
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _contractsDirectory;

    /// <summary>
    /// 初始化 <see cref="ContractManager"/> 类的新实例。
    /// </summary>
    /// <param name="baseDirectory">基础目录（默认为 .pudding/swarm）</param>
    public ContractManager(string? baseDirectory = null)
    {
        var puddingDir = baseDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".pudding", "swarm");
        _contractsDirectory = Path.Combine(puddingDir, "contracts");

        // 确保目录存在
        Directory.CreateDirectory(_contractsDirectory);
    }

    /// <inheritdoc />
    public Task<Contract> DefineContractAsync(string specification, CancellationToken ct = default)
    {
        // 解析规格说明，提取文件路径和符号
        var files = ExtractFilePaths(specification);
        var symbols = ExtractSymbols(specification);

        // 创建唯一的契约 ID
        var id = $"contract-{Guid.NewGuid():N}";

        var contract = new Contract
        {
            Id = id,
            Files = files,
            Symbols = symbols,
            Specification = specification
        };

        // 保存到磁盘
        return SaveContractAsync(contract, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateContractAsync(string contractId, string worktreePath, CancellationToken ct = default)
    {
        // 加载契约
        var contract = await LoadContractAsync(contractId, ct);
        if (contract is null)
            return false;

        // 验证所有符号是否在工作树中实现
        foreach (var symbol in contract.Symbols)
        {
            if (!await SymbolExistsAsync(symbol, worktreePath, ct))
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<string> InitializeSwarmDirectoryAsync(CancellationToken ct = default)
    {
        var swarmRoot = Path.Combine(Directory.GetCurrentDirectory(), ".pudding", "swarm");

        // 创建目录结构
        var directories = new[]
        {
            swarmRoot,
            Path.Combine(swarmRoot, "contracts"),
            Path.Combine(swarmRoot, "tasks"),
            Path.Combine(swarmRoot, "messages"),
            Path.Combine(swarmRoot, "worktrees")
        };

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
        }

        // 创建配置文件（如果不存在）
        var configPath = Path.Combine(swarmRoot, "config.json");
        if (!File.Exists(configPath))
        {
            var config = new
            {
                version = "0.8",
                created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                mode = "local"
            };

            var json = JsonSerializer.Serialize(config, s_jsonOptions);
            await File.WriteAllTextAsync(configPath, json, ct);
        }

        return swarmRoot;
    }

    /// <summary>
    /// 从规格说明中提取文件路径。
    /// </summary>
    /// <param name="specification">规格说明文本</param>
    /// <returns>文件路径列表</returns>
    private static List<string> ExtractFilePaths(string specification)
    {
        var files = new List<string>();

        // 匹配模式："create file X", "modify Y", "file Z", "X.cs" 等
        var patterns = new[]
        {
            @"create\s+file\s+([^\s,]+)",           // create file X
            @"modify\s+([^\s,]+)",                   // modify Y
            @"file\s+([^\s,]+)",                     // file Z
            @"in\s+file\s+([^\s,]+)",                // in file A
            @"to\s+file\s+([^\s,]+)",                // to file B
            @"([a-zA-Z0-9_\-/\\.]+\.(cs|json|md|txt))" // 文件名带扩展名
        };

        var seen = new HashSet<string>();

        foreach (var pattern in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(specification, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var match in matches.Cast<System.Text.RegularExpressions.Match>())
            {
                // 对于通用文件名模式，只取第一组；对于具体命令模式，取捕获组
                var filePath = match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : match.Value.Trim();

                // 清理路径，去除引号和多余字符
                filePath = filePath.Trim('"', '\'', '.', ',', ';', ':');

                // 跳过太短的匹配（可能是误匹配）
                if (filePath.Length < 2)
                    continue;

                // 确保是合理的路径格式
                if (!filePath.Contains('/') && !filePath.Contains('\\') && !filePath.Contains('.'))
                    continue;

                if (seen.Add(filePath))
                    files.Add(filePath);
            }
        }

        return files;
    }

    /// <summary>
    /// 从规格说明中提取符号（类名、方法名等）。
    /// </summary>
    /// <param name="specification">规格说明文本</param>
    /// <returns>符号列表</returns>
    private static List<string> ExtractSymbols(string specification)
    {
        var symbols = new List<string>();

        // 匹配模式：类名、接口名、方法名
        var patterns = new[]
        {
            @"(?:class|interface|record|struct)\s+([A-Z][a-zA-Z0-9_]*)",  // class/interface/record/struct Name
            @"(?:public|private|protected|internal)?\s*(?:static\s+)?(?:async\s+)?(?:\w+(?:<[^>]+>)?\s+)?([A-Z][a-zA-Z0-9_]*)\s*\(", // Method(
            @"I[A-Z][a-zA-Z0-9_]*",  // 接口名称模式
            @"[A-Z][a-zA-Z0-9_]*Service",  // XxxService 模式
            @"[A-Z][a-zA-Z0-9_]*Manager",  // XxxManager 模式
            @"[A-Z][a-zA-Z0-9_]*Factory",  // XxFactory 模式
            @"[A-Z][a-zA-Z0-9_]*Provider"   // XxProvider 模式
        };

        var seen = new HashSet<string>();

        foreach (var pattern in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(specification, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var match in matches.Cast<System.Text.RegularExpressions.Match>())
            {
                var symbol = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                symbol = symbol.Trim();

                // 跳过 C# 关键字和常见非符号词
                if (IsKeyword(symbol))
                    continue;

                if (seen.Add(symbol))
                    symbols.Add(symbol);
            }
        }

        return symbols;
    }

    /// <summary>
    /// 判断是否为 C# 关键字或常见非符号词。
    /// </summary>
    private static bool IsKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "class", "interface", "record", "struct", "enum",
            "public", "private", "protected", "internal",
            "static", "async", "await", "virtual", "override",
            "abstract", "sealed", "readonly", "const",
            "void", "var", "string", "int", "bool", "object",
            "if", "else", "for", "foreach", "while", "do", "switch",
            "return", "yield", "break", "continue", "throw",
            "try", "catch", "finally", "using", "namespace",
            "new", "this", "base", "typeof", "nameof",
            "The", "A", "An", "Is", "Are", "Was", "Were",
            "To", "From", "With", "For", "In", "On", "At"
        };

        return keywords.Contains(word);
    }

    /// <summary>
    /// 保存契约到磁盘。
    /// </summary>
    private async Task<Contract> SaveContractAsync(Contract contract, CancellationToken ct)
    {
        var filePath = GetContractFilePath(contract.Id);
        var json = JsonSerializer.Serialize(contract, s_jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
        return contract;
    }

    /// <summary>
    /// 从磁盘加载契约。
    /// </summary>
    private async Task<Contract?> LoadContractAsync(string contractId, CancellationToken ct)
    {
        var filePath = GetContractFilePath(contractId);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<Contract>(json, s_jsonOptions);
    }

    /// <summary>
    /// 获取契约文件路径。
    /// </summary>
    private string GetContractFilePath(string contractId) =>
        Path.Combine(_contractsDirectory, $"{contractId}.json");

    /// <summary>
    /// 验证符号是否在工作树中存在。
    /// </summary>
    private async Task<bool> SymbolExistsAsync(string symbol, string worktreePath, CancellationToken ct)
    {
        // 在工作树中查找包含该符号的 .cs 文件
        var csFiles = Directory.GetFiles(worktreePath, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            var content = await File.ReadAllTextAsync(file, ct);

            // 简单检查符号是否在文件中出现
            // 更精确的检查可以使用 Roslyn，但根据需求，Phase 1/2 使用简单反射/文本匹配
            if (content.Contains(symbol))
            {
                // 进一步验证：尝试加载程序集并检查类型
                if (await TypeExistsInAssemblyAsync(symbol, file, worktreePath, ct))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查类型是否在程序集中存在。
    /// </summary>
    private async Task<bool> TypeExistsInAssemblyAsync(string typeName, string sourceFile, string worktreePath, CancellationToken ct)
    {
        // Phase 1/2 简单实现：检查源文件中是否定义了该类型
        // 更复杂的实现会使用 Roslyn 编译并检查，但这里使用简单的文本匹配

        var content = await File.ReadAllTextAsync(sourceFile, ct);

        // 检查类/接口/记录定义
        var typePattern = $@"(?:public|private|protected|internal)?\s*(?:abstract|static|sealed|partial)?\s*(?:class|interface|record|struct|enum)\s+{Regex.Escape(typeName)}";
        if (System.Text.RegularExpressions.Regex.IsMatch(content, typePattern))
            return true;

        // 检查方法定义
        var methodPattern = $@"(?:public|private|protected|internal)?\s*(?:static|async|virtual|override|abstract)?\s*\w+(?:<[^>]+>)?\s+{Regex.Escape(typeName)}\s*\(";
        if (System.Text.RegularExpressions.Regex.IsMatch(content, methodPattern))
            return true;

        return false;
    }
}

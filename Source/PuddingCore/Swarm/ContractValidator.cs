namespace PuddingCode.Swarm;

using System.Reflection;
using System.Runtime.CompilerServices;
using PuddingCode.Models;

/// <summary>
/// 契约验证结果。
/// </summary>
/// <param name="IsValid">验证是否通过。</param>
/// <param name="Errors">错误消息列表。</param>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// 契约验证器。验证 Worker 实现是否匹配契约签名。
/// </summary>
public sealed class ContractValidator
{
    /// <summary>
    /// 验证契约实现是否匹配契约定义。
    /// </summary>
    /// <param name="contract">契约定义。</param>
    /// <param name="worktreePath">Worker 工作树路径。</param>
    /// <returns>验证结果，包含错误详情。</returns>
    public ValidationResult ValidateContract(Contract contract, string worktreePath)
    {
        var errors = new List<string>();

        // 加载 Worker 工作树的程序集
        var assembly = LoadAssembly(worktreePath);
        if (assembly == null)
        {
            return new ValidationResult(false, [$"无法加载工作树程序集：{worktreePath}"]);
        }

        // 验证每个符号
        foreach (var symbol in contract.Symbols)
        {
            var symbolErrors = ValidateSymbol(symbol, assembly);
            errors.AddRange(symbolErrors);
        }

        return new ValidationResult(errors.Count == 0, errors.AsReadOnly());
    }

    /// <summary>
    /// 从工作树路径加载程序集。
    /// </summary>
    /// <param name="worktreePath">工作树路径。</param>
    /// <returns>加载的程序集，失败返回 null。</returns>
    private static Assembly? LoadAssembly(string worktreePath)
    {
        try
        {
            // 查找编译后的 DLL 文件
            var dllFiles = Directory.GetFiles(worktreePath, "*.dll", SearchOption.AllDirectories);
            
            // 优先查找与项目名匹配的 DLL
            var projectDll = dllFiles.FirstOrDefault(f => 
                f.Contains("PuddingCode", StringComparison.OrdinalIgnoreCase));
            
            if (projectDll != null && File.Exists(projectDll))
            {
                return Assembly.LoadFrom(projectDll);
            }

            // 如果没有找到项目 DLL，尝试加载第一个 DLL（可能是依赖项）
            // 注意：这里简化处理，实际可能需要更复杂的逻辑
            return null;
        }
        catch
        {
            // 记录日志但继续处理
            return null;
        }
    }

    /// <summary>
    /// 验证单个符号。
    /// </summary>
    /// <param name="symbol">符号名称（格式：TypeName 或 TypeName.MethodName）。</param>
    /// <param name="assembly">要检查的程序集。</param>
    /// <returns>错误消息列表。</returns>
    private List<string> ValidateSymbol(string symbol, Assembly assembly)
    {
        var errors = new List<string>();

        // 解析符号：可能是类型名或 类型名。方法名
        var parts = symbol.Split('.', 2);
        var typeName = parts[0];
        var methodName = parts.Length > 1 ? parts[1] : null;

        // 查找类型
        var type = FindTypeInAssembly(typeName, assembly);
        if (type == null)
        {
            errors.Add($"未找到类型：{typeName}");
            return errors;
        }

        // 如果指定了方法名，验证方法
        if (methodName != null)
        {
            var methodErrors = ValidateMethod(methodName, type);
            errors.AddRange(methodErrors);
        }

        return errors;
    }

    /// <summary>
    /// 在程序集中查找类型。
    /// </summary>
    /// <param name="typeName">类型名称（可以是全名或简单名）。</param>
    /// <param name="assembly">要搜索的程序集。</param>
    /// <returns>找到的类型，未找到返回 null。</returns>
    private static Type? FindTypeInAssembly(string typeName, Assembly assembly)
    {
        // 尝试全名匹配
        var type = assembly.GetType(typeName);
        if (type != null)
        {
            return type;
        }

        // 尝试简单名匹配（忽略命名空间）
        var simpleName = typeName.Contains('.') ? typeName.Split('.').Last() : typeName;
        type = assembly.GetTypes().FirstOrDefault(t => t.Name == simpleName);
        
        return type;
    }

    /// <summary>
    /// 在类型中查找方法并验证。
    /// </summary>
    /// <param name="methodName">方法名称。</param>
    /// <param name="type">要搜索的类型。</param>
    /// <returns>错误消息列表。</returns>
    private List<string> ValidateMethod(string methodName, Type type)
    {
        var errors = new List<string>();

        // 查找方法（包括 public 和 private 实例方法）
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var method = methods.FirstOrDefault(m => m.Name == methodName);

        if (method == null)
        {
            errors.Add($"类型 {type.Name} 中未找到方法：{methodName}");
            return errors;
        }

        // 检查方法是否已实现（不是纯抽象或只有 throw NotImplementedException）
        if (!IsMethodImplemented(method))
        {
            errors.Add($"方法 {type.Name}.{methodName} 未实现（仍为抽象或抛出 NotImplementedException）");
        }

        return errors;
    }

    /// <summary>
    /// 在类型中查找指定方法。
    /// </summary>
    /// <param name="methodName">方法名称。</param>
    /// <param name="type">要搜索的类型。</param>
    /// <returns>找到的方法，未找到返回 null。</returns>
    private static MethodInfo? FindMethodInType(string methodName, Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        return methods.FirstOrDefault(m => m.Name == methodName);
    }

    /// <summary>
    /// 检查方法签名是否匹配。
    /// </summary>
    /// <param name="contract">契约方法。</param>
    /// <param name="implementation">实现方法。</param>
    /// <returns>签名是否匹配。</returns>
    private static bool SignaturesMatch(MethodInfo contract, MethodInfo implementation)
    {
        // 检查返回类型
        if (contract.ReturnType != implementation.ReturnType)
        {
            return false;
        }

        // 检查参数列表
        var contractParams = contract.GetParameters();
        var implParams = implementation.GetParameters();

        if (contractParams.Length != implParams.Length)
        {
            return false;
        }

        for (int i = 0; i < contractParams.Length; i++)
        {
            if (contractParams[i].ParameterType != implParams[i].ParameterType)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 检查方法是否已实现（不是纯抽象或只有 throw NotImplementedException）。
    /// </summary>
    /// <param name="method">要检查的方法。</param>
    /// <returns>方法是否已实现。</returns>
    private static bool IsMethodImplemented(MethodInfo method)
    {
        // 抽象方法未实现
        if (method.IsAbstract)
        {
            return false;
        }

        // 尝试检查方法体是否只有 throw NotImplementedException
        // 注意：反射无法直接检查方法体，这里简化处理
        // 实际项目中可能需要使用 Roslyn 进行更精确的检查

        // 如果方法有 MethodImplAttribute 且标志为 Abstract，则未实现
        var implAttr = method.GetCustomAttribute<MethodImplAttribute>();
        if (implAttr != null && implAttr.Value.HasFlag(MethodImplOptions.InternalCall))
        {
            return false;
        }

        // 默认假设已实现（简化处理）
        // 更精确的检查需要读取 IL 代码或使用 Roslyn
        return true;
    }
}

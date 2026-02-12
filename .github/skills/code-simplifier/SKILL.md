---
name: code-simplifier
description: Use after completing code implementation or modification — before committing. Simplifies and refines C#/XAML/Vue/TypeScript code for clarity, consistency, and maintainability while preserving all functionality. Focuses on recently modified code. Triggers: code review, refactoring, polishing, cleaning up.
argument-hint: "指定要简化的文件或目录，例如 'Source/Pudding.Agent/Services/' 或留空处理最近修改的代码"
---

# Code Simplifier — 代码精简与重构

## 概述

在保持功能完全不变的前提下，提升代码的清晰度、一致性和可维护性。
**只精简 HOW，不改变 WHAT。**

**核心原则：代码是写给人读的，恰巧也能被机器执行。**

## 何时使用

**TDD REFACTOR 阶段（必须）：**
- 测试通过后，在提交前执行精简

**代码审阅前（推荐）：**
- 提交 PR/QA 前自检

**跳过：**
- 原型/实验代码（标注 `// PROTOTYPE`）
- 紧急热修复
- 已经过 QA 审阅的代码（除非 QA 明确要求）

## 精简规则

### 1. 保持功能不变

- 永远不改代码做什么 — 只改怎么做
- 所有原有功能、输出、行为必须完整保留

### 2. 遵循项目标准

按 Pudding 架构分层和编码规范：

- 架构分层不可违反：UI → Application → Core/Domain → Infrastructure
- 类注释和方法注释必须保留和补充
- 被吞掉的异常必须有 `ILogger` 日志
- 命名遵循 C# 惯例（PascalCase 公开成员，camelCase 私有字段，`_` 前缀可选）

### 3. 增强清晰度

- **降低嵌套**：提前 return 替代深层 if 嵌套
- **消除冗余**：重复代码提取方法、未使用的 using/import 删除
- **提升可读性**：
  - 清晰的变量和方法命名
  - 合并相关逻辑
  - 删除不必要且显而易见的注释
- **避免过度紧凑**：
  - 不用嵌套三元运算符（优先 switch/if-else）
  - 显式优于隐式
  - 不要为了减少行数而牺牲可读性

### 4. 保持平衡 — 避免过度简化

| 不要 | 原因 |
|------|------|
| 合并不相关的逻辑到一个方法 | 违反单一职责 |
| 创建过于"聪明"难以理解的结构 | 维护成本上升 |
| 删除有益的抽象层 | 破坏代码组织 |
| 为减少行数使用嵌套三元/高密度单行 | 降低可读性 |

### 5. 聚焦范围

只精简当前会话中修改或新增的代码，除非明确要求审查更广范围。

## 精简流程

```
1. 识别最近修改的代码段
2. 分析可提升优雅度和一致性的机会
3. 应用项目最佳实践和编码标准
4. 确保所有功能保持不变
5. 运行测试验证精简后的代码
6. 仅记录影响理解的重要变更
```

## C# 特定精简模式

### 模式 1: 降低嵌套

```csharp
// ❌ 深层嵌套
public async Task<Result> ProcessAsync(Data data)
{
    if (data != null)
    {
        if (data.IsValid)
        {
            if (await CanProcessAsync())
            {
                return await DoProcessAsync(data);
            }
        }
    }
    return Result.Fail("处理失败");
}

// ✅ 提前 return
public async Task<Result> ProcessAsync(Data data)
{
    if (data is null) return Result.Fail("数据为空");
    if (!data.IsValid) return Result.Fail("数据无效");
    if (!await CanProcessAsync()) return Result.Fail("无法处理");

    return await DoProcessAsync(data);
}
```

### 模式 2: 消除冗余变量

```csharp
// ❌ 不必要的临时变量
var service = container.Resolve<IUserService>();
var result = service.GetUser(id);
return result;

// ✅ 直接返回
return container.Resolve<IUserService>().GetUser(id);
```

### 模式 3: 使用模式匹配

```csharp
// ❌ 传统类型检查
if (entity is PhysicalAsset)
{
    var asset = (PhysicalAsset)entity;
    return asset.Location;
}

// ✅ 模式匹配
if (entity is PhysicalAsset { Location: var location })
    return location;
```

### 模式 4: 集合表达式

```csharp
// ❌ 旧的集合初始化
var list = new List<string>();
list.Add("a");
list.Add("b");

// ✅ 集合表达式 (C# 12+)
List<string> list = ["a", "b"];
```

## Vue/TypeScript 特定模式

### 降低响应式冗余

```typescript
// ❌ 冗余
const isLoading = ref(false);
const loadData = async () => {
  isLoading.value = true;
  try {
    await fetchData();
  } finally {
    isLoading.value = false;
  }
};

// ✅ 使用 composable
const { execute, isLoading } = useAsync(fetchData);
```

## 自检清单

- [ ] 测试仍然全部通过
- [ ] 没有改变任何公开 API 签名（除非有意重构）
- [ ] 遵循了项目架构分层
- [ ] 保留了必要的日志和异常处理
- [ ] 删除了调试代码和注释掉的代码
- [ ] 新增的复杂逻辑有注释说明 WHY（不是 WHAT）

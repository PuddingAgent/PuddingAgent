# Task 11 — 权限与安全沙盒设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 10 (Agent 能力体系)、D02 (ShellTool / FileTool)
> **目标：** 为 Agent 的文件访问和命令执行建立路径沙盒、指令分级白名单和人工授权机制，防止 AI 产生非预期破坏
> **参考：** [Claude Code EP07 Permission Pipeline](../../Docs/claude-reviews-claude/architecture/07-permission-pipeline.md) — 7 层纵深防御、fail-closed/fail-open 判定原则

---

## 参考设计：7 层纵深防御

借鉴 Claude Code 的 `hasPermissionsToUseToolInner()` 权限评估管道：

```
Layer 1: 工具级 Deny 规则 → 硬拒绝，不可覆盖
Layer 2: 工具级 Ask 规则  → 需要用户确认（沙箱例外：自动允许）
Layer 3: 工具自定义检查   → 每个工具实现自己的 checkPermissions()
Layer 4: 不可绕过的安全护栏 → .git/.claude/密钥文件保护
Layer 5: Bypass 模式判断   → 计划模式/信任模式可跳过确认
Layer 6: Always-Allow 规则 → MCP 服务级白名单
Layer 7: 默认 → Ask 用户   → 兜底策略
```

### fail-closed vs fail-open 判定原则

| 场景 | 策略 | 说明 |
|------|------|------|
| Bash 命令解析失败 | **fail-closed** → 拒绝执行 | 安全相关 |
| 权限检查超时 | **fail-closed** → 默认拒绝 | 安全相关 |
| 未知命令类别 | **fail-closed** → 需用户确认 | 安全相关 |
| 策略加载失败 | **fail-open** → 允许所有 | 可用性优先 |
| 远程配置不可用 | **fail-open** → 使用本地缓存 | 可用性优先 |

**Pudding 的对应策略**：安全相关检查 fail-closed（未知→拒绝），可用性相关检查 fail-open（服务不可用→降级放行）。

---

## 目录

1. [设计原则](#一设计原则)
2. [路径沙盒](#二路径沙盒)
3. [指令分级](#三指令分级)
4. [命令白名单](#四命令白名单)
5. [文件类型白名单](#五文件类型白名单)
6. [黑名单与危险模式](#六黑名单与危险模式)
7. [人工授权 UI](#七人工授权-ui)
8. [LLM 边界告知](#八llm-边界告知)
9. [PermissionGuard 实现](#九permissionguard-实现)
10. [实现路线](#十实现路线)

---

## 一、设计原则

| 原则 | 说明 |
|---|---|
| **默认拒绝** | 不在白名单中的命令/路径一律拒绝，需人工显式授权 |
| **最小权限** | Agent 只获得当前任务所需的最小文件和命令访问范围 |
| **可审计** | 所有提权操作和用户决策记录到 `security.log` |
| **教育反馈** | 越权时通过结构化错误信息"教育" LLM 自觉遵守边界 |
| **跨平台** | 白名单覆盖 Windows / Linux / macOS 三平台等价命令 |

---

## 二、路径沙盒

### 2.1 分区模型

```
┌─────────────────────────────────────────────────┐
│  受信任区 (Trusted Zone)                         │
│  项目根目录及其所有子目录                          │
│  → Agent 拥有受限的读写权限                       │
├─────────────────────────────────────────────────┤
│  管控区 (Restricted Zone)                        │
│  项目根目录以外的一切路径                          │
│  → 默认拒绝，需人工授权                           │
├─────────────────────────────────────────────────┤
│  禁区 (Forbidden Zone)                           │
│  系统关键目录，任何情况下都禁止访问                  │
│  → 无法通过授权绕过                               │
└─────────────────────────────────────────────────┘
```

### 2.2 禁区路径（硬编码，不可覆盖）

| 平台 | 禁区路径 |
|---|---|
| **Windows** | `C:\Windows\`, `C:\Program Files\`, `C:\Program Files (x86)\`, `%SYSTEMROOT%\` |
| **Linux** | `/boot/`, `/sbin/`, `/usr/sbin/`, `/proc/`, `/sys/`, `/dev/` |
| **macOS** | `/System/`, `/usr/sbin/`, `/private/var/`, `/Library/LaunchDaemons/` |
| **通用** | `~/.ssh/`, `~/.gnupg/`, `~/.aws/`, `~/.azure/`, `~/.kube/` |

### 2.3 校验逻辑

```
输入路径 → Path.GetFullPath() 解析绝对路径
  → 匹配禁区？ → 硬拒绝（不可授权）
  → 在项目根内？ → 允许（受文件类型白名单约束）
  → 项目根外？ → 触发人工授权弹窗
```

---

## 三、指令分级

所有 Agent 可执行的操作按危险程度分为三个安全级别：

### Level 0 — 只读（Read-Only）

| 特征 | 说明 |
|---|---|
| 风险 | 无 — 不修改任何文件或系统状态 |
| 权限 | 自动允许，无需确认 |
| 示例 | 读取文件、列出目录、查看 Git 状态、编译检查 |

### Level 1 — 项目写入（Project Write）

| 特征 | 说明 |
|---|---|
| 风险 | 低 — 仅在受信任区内修改项目文件 |
| 权限 | 受信任区内静默执行；管控区触发授权 |
| 约束 | 受文件类型白名单限制（见第五节） |
| 示例 | 写入 `.cs` 文件、`git commit`、创建目录 |

### Level 2 — 系统执行（System Execution）

| 特征 | 说明 |
|---|---|
| 风险 | 高 — 可能修改系统状态、安装软件、删除数据 |
| 权限 | **必须经过人工确认** |
| 示例 | 安装包管理器依赖、删除目录树、运行生成的二进制 |

---

## 四、命令白名单

### 4.1 白名单总表

以下命令在 **受信任区** 内自动允许执行。跨平台等价命令归入同一行。

#### 开发工具链

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `dotnet` | 全平台 | L0/L1 | .NET CLI（build/test/run/publish/format/new） |
| `msbuild` | 全平台 | L0/L1 | MSBuild 编译 |
| `nuget` | 全平台 | L1 | NuGet 包管理 |
| `npm` | 全平台 | L1 | Node.js 包管理 |
| `npx` | 全平台 | L1 | Node.js 包运行器 |
| `yarn` | 全平台 | L1 | Yarn 包管理 |
| `pnpm` | 全平台 | L1 | pnpm 包管理 |
| `node` | 全平台 | L1 | Node.js 运行时 |
| `python` / `python3` | 全平台 | L1 | Python 运行时 |
| `pip` / `pip3` | 全平台 | L1 | Python 包管理 |
| `cargo` | 全平台 | L1 | Rust 包管理/编译 |
| `rustc` | 全平台 | L1 | Rust 编译器 |
| `go` | 全平台 | L1 | Go 工具链 |
| `javac` | 全平台 | L0 | Java 编译器 |
| `java` | 全平台 | L1 | Java 运行时 |
| `mvn` | 全平台 | L1 | Maven 构建 |
| `gradle` | 全平台 | L1 | Gradle 构建 |

#### 版本控制

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `git` | 全平台 | L0/L1 | Git 版本控制（status/log/diff 为 L0；add/commit/push 为 L1） |
| `gh` | 全平台 | L1 | GitHub CLI |

#### 文件查看（只读）

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `cat` | Linux/macOS | L0 | 查看文件内容 |
| `type` | Windows | L0 | 查看文件内容 |
| `head` / `tail` | Linux/macOS | L0 | 查看文件头/尾 |
| `less` / `more` | 全平台 | L0 | 分页查看 |
| `wc` | Linux/macOS | L0 | 字数/行数统计 |

#### 文件搜索

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `grep` / `egrep` | Linux/macOS | L0 | 文本搜索 |
| `findstr` | Windows | L0 | 文本搜索 |
| `find` | Linux/macOS | L0 | 文件查找 |
| `where` | Windows | L0 | 查找可执行文件路径 |
| `which` | Linux/macOS | L0 | 查找可执行文件路径 |
| `rg` (ripgrep) | 全平台 | L0 | 高速文本搜索 |
| `fd` | 全平台 | L0 | 高速文件查找 |
| `ag` (silver searcher) | 全平台 | L0 | 文本搜索 |

#### 目录与文件操作

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `ls` | Linux/macOS | L0 | 列出目录 |
| `dir` | Windows | L0 | 列出目录 |
| `pwd` | Linux/macOS | L0 | 显示当前目录 |
| `cd` | 全平台 | L0 | 切换目录 |
| `mkdir` | 全平台 | L1 | 创建目录 |
| `cp` / `copy` | Linux·macOS / Windows | L1 | 复制文件 |
| `mv` / `move` | Linux·macOS / Windows | L1 | 移动/重命名文件 |
| `touch` | Linux/macOS | L1 | 创建空文件 |
| `tree` | 全平台 | L0 | 目录树展示 |
| `stat` | Linux/macOS | L0 | 文件状态信息 |
| `file` | Linux/macOS | L0 | 文件类型检测 |
| `diff` | 全平台 | L0 | 文件差异比较 |
| `patch` | 全平台 | L1 | 应用补丁 |

#### 系统信息（只读）

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `echo` | 全平台 | L0 | 输出文本 |
| `env` / `printenv` | Linux/macOS | L0 | 查看环境变量 |
| `set` | Windows | L0 | 查看环境变量 |
| `uname` | Linux/macOS | L0 | 系统信息 |
| `hostname` | 全平台 | L0 | 主机名 |
| `whoami` | 全平台 | L0 | 当前用户 |
| `date` | 全平台 | L0 | 当前日期时间 |
| `df` | Linux/macOS | L0 | 磁盘空间 |
| `du` | Linux/macOS | L0 | 目录大小 |
| `ps` | Linux/macOS | L0 | 进程列表 |
| `tasklist` | Windows | L0 | 进程列表 |
| `systeminfo` | Windows | L0 | 系统信息 |

#### 文本处理

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `sed` | Linux/macOS | L1 | 流编辑器（项目内文件） |
| `awk` | Linux/macOS | L0 | 文本处理 |
| `sort` | 全平台 | L0 | 排序 |
| `uniq` | Linux/macOS | L0 | 去重 |
| `cut` | Linux/macOS | L0 | 列提取 |
| `tr` | Linux/macOS | L0 | 字符替换 |
| `xargs` | Linux/macOS | L1 | 参数传递（可能触发写操作） |
| `jq` | 全平台 | L0 | JSON 处理 |
| `yq` | 全平台 | L0 | YAML 处理 |

#### 网络诊断（只读）

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `ping` | 全平台 | L0 | 网络连通性检测 |
| `nslookup` | 全平台 | L0 | DNS 查询 |
| `dig` | Linux/macOS | L0 | DNS 查询 |
| `traceroute` / `tracert` | Linux·macOS / Windows | L0 | 路由追踪 |
| `netstat` | 全平台 | L0 | 网络状态 |
| `ss` | Linux | L0 | Socket 统计 |
| `ipconfig` | Windows | L0 | 网络配置 |
| `ifconfig` / `ip` | Linux/macOS | L0 | 网络配置 |

#### 压缩工具

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `tar` | Linux/macOS | L1 | 打包/解包 |
| `zip` / `unzip` | 全平台 | L1 | ZIP 压缩/解压 |
| `gzip` / `gunzip` | Linux/macOS | L1 | gzip 压缩/解压 |
| `7z` | 全平台 | L1 | 7-Zip 压缩/解压 |

#### 容器与云（受控）

| 命令 | 平台 | Level | 说明 |
|---|---|---|---|
| `docker` | 全平台 | L1 | Docker CLI（build/ps/logs 为 L1；run/rm 为 L2） |
| `kubectl` | 全平台 | L1 | Kubernetes CLI（get/describe 为 L0；apply/delete 为 L2） |
| `az` | 全平台 | L1 | Azure CLI（查询为 L0；部署为 L2） |
| `aws` | 全平台 | L1 | AWS CLI（查询为 L0；修改为 L2） |
| `gcloud` | 全平台 | L1 | Google Cloud CLI |

### 4.2 子命令分级规则

某些命令的安全级别取决于子命令：

```
git status       → L0 (只读)
git add          → L1 (项目写入)
git push         → L2 (远程修改，需确认)
git reset --hard → L2 (破坏性操作)

dotnet build     → L0 (编译检查)
dotnet test      → L1 (执行测试)
dotnet run       → L2 (运行程序)

docker ps        → L0 (查看容器)
docker build     → L1 (构建镜像)
docker run       → L2 (启动容器)
docker rm -f     → L2 (强制删除)

kubectl get      → L0 (查看资源)
kubectl apply    → L2 (部署变更)
kubectl delete   → L2 (删除资源)
```

---

## 五、文件类型白名单

Agent 写入操作受文件扩展名限制：

### 5.1 允许写入（项目内自动放行）

| 类别 | 扩展名 |
|---|---|
| **C# / .NET** | `.cs`, `.csproj`, `.sln`, `.props`, `.targets`, `.razor`, `.axaml` |
| **Web 前端** | `.js`, `.ts`, `.jsx`, `.tsx`, `.css`, `.scss`, `.less`, `.html`, `.vue`, `.svelte` |
| **标记 / 配置** | `.md`, `.json`, `.yaml`, `.yml`, `.xml`, `.toml`, `.ini`, `.env`, `.editorconfig` |
| **脚本** | `.py`, `.rb`, `.lua`, `.rs`, `.go`, `.java`, `.kt`, `.swift` |
| **数据** | `.sql`, `.csv`, `.txt`, `.log` |
| **Docker / CI** | `Dockerfile`, `.dockerignore`, `.gitignore`, `.github/**` |

### 5.2 禁止写入（硬拒绝）

| 类别 | 扩展名 / 模式 |
|---|---|
| **二进制可执行** | `.exe`, `.dll`, `.so`, `.dylib`, `.app`, `.msi`, `.deb`, `.rpm` |
| **脚本可执行** | `.sh`, `.bat`, `.cmd`, `.ps1`, `.psm1`（写入需人工确认） |
| **密钥 / 证书** | `.pem`, `.key`, `.pfx`, `.p12`, `.cer`, `.crt` |
| **系统配置** | `.reg`, `.sys`, `.inf`, `.service` |

---

## 六、黑名单与危险模式

### 6.1 绝对禁止的命令

无论任何情况，以下命令或模式 **不可执行**，即使用户手动授权也被拦截：

| 命令 / 模式 | 平台 | 原因 |
|---|---|---|
| `rm -rf /` | Linux/macOS | 系统级递归删除 |
| `del /s /q C:\` | Windows | 系统级递归删除 |
| `format` | Windows | 格式化磁盘 |
| `mkfs` | Linux | 格式化文件系统 |
| `dd if=` | Linux/macOS | 磁盘原始写入 |
| `sudo` / `su` | Linux/macOS | 提权执行 |
| `runas` | Windows | 提权执行 |
| `chmod 777` | Linux/macOS | 全局权限开放 |
| `curl \| bash` / `wget \| sh` | 全平台 | 远程脚本盲执行 |
| `eval` (shell) | 全平台 | 动态代码执行 |
| `:(){ :\|:& };:` | Linux/macOS | Fork 炸弹 |
| `shutdown` / `reboot` | 全平台 | 关机/重启 |
| `kill -9 1` | Linux/macOS | 杀死 init 进程 |
| `taskkill /f /im *` | Windows | 批量杀进程 |
| `reg delete` | Windows | 删除注册表项 |
| `iptables -F` | Linux | 清空防火墙规则 |
| `netsh advfirewall reset` | Windows | 重置防火墙 |

### 6.2 危险参数模式检测

即使命令本身在白名单中，以下参数模式需要拦截：

| 模式 | 说明 |
|---|---|
| `--force` 配合删除操作 | `git clean -fd`, `rm -f` 等 |
| `> /dev/sda` | 直接写磁盘 |
| 管道到 `bash`/`sh`/`cmd`/`powershell` | 链式执行不可控命令 |
| `--no-preserve-root` | 绕过安全检查 |
| `-rf` 配合项目根以外路径 | 递归强制删除 |
| 环境变量注入（`$()`, `` ` ` ``） | 命令注入攻击向量 |

---

## 七、人工授权 UI

### 7.1 桌面端（Avalonia）

权限拦截时，Swarm 拓扑中的交互流程：

1. **节点变色** — 发起操作的 Agent 节点由当前状态色变为 **警戒橙 `#F5A623`**
2. **授权气泡** — 节点上方弹出带锁图标的气泡：

```
┌─────────────────────────────────────────────┐
│  🔒 "抹茶布丁" 请求权限                       │
│                                              │
│  指令:  rm -rf ./old_backup                  │
│  路径:  /project/old_backup/                 │
│  理由:  清理过时的备份以释放空间               │
│  级别:  ⚠️ Level 2 (System Execution)        │
│                                              │
│  [ 拒绝 ]  [ 允许一次 ]  [ 始终允许该路径 ]   │
└─────────────────────────────────────────────┘
```

3. **审计日志** — 用户选择记录到 `security.log`

### 7.2 CLI 端（Spectre.Console）

```text
⚠️  PERMISSION REQUEST ─────────────────────────
│ Agent:   抹茶布丁 (worker-1)
│ Command: rm -rf ./old_backup
│ Path:    /project/old_backup/
│ Level:   Level 2 (System Execution)
│ Reason:  清理过时的备份以释放空间
├───────────────────────────────────────────────
│ [D]eny  [A]llow once  [P]ermit path always
└───────────────────────────────────────────────
```

### 7.3 授权决策持久化

| 用户选择 | 效果 |
|---|---|
| **拒绝** | 本次拒绝，返回错误给 Agent |
| **允许一次** | 仅本次执行放行 |
| **始终允许该路径** | 将路径加入本会话的信任扩展区 |

---

## 八、LLM 边界告知

### 8.1 越权反馈模板

不在 System Prompt 中塞规则，而是通过 **结构化错误反馈** 教育 LLM：

```
[SECURITY ERROR]: Permission denied.
Action: write_file
Path: /etc/hosts
Reason: Path is outside the project boundary.
Hint: You are only allowed to operate within the project directory.
      If you need external access, use 'consult_leader' to ask the user.
```

```
[SECURITY ERROR]: Command blocked.
Command: curl https://example.com/script.sh | bash
Reason: Piping remote content to shell is forbidden.
Hint: Download the file first with a safe tool, then review its contents.
```

### 8.2 预期行为

经过几次越权反馈后，LLM 会学习到边界规则，在尝试敏感操作前主动发消息：

> "我需要清理 `./old_backup` 目录，这可能需要你的授权。我可以执行吗？"

---

## 九、PermissionGuard 实现

### 9.1 核心类设计

```csharp
/// <summary>Agent 操作的安全级别。</summary>
public enum SecurityLevel
{
    ReadOnly = 0,       // L0: 只读，自动放行
    ProjectWrite = 1,   // L1: 项目内写入
    SystemExecution = 2 // L2: 系统执行，必须人工确认
}

/// <summary>权限校验结果。</summary>
public record PermissionResult(
    bool IsAllowed,
    SecurityLevel Level,
    string? DenialReason = null)
{
    public static PermissionResult Allowed(SecurityLevel level)
        => new(true, level);
    public static PermissionResult RequiresApproval(SecurityLevel level, string reason)
        => new(false, level, reason);
    public static PermissionResult Denied(string reason)
        => new(false, SecurityLevel.SystemExecution, reason);
}
```

### 9.2 PermissionGuard 主逻辑

```csharp
public class PermissionGuard(string projectRoot)
{
    private readonly string _projectRoot = Path.GetFullPath(projectRoot);

    // ── 白名单注册表 ──
    private static readonly HashSet<string> s_l0Commands = [
        "ls", "dir", "cat", "type", "head", "tail", "grep", "findstr",
        "find", "where", "which", "rg", "fd", "ag", "pwd", "cd",
        "tree", "stat", "file", "diff", "echo", "env", "printenv",
        "set", "uname", "hostname", "whoami", "date", "df", "du",
        "ps", "tasklist", "systeminfo", "awk", "sort", "uniq", "cut",
        "tr", "jq", "yq", "wc", "less", "more", "ping", "nslookup",
        "dig", "traceroute", "tracert", "netstat", "ss", "ipconfig",
        "ifconfig", "ip"
    ];

    private static readonly HashSet<string> s_l1Commands = [
        "dotnet", "msbuild", "nuget", "npm", "npx", "yarn", "pnpm",
        "node", "python", "python3", "pip", "pip3", "cargo", "rustc",
        "go", "javac", "java", "mvn", "gradle",
        "git", "gh",
        "mkdir", "cp", "copy", "mv", "move", "touch", "patch",
        "sed", "xargs",
        "tar", "zip", "unzip", "gzip", "gunzip", "7z",
        "docker", "kubectl", "az", "aws", "gcloud"
    ];

    // ── 绝对黑名单 ──
    private static readonly HashSet<string> s_blockedCommands = [
        "sudo", "su", "runas", "format", "mkfs", "dd",
        "shutdown", "reboot", "halt", "poweroff", "eval"
    ];

    // ── 危险参数模式 ──
    private static readonly string[] s_dangerousPatterns = [
        "| bash", "| sh", "| cmd", "| powershell",
        "--no-preserve-root", "> /dev/sd", ":(){ :",
        "curl|", "wget|"
    ];

    /// <summary>校验命令是否允许执行。</summary>
    public PermissionResult ValidateCommand(string command, string? targetPath)
    {
        var cmdName = ExtractCommandName(command);

        // 1. 绝对黑名单
        if (s_blockedCommands.Contains(cmdName))
            return PermissionResult.Denied($"Command '{cmdName}' is permanently blocked.");

        // 2. 危险参数模式
        foreach (var pattern in s_dangerousPatterns)
        {
            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return PermissionResult.Denied($"Dangerous pattern detected: '{pattern}'");
        }

        // 3. 路径校验
        if (targetPath is not null)
        {
            var fullPath = Path.GetFullPath(targetPath);
            if (IsForbiddenPath(fullPath))
                return PermissionResult.Denied($"Path '{fullPath}' is in a forbidden zone.");
            if (!fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
                return PermissionResult.RequiresApproval(
                    SecurityLevel.SystemExecution,
                    $"Path '{fullPath}' is outside the project boundary.");
        }

        // 4. 命令级别判定
        if (s_l0Commands.Contains(cmdName))
            return PermissionResult.Allowed(SecurityLevel.ReadOnly);
        if (s_l1Commands.Contains(cmdName))
            return PermissionResult.Allowed(SecurityLevel.ProjectWrite);

        // 5. 未知命令 → 需人工确认
        return PermissionResult.RequiresApproval(
            SecurityLevel.SystemExecution,
            $"Command '{cmdName}' is not in the whitelist.");
    }

    /// <summary>校验文件写入是否允许。</summary>
    public PermissionResult ValidateFileWrite(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        // 禁区
        if (IsForbiddenPath(fullPath))
            return PermissionResult.Denied("Path is in a forbidden zone.");

        // 项目外
        if (!fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
            return PermissionResult.RequiresApproval(
                SecurityLevel.SystemExecution, "Path is outside the project boundary.");

        // 禁止写入的文件类型
        if (s_blockedExtensions.Contains(ext))
            return PermissionResult.RequiresApproval(
                SecurityLevel.SystemExecution,
                $"Writing '{ext}' files requires manual approval.");

        return PermissionResult.Allowed(SecurityLevel.ProjectWrite);
    }

    private static readonly HashSet<string> s_blockedExtensions = [
        ".exe", ".dll", ".so", ".dylib", ".app", ".msi", ".deb", ".rpm",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1",
        ".pem", ".key", ".pfx", ".p12", ".cer", ".crt",
        ".reg", ".sys", ".inf", ".service"
    ];

    private static bool IsForbiddenPath(string fullPath)
    {
        string[] forbidden = OperatingSystem.IsWindows()
            ? [@"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\"]
            : OperatingSystem.IsMacOS()
                ? ["/System/", "/usr/sbin/", "/private/var/", "/Library/LaunchDaemons/"]
                : ["/boot/", "/sbin/", "/usr/sbin/", "/proc/", "/sys/", "/dev/"];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] sensitiveHome = [
            Path.Combine(home, ".ssh"),
            Path.Combine(home, ".gnupg"),
            Path.Combine(home, ".aws"),
            Path.Combine(home, ".azure"),
            Path.Combine(home, ".kube")
        ];

        foreach (var dir in forbidden.Concat(sensitiveHome))
        {
            if (fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ExtractCommandName(string command)
    {
        var trimmed = command.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        var name = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        return Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
    }
}
```

---

## 十、实现路线

### ✅ 已完成

- `ShellTool` / `FileTool` 基础实现（无权限检查）
- 项目根目录检测

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `PermissionGuard` 核心类 | 路径沙盒 + 命令白名单 + 危险模式检测 |
| **P0** | 集成到 `ShellTool` | 在 `ExecuteAsync` 前插入 `ValidateCommand` 拦截 |
| **P0** | 集成到 `FileTool` | 在 write 操作前插入 `ValidateFileWrite` 拦截 |
| **P1** | 子命令分级解析 | 解析 `git push` / `docker run` 等复合命令的实际级别 |
| **P1** | 授权 UI（桌面端） | Avalonia 弹窗：拒绝 / 允许一次 / 始终允许 |
| **P1** | 授权 UI（CLI 端） | Spectre.Console 交互式确认 |
| **P2** | `security.log` 审计 | 所有提权操作和用户决策持久化记录 |
| **P2** | 会话信任扩展区 | 用户"始终允许"的路径在本会话内免再确认 |
| **P3** | 配置文件 `permissions.json` | 项目级白名单/黑名单自定义 |
| **P3** | Swarm 权限隔离 | 不同 Worker 的作用域独立校验（配合 Task 04） |

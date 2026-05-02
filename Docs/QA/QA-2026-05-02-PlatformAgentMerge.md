# QA 审阅报告：PuddingPlatform 管理台合并入 PuddingAgent 单进程

**审阅日期**: 2026-05-02  
**任务卡**: task-20260502-016  
**审阅者**: QA-Sonnet (Claude Sonnet 4.6)  
**开发者**: GPT-5.3-Codex (@dev)  
**结论**: **FAIL** — 存在 P0 阻断问题，需修复后重新审阅

---

## 一、审阅范围

| 文件 | 路径 | 说明 |
|------|------|------|
| Program.cs | Source/PuddingAgent/Program.cs | DI 合并 + 中间件管道 |
| PuddingAgent.csproj | Source/PuddingAgent/PuddingAgent.csproj | 项目引用 + NuGet 包 |
| Dockerfile | Source/PuddingAgent/Dockerfile | 三阶段构建 |
| docker-compose.yml | docker-compose.yml | 单服务编排 |
| appsettings.json | Source/PuddingAgent/appsettings.json | 默认配置 |
| build-and-up.ps1 | build-and-up.ps1 | 编译部署脚本 |

---

## 二、发现清单

### P0（阻断 — 必须修复后重审）

#### P0-1: Admin SPA dist/index.html 覆盖 Chat SPA wwwroot/index.html

**位置**: [Source/PuddingAgent/Dockerfile](Source/PuddingAgent/Dockerfile#L47-L48)

**问题描述**:

Dockerfile 第 3 阶段：
```dockerfile
COPY --from=dotnet-build /app .
COPY --from=frontend-build /admin/dist /app/wwwroot/
```

- Stage 2 发布的 `wwwroot/index.html` 是 **Chat SPA**（纯 HTML+JS 聊天界面）
- Stage 1 编译的 `PuddingPlatformAdmin`（UmiJS/Ant Design Pro）输出 `dist/index.html`（React 管理台 SPA）
- Stage 3 中 `COPY --from=frontend-build /admin/dist /app/wwwroot/` 会用 Admin 的 `index.html` **覆盖** Chat 的 `index.html`

UmiJS 配置（[config/config.ts](Source/PuddingPlatformAdmin/config/config.ts#L19)）已确认：
```ts
const PUBLIC_PATH: string = '/';  // 部署在根路径
```

**影响**:
- 部署后访问 `http://localhost:8080` 将加载 Admin React SPA，而非 Chat SPA
- `/api/chat` POST 端点仍可工作，但前端聊天界面被完全覆盖
- `MapFallbackToFile("index.html")` 仅能服务一个 SPA，两个 SPA 无法共享同一个回退文件

**修复建议**:
1. 将 Admin SPA 部署到 `/admin/` 子路径：
   - 修改 UmiJS `publicPath` 为 `/admin/`
   - Dockerfile 改为 `COPY --from=frontend-build /admin/dist /app/wwwroot/admin/`
   - 可选：添加 `MapFallbackToFile("admin/index.html")` 用于 `/admin/{**rest}` 路由（需 `app.Map("/admin", ...)` 子管道）
2. 或将 Chat SPA 嵌入 Admin SPA 作为一个页面（工作量大，不推荐 V1）
3. 或保留两个进程分别部署（回退到合并前的架构）

**严重性**: P0 — Chat 功能在 Docker 部署后完全不可用

---

### P1（严重 — 合并前应修复）

#### P1-1: JWT 密钥默认值无生产环境保护

**位置**: [Source/PuddingAgent/Program.cs](Source/PuddingAgent/Program.cs#L33-L34) + [docker-compose.yml](docker-compose.yml#L28)

```csharp
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
```

```yaml
Jwt__Key: ${JWT_KEY:-Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!}
```

**问题**: 代码和 docker-compose 均使用硬编码 dev key 作为回退值。虽然注释/变量名中写有 "MUST-CHANGE-IN-PRODUCTION"，但在生产环境中如果运维忘记设置 `JWT_KEY` 环境变量，系统将静默使用弱密钥，无任何警告或启动失败。

**修复建议**: 在 Program.cs 中添加非开发环境的密钥校验：
```csharp
if (!builder.Environment.IsDevelopment() && jwtKey.Contains("MUST-CHANGE"))
{
    throw new InvalidOperationException(
        "JWT Key 未配置或仍为默认值，生产环境必须设置 Jwt:Key");
}
```

**严重性**: P1 — 安全配置缺失不会自检，可能导致生产环境使用弱密钥

---

#### P1-2: Session 中间件顺序不符合 ASP.NET Core 推荐

**位置**: [Source/PuddingAgent/Program.cs](Source/PuddingAgent/Program.cs#L131-L141)

```csharp
app.UseRouting();
app.UseSession();        // ← 在 Authentication 之前
app.UseAuthentication();
app.UseAuthorization();
```

**问题**: Microsoft 推荐的中间件顺序为 `UseRouting → UseCors → UseAuthentication → UseAuthorization → UseSession → Endpoints`。当前 `UseSession` 在 `UseAuthentication` 之前，虽然当前不会导致功能问题（Session 不依赖 Auth），但不符合标准实践，未来如果改用 Cookie 认证可能产生会话状态时序问题。

**修复建议**: 将 `UseSession()` 移到 `UseAuthorization()` 之后、`MapStaticAssets()` 之前。

**严重性**: P1 — 非标准顺序，未来维护风险

---

#### P1-3: Bootstrap Secret 默认为空，无启动校验

**位置**: [docker-compose.yml](docker-compose.yml#L31)

```yaml
Bootstrap__Secret: "${BOOTSTRAP_SECRET:-}"
```

**问题**: 首次启动创建管理员账号时需要的 `Bootstrap__Secret` 默认为空字符串。如果管理员账号已存在则无影响，但如果数据库为空且未设置该变量，启动时行为未定义（取决于 Auth 控制器如何处理空 secret）。

**修复建议**: 在 Auth 控制器中（或 Program.cs 启动检查中）校验：若数据库无管理员用户且 Bootstrap Secret 为空，应输出明确错误而非静默允许空 secret 创建管理员。

**严重性**: P1 — 初始化安全边界脆弱

---

### P2（改进建议 — 非阻断）

#### P2-1: CORS 中间件顺序注释不准确

**位置**: [Source/PuddingAgent/Program.cs](Source/PuddingAgent/Program.cs#L127)

```csharp
// ── CORS（必须在 Routing 前）────────────────────────
app.UseCors("AdminSpa");
```

**问题**: 注释说"必须在 Routing 前"，但 Microsoft 官方文档推荐 `UseCors` 在 `UseRouting` **之后**（以实现端点感知 CORS）。当前放在 Routing 前虽可工作（全局 CORS），但注释内容与官方推荐相反。

**修复建议**: 将注释修正为 "CORS（全局策略，在 Routing 前全局生效）" 或移动到 Routing 之后。

**严重性**: P2 — 注释误导，不影响功能

---

#### P2-2: `MapControllers()` 与 `MapControllerRoute()` 冗余

**位置**: [Source/PuddingAgent/Program.cs](Source/PuddingAgent/Program.cs#L147-L153)

```csharp
app.MapControllers();           // 属性路由
app.MapControllerRoute(...);    // 约定路由
```

**问题**: 当前 PuddingPlatform 控制器均为约定路由（无 `[Route]` 属性），`MapControllers()` 不会匹配任何控制器。调用它虽然无害，但属于冗余代码。

**修复建议**: 保留 `MapControllerRoute`，移除 `MapControllers()`；除非未来计划添加属性路由的 API 控制器。

**严重性**: P2 — 冗余但不影响功能

---

#### P2-3: 缺少 `appsettings.Development.json`

**位置**: Source/PuddingAgent/（文件不存在）

**问题**: Program.cs 中有 `app.Environment.IsDevelopment()` 判断（如 ExceptionHandler），但项目缺少 `appsettings.Development.json`。本地开发时的配置（如详细日志、CORS 宽松策略）应放在该文件中。

**严重性**: P2 — 开发者体验改进

---

#### P2-4: Dockerfile 中 `PuddingGateway.csproj` 可能冗余

**位置**: [Source/PuddingAgent/Dockerfile](Source/PuddingAgent/Dockerfile#L20)

```dockerfile
COPY Source/PuddingGateway/PuddingGateway.csproj Source/PuddingGateway/
```

**问题**: `PuddingAgent.csproj` 不直接引用 `PuddingGateway`（仅通过 `PuddingController` 传递依赖）。在 Dockerfile 中单独 COPY Gateway.csproj 不会造成 restore 失败，但属于不必要的文件复制。`dotnet restore` 会自动处理传递依赖。

**修复建议**: 可移除该行，`dotnet restore` 会通过 `PuddingController → PuddingGateway` 的传递引用自动解析。

**严重性**: P2 — 轻微冗余，不影响构建

---

## 三、验证通过项

以下检查项**通过审核**，未发现问题：

| 检查项 | 状态 | 说明 |
|--------|------|------|
| DI 注册完整性 | ✅ PASS | CORS → Controllers → JWT → PlatformApiClient → Workspace → Minio → EF Core → Session → Runtime(9) → LLM，覆盖全面 |
| EF Core 迁移启动 | ✅ PASS | 仅 `MigrateAsync()`，无硬编码 `EnsureCreated()` |
| JWT TokenValidationParameters | ✅ PASS | ValidateIssuer/Audience/Lifetime/IssuerSigningKey 全部启用 |
| Chat endpoint 在 Fallback 前 | ✅ PASS | `/api/chat` 在 `MapFallbackToFile` 之前注册 |
| 健康检查端点 | ✅ PASS | `/health` 返回标准健康状态 JSON |
| .csproj 项目引用 | ✅ PASS | 正确引用 PuddingCore/Controller/Runtime/MemoryEngine/Platform |
| NuGet 包完整性 | ✅ PASS | JWT Bearer / EF Core Sqlite / EF Core SqlServer / Npgsql / Minio 均包含 |
| Dockerfile 三阶段结构 | ✅ PASS | Node.js → dotnet SDK → aspnet runtime，结构合理 |
| docker-compose 单服务 | ✅ PASS | 已移除 pudding-platform 独立服务 |
| docker-compose 环境变量 | ✅ PASS | JWT/Bootstrap/DB/P2P 配置完整 |
| docker-compose volume | ✅ PASS | agent_data 挂载到 /app/data，SQLite 持久化正确 |
| appsettings.json 配置节 | ✅ PASS | JWT、ConnectionStrings、Bootstrap、Pudding 节完整 |
| build-and-up.ps1 流程 | ✅ PASS | 前端 npm ci/build + 后端 dotnet build + Docker compose，流程正确 |
| CancellationToken 传递 | ✅ PASS | Chat endpoint 正确传递 ct |
| 依赖方向 | ✅ PASS | UI → Controller → Runtime → Core，无逆向引用 |
| 端口配置 | ✅ PASS | 统一 8080，P2P 9527/udp |

---

## 四、审阅统计

| 严重度 | 数量 |
|--------|------|
| P0 (阻断) | 1 |
| P1 (严重) | 3 |
| P2 (改进) | 4 |
| **合计** | **8** |

---

## 五、结论

**判定: FAIL**

必须修复 P0-1（Admin SPA 覆盖 Chat SPA）后重新提交审阅。P1 问题建议在合并前修复，P2 问题可在后续迭代中改进。

**重审要求**: 修复 P0-1 后，确认 Docker 部署下 `http://localhost:8080` 同时可访问 Chat 界面和 Admin 管理台（通过不同的 URL 路径或导航）。

---

*审阅者: QA-Sonnet (Claude Sonnet 4.6)*  
*审阅完成时间: 2026-05-02*

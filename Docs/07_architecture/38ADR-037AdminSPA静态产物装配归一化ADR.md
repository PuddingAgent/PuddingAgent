# ADR-037：Admin SPA 静态产物装配归一化

> 状态：**Proposed**  
> 日期：2026-05-23  
> 范围：PuddingAgent、PuddingPlatformAdmin、Docker 构建、本地 `dotnet run`、静态文件发布链路  
> 关联：[33ADR-032构建门禁与运行态漂移修复方案](33ADR-032构建门禁与运行态漂移修复方案.md)、[06PuddingAgent与客户端](06PuddingAgent与客户端.md)

---

## 1. 背景

Admin 前端 `PuddingPlatformAdmin` 使用 Umi/Max 构建，运行路径固定为：

```text
base: /admin/
publicPath: /admin/
```

后端 `PuddingAgent` 使用 ASP.NET Core 静态文件能力承载 Admin SPA：

```csharp
app.UseStaticFiles();
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
app.MapFallbackToFile("index.html");
```

当前 Docker 镜像构建会额外执行：

```dockerfile
COPY Source/PuddingPlatformAdmin/dist /app/wwwroot/admin/
```

但本地 `build-and-up.ps1 -BuildOnly` 和直接 `dotnet run --project Source/PuddingAgent` 链路只生成根路径重定向 `wwwroot/index.html`，没有把 `PuddingPlatformAdmin/dist` 放入 `PuddingAgent/wwwroot/admin` 或 build output 的 `wwwroot/admin`。因此本地访问：

```text
http://localhost:8080/admin/
```

会出现 404 或白屏。

这个问题不是单纯“脚本少复制一步”，而是静态资源装配职责分散：

| 链路 | 当前装配点 | 问题 |
|------|------------|------|
| Docker | `Dockerfile COPY Source/PuddingPlatformAdmin/dist /app/wwwroot/admin/` | Docker 独有逻辑 |
| 本地 `dotnet run` | `Source/PuddingAgent/wwwroot` | 未装配 Admin SPA |
| `dotnet publish` | `PuddingAgent.csproj` content items | 未声明 Admin SPA 为 Web 项目内容 |
| 脚本 | `build-and-up.ps1` 手工清理并写根 `index.html` | 只处理根跳转，不处理 `/admin` |

如果继续在多个地方手工复制，会让 Docker、本地调试、publish 产物长期漂移。

---

## 2. 决策

### ADR-037-A：Admin SPA 产物装配权归属 `PuddingAgent.csproj`

**决定**：`PuddingAgent` Web 项目是最终承载 Admin SPA 的服务端应用，因此由 `Source/PuddingAgent/PuddingAgent.csproj` 统一声明 Admin SPA 静态资源。

`PuddingPlatformAdmin` 只负责产生前端构建产物：

```text
Source/PuddingPlatformAdmin/dist/**
```

`PuddingAgent.csproj` 负责把该产物映射为 Web 静态内容：

```text
wwwroot/admin/**
```

建议 MSBuild 规则：

```xml
<ItemGroup>
  <Content Include="..\PuddingPlatformAdmin\dist\**\*">
    <Link>wwwroot\admin\%(RecursiveDir)%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

### ADR-037-B：`dist` 根目录整体映射到 `/admin/`

**决定**：复制源必须是：

```text
Source/PuddingPlatformAdmin/dist/**
```

目标必须是：

```text
wwwroot/admin/**
```

不得只复制：

```text
Source/PuddingPlatformAdmin/dist/admin/**
```

原因：Umi `exportStatic` 会在 `dist/admin/index.html` 生成路由静态页，但真正的 `umi.*.js`、`umi.*.css`、`assets/`、`scripts/` 等资源位于 `dist` 根目录。只复制 `dist/admin` 会继续导致资源 404。

### ADR-037-C：Docker 不再维护第二套 Admin SPA 复制规则

**决定**：删除 `Source/PuddingAgent/Dockerfile` 中的 Admin SPA 复制语句：

```dockerfile
COPY Source/PuddingPlatformAdmin/dist /app/wwwroot/admin/
```

Docker 运行镜像只复制 `dotnet publish` 结果：

```dockerfile
COPY --from=dotnet-build /app .
```

Admin SPA 是否进入镜像，完全由 `PuddingAgent.csproj` 的 publish content 规则决定。

### ADR-037-D：构建脚本只负责编排，不负责静态资源装配

**决定**：`build-and-up.ps1` 保留编排顺序：

1. 准备配置文件；
2. 执行 `pnpm install` / `pnpm run build`；
3. 执行 `dotnet build Source/PuddingAgent/PuddingAgent.csproj -c Release`；
4. 按参数执行 Docker build / up。

脚本不得再维护 Admin SPA 到 `wwwroot/admin` 的复制逻辑。

根路径重定向 `wwwroot/index.html` 仍可由脚本生成，或后续迁入 `PuddingAgent` 项目中的固定静态文件；这不影响本 ADR 的 `/admin` 产物装配决策。

### ADR-037-E：不把前端 `outputPath` 指向后端 `wwwroot`

**决定**：不采用以下方案：

```text
PuddingPlatformAdmin outputPath -> ../PuddingAgent/wwwroot/admin
```

原因：

1. 前端构建会直接写入后端源码目录，容易污染 git 状态；
2. 前端项目失去独立 `dist` 产物边界；
3. `dotnet publish` 的输入来源不再清晰；
4. 多平台构建和 CI 缓存更难定位产物责任。

---

## 3. 影响范围

| 文件 | 变更 |
|------|------|
| `Source/PuddingAgent/PuddingAgent.csproj` | 新增 Admin SPA `dist/**/*` 到 `wwwroot/admin/**/*` 的 Content 映射 |
| `Source/PuddingAgent/Dockerfile` | 删除 `COPY Source/PuddingPlatformAdmin/dist /app/wwwroot/admin/` |
| `build-and-up.ps1` | 保持先前端 build、后后端 build；移除或避免新增 Admin SPA 手工复制 |
| `Source/PuddingAgent/Program.cs` | 保持 `UseStaticFiles` 与 `/admin` fallback，不需要改路由 |
| `Source/PuddingPlatformAdmin/config/config.ts` | 保持 `base` / `publicPath` 为 `/admin/` |

---

## 4. 验收标准

### 4.1 本地 build output

执行：

```powershell
cd Source\PuddingPlatformAdmin
pnpm run build

cd ..\..
dotnet build Source\PuddingAgent\PuddingAgent.csproj -c Release --nologo
```

必须满足：

```powershell
Test-Path Source\PuddingAgent\bin\Release\net10.0\wwwroot\admin\index.html
Get-ChildItem Source\PuddingAgent\bin\Release\net10.0\wwwroot\admin\umi.*.js
Get-ChildItem Source\PuddingAgent\bin\Release\net10.0\wwwroot\admin\umi.*.css
```

### 4.2 本地运行

执行：

```powershell
dotnet run --project Source\PuddingAgent\PuddingAgent.csproj -c Release
```

必须满足：

```powershell
curl -I http://localhost:8080/admin/
curl -I http://localhost:8080/admin/umi.8987659e.js
```

`/admin/` 返回 `200`，Admin JS/CSS 资源返回 `200`。

具体 hash 文件名以当次 `pnpm run build` 产物为准。

### 4.3 Docker 镜像

执行：

```powershell
.\build-and-up.ps1
docker compose exec pudding-agent sh -c "test -f /app/wwwroot/admin/index.html"
docker compose exec pudding-agent sh -c "ls /app/wwwroot/admin/umi.*.js"
```

必须满足：

```powershell
curl -I http://localhost:8080/admin/
```

返回 `200`，浏览器访问 Admin 不白屏。

---

## 5. 后果

正向后果：

1. Docker、本地 `dotnet run`、`dotnet publish` 使用同一套静态资源装配规则；
2. Admin SPA 是否进入发布产物可以通过 `PuddingAgent.csproj` 直接审计；
3. Dockerfile 不再承担跨项目静态资源复制职责；
4. 后续 CI 只需验证 `dotnet publish` 产物即可覆盖镜像静态资源完整性。

代价：

1. `PuddingAgent` build/publish 前必须已经存在 `PuddingPlatformAdmin/dist`；
2. 后端开发若只想编译 API，不关心 Admin 静态页，需要明确是否允许跳过 Admin SPA 产物校验；
3. `dist` 是生成产物，不应提交到 git，但构建环境必须先运行前端 build。

---

## 6. 后续决策点

是否在 `PuddingAgent.csproj` 中加入 fail-fast 校验：

```xml
<Target Name="ValidateAdminSpaDist" BeforeTargets="Build;Publish">
  <Error Condition="!Exists('..\PuddingPlatformAdmin\dist\index.html')" Text="Admin SPA dist is missing. Run pnpm run build in Source/PuddingPlatformAdmin first." />
</Target>
```

建议实现阶段按环境区分：

| 场景 | 建议 |
|------|------|
| CI / release / Docker | 必须 fail fast |
| 日常后端单元测试 | 可通过 `SkipAdminSpaDistValidation=true` 跳过 |
| `build-and-up.ps1` | 不跳过，缺失即失败 |

是否加入该 Target 由实施方案落地时结合现有开发体验决定。

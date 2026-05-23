# ── Pudding Agent Dockerfile ──────────────────────────────
# ADR-037/040: 双阶段构建
#   Stage 1: dotnet publish（BuildKit cache mount 加速 NuGet 还原）
#   Stage 2: aspnet runtime 镜像
# Admin SPA 由 PuddingAgent.csproj 的 Content 项装配至 wwwroot/admin/，
# Dockerfile 不再直接 COPY 前端产物（csproj 是统一装配点）。

# ── Stage 1: dotnet publish ──────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# 复制项目文件并还原依赖
COPY Source/PuddingCore/PuddingCore.csproj           Source/PuddingCore/
COPY Source/PuddingMemoryEngine/PuddingMemoryEngine.csproj Source/PuddingMemoryEngine/
COPY Source/PuddingRuntime/PuddingRuntime.csproj     Source/PuddingRuntime/
COPY Source/PuddingAgent/PuddingAgent.csproj         Source/PuddingAgent/
COPY Source/PuddingPlatform/PuddingPlatform.csproj   Source/PuddingPlatform/
COPY Source/PuddingController/PuddingController.csproj Source/PuddingController/
COPY Source/PuddingGateway/PuddingGateway.csproj     Source/PuddingGateway/
COPY external/github.hyfree.GM/github.hyfree.GM/github.hyfree.GM.csproj external/github.hyfree.GM/github.hyfree.GM/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore Source/PuddingAgent/PuddingAgent.csproj

# 复制全部源码
COPY Source/ Source/
COPY external/ external/

# 发布
# ADR-040: 使用 BuildKit cache mount 加速 restore
# compose 未传入 CACHE_BUST 时不会主动破坏缓存
ARG CACHE_BUST=0
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Source/PuddingAgent/PuddingAgent.csproj \
    -c Release -o /app \
    --no-restore \
    --nologo

# ── Stage 2: 运行阶段 ────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# ADR-037: Admin SPA 由 csproj Content 项装配至 wwwroot/admin/
# 不再通过 Dockerfile 直接 COPY，避免与 csproj 双重复制
COPY --from=dotnet-build /app .
# 默认 data 模板。运行时只复制缺失文件到 /app/data，不覆盖用户配置。
COPY Source/PuddingAgent/default-data/ /app/default-data/
ENV ASPNETCORE_HTTP_PORTS=8080
ENV PUDDING_DATA_ROOT=/app/data

EXPOSE 8080

ENTRYPOINT ["dotnet", "PuddingAgent.dll"]

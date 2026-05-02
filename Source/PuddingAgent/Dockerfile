# ── Pudding Agent Dockerfile ──────────────────────────────
# 双阶段构建（前端由宿主机 pnpm build 预编译，Docker 直接 COPY）：
#   Stage 1: dotnet publish（全部项目引用 + 发布）
#   Stage 2: aspnet runtime 镜像 + 前端静态文件

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

RUN dotnet restore Source/PuddingAgent/PuddingAgent.csproj

# 复制全部源码
COPY Source/ Source/

# 发布
RUN dotnet publish Source/PuddingAgent/PuddingAgent.csproj \
    -c Release -o /app \
    --nologo

# ── Stage 2: 运行阶段 ────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=dotnet-build /app .
# Admin SPA 由宿主机预编译（pnpm build），直接 COPY 产物
COPY Source/PuddingPlatformAdmin/dist /app/wwwroot/admin/

ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "PuddingAgent.dll"]

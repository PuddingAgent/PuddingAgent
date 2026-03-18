# ── 构建阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 仅拷贝 csproj，利用层缓存恢复依赖
COPY Source/PuddingCore/PuddingCore.csproj               Source/PuddingCore/
COPY Source/PuddingAgent/PuddingAgent.csproj             Source/PuddingAgent/
COPY Source/PuddingMemoryEngine/PuddingMemoryEngine.csproj Source/PuddingMemoryEngine/
COPY Source/PuddingRuntime/PuddingRuntime.csproj         Source/PuddingRuntime/
RUN dotnet restore Source/PuddingRuntime/PuddingRuntime.csproj

# 拷贝源码并发布
COPY Source/PuddingCore/         Source/PuddingCore/
COPY Source/PuddingAgent/        Source/PuddingAgent/
COPY Source/PuddingMemoryEngine/ Source/PuddingMemoryEngine/
COPY Source/PuddingRuntime/      Source/PuddingRuntime/
RUN dotnet publish Source/PuddingRuntime/PuddingRuntime.csproj \
    -c Release -o /app/publish --no-restore

# ── 运行阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingRuntime.dll"]

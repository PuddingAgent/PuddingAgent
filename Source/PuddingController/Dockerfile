# ── 构建阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 仅拷贝 csproj，利用层缓存恢复依赖
COPY Source/PuddingCore/PuddingCore.csproj               Source/PuddingCore/
COPY Source/PuddingAgent/PuddingAgent.csproj             Source/PuddingAgent/
COPY Source/PuddingGateway/PuddingGateway.csproj         Source/PuddingGateway/
COPY Source/PuddingController/PuddingController.csproj   Source/PuddingController/
RUN dotnet restore Source/PuddingController/PuddingController.csproj

# 拷贝源码并发布
COPY Source/PuddingCore/       Source/PuddingCore/
COPY Source/PuddingAgent/      Source/PuddingAgent/
COPY Source/PuddingGateway/    Source/PuddingGateway/
COPY Source/PuddingController/ Source/PuddingController/
RUN dotnet publish Source/PuddingController/PuddingController.csproj \
    -c Release -o /app/publish --no-restore

# ── 运行阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingController.dll"]

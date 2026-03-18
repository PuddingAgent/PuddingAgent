# ── 构建阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 仅拷贝 csproj，利用层缓存恢复依赖
COPY Source/PuddingCore/PuddingCore.csproj               Source/PuddingCore/
COPY Source/PuddingPlatform/PuddingPlatform.csproj       Source/PuddingPlatform/
RUN dotnet restore Source/PuddingPlatform/PuddingPlatform.csproj

# 拷贝源码并发布
COPY Source/PuddingCore/   Source/PuddingCore/
COPY Source/PuddingPlatform/ Source/PuddingPlatform/
RUN dotnet publish Source/PuddingPlatform/PuddingPlatform.csproj \
    -c Release -o /app/publish --no-restore

# ── 运行阶段 ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# 安装 Npgsql GSSAPI 依赖（避免 libgssapi_krb5.so.2 警告）
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingPlatform.dll"]

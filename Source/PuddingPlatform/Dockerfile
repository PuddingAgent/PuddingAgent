# ── 运行阶段 ──────────────────────────────────────────────────
# 产物由宿主机 dotnet publish 生成（build-and-up.ps1 步骤 2），
# 此处直接 COPY publish/ 目录，无需在容器内重复编译。
FROM m.daocloud.io/mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# 安装 Npgsql GSSAPI 依赖（避免 libgssapi_krb5.so.2 警告）
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY Source/PuddingPlatform/publish/ .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingPlatform.dll"]

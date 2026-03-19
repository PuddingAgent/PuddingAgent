# ── 运行阶段 ──────────────────────────────────────────────────
# 产物由宿主机 dotnet publish 生成（build-and-up.ps1 步骤 2），
# 此处直接 COPY publish/ 目录，无需在容器内重复编译。
FROM m.daocloud.io/mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# 安装 Docker CLI，使 Runtime 可通过挂载的 /var/run/docker.sock 管理 Agent 沙箱容器
RUN apt-get update && apt-get install -y --no-install-recommends \
    docker.io \
    && rm -rf /var/lib/apt/lists/*
# 安装 Docker CLI，使 Runtime 可通过挂载的 /var/run/docker.sock 管理 Agent 沙箱容器
RUN apt-get update && apt-get install -y --no-install-recommends \
    docker.io \
    && rm -rf /var/lib/apt/lists/*
COPY Source/PuddingRuntime/publish/ .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingRuntime.dll"]

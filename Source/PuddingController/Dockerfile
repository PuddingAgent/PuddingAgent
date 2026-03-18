# ── 运行阶段 ──────────────────────────────────────────────────
# 产物由宿主机 dotnet publish 生成（build-and-up.ps1 步骤 2），
# 此处直接 COPY publish/ 目录，无需在容器内重复编译。
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY Source/PuddingController/publish/ .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingController.dll"]

# 本地预编译版 Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY Source/PuddingRuntime/publish/ .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuddingRuntime.dll"]

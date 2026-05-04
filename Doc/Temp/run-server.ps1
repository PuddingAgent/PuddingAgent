$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:9528"
$env:JWT_KEY = "Pudding-UI-Test-JWT-DEV-Key-32charsMIN!!"
$env:Jwt__Key = "Pudding-UI-Test-JWT-DEV-Key-32charsMIN!!"
$env:LLM_ENDPOINT = "https://api.openai.com"
$env:LLM_MODEL = "gpt-4o-mini"

Set-Location D:\WangXianQiang\github\hyfree\PuddingCode
dotnet run --project Source/PuddingAgent/PuddingAgent.csproj --no-launch-profile

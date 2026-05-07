$env:TODO_API_PARTICIPANT_ID="msi-pudding"
$env:TODO_API_PARTICIPANT_NAME="msi-pudding"
$env:TODO_API_PARTICIPANT_KEY="msi-pudding-agent-key-2026"
$py = "C:\Users\huany\AppData\Local\Programs\Python\Python312\python.exe"

$json = & $py .github/skills/todo-api/todo_api.py list-tasks --project Pudding --status pending --status progress --status auditing 2>&1

# Parse and summarize
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$data = $ser.DeserializeObject([string]::Join("`n", $json))
$tasks = $data["tasks"]

Write-Host "`n=== Pudding 待完成任务卡 ($($tasks.Count) 张) ===" -ForegroundColor Cyan
Write-Host ""

foreach ($t in $tasks) {
    $id = $t["id"]
    $title = $t["title"]
    $pri = $t["priority"]
    $stage = $t["stage"]
    $status = $t["status"]
    $owner = $t["owner"]
    $exec = $t["executor_type"]
    Write-Host "[$pri] [stage=$stage] [status=$status] $id" -ForegroundColor Yellow
    Write-Host "  $title"
    Write-Host "  owner=$owner  executor=$exec"
    Write-Host ""
}

# dev.ps1 — hot reload dev server on http://localhost:5290
# Edit FBXService.cs → service restarts automatically.

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "true"

Set-Location $PSScriptRoot

Write-Host ""
Write-Host "  FBXService  [hot reload]" -ForegroundColor Cyan
Write-Host "  http://localhost:25290" -ForegroundColor Green
Write-Host "  Edit FBXService.cs to auto-restart  |  Ctrl+C to stop" -ForegroundColor Gray
Write-Host ""

dotnet watch run

# run.ps1 — build to bin\ then launch bin\FBXService.exe on http://localhost:5290
# Use this for a stable server (no auto-restart) when other tools depend on it.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "  FBXService  [stable]" -ForegroundColor Cyan

# Build to bin\ (flat — no Debug/Release/net8.0 subfolders)
Write-Host "  Building..." -ForegroundColor Gray
dotnet build -c Release -o bin --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "  Build FAILED" -ForegroundColor Red; exit 1 }

Write-Host "  http://localhost:25290" -ForegroundColor Green
Write-Host "  Ctrl+C to stop" -ForegroundColor Gray
Write-Host ""

$env:ASPNETCORE_ENVIRONMENT = "Production"

.\bin\FBXService.exe

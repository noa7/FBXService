# build.ps1 — compile to bin\ (flat output, no nested subfolders)
# Useful to verify the code compiles before deploying.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host ""
dotnet build -c Release -o bin --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "  Build OK  →  bin\FBXService.exe" -ForegroundColor Green
} else {
    Write-Host "  Build FAILED" -ForegroundColor Red
    exit 1
}
Write-Host ""

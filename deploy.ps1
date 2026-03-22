# deploy.ps1 — Build container with Cloud Build and deploy to Cloud Run.
#
# Usage:
#   .\deploy.ps1                              # uses gcloud default project
#   .\deploy.ps1 -Project my-project-id
#   .\deploy.ps1 -Project my-project-id -Region us-central1
#   .\deploy.ps1 -Project my-project-id -Tag v2-mesh-fix   (labels the image)

param(
    [string]$Project = "",
    [string]$Region  = "europe-west1",
    [string]$Tag     = "latest",
    [string]$Service = "fbx-service",
    [switch]$SkipTest   # skip post-deploy smoke test
)

$ErrorActionPreference = "Stop"

# ── resolve project ───────────────────────────────────────────────────────────
if (-not $Project) {
    $Project = (gcloud config get-value project 2>$null).Trim()
    if (-not $Project) {
        Write-Host "ERROR: No GCP project set. Run 'gcloud config set project YOUR_ID' or pass -Project." -ForegroundColor Red
        exit 1
    }
}

$Image = "gcr.io/$Project/$Service`:$Tag"

Write-Host ""
Write-Host "  FbxService Deploy" -ForegroundColor Cyan
Write-Host "  Project : $Project" -ForegroundColor White
Write-Host "  Region  : $Region" -ForegroundColor White
Write-Host "  Service : $Service" -ForegroundColor White
Write-Host "  Image   : $Image" -ForegroundColor White
Write-Host ""

# ── build & push via Cloud Build (no local Docker needed) ─────────────────────
Write-Host "── Cloud Build ──────────────────────────────────────" -ForegroundColor Cyan
$buildArgs = @(
    "builds", "submit", $PSScriptRoot,
    "--project", $Project,
    "--tag", $Image,
    "--timeout", "10m"
)
& gcloud @buildArgs
if ($LASTEXITCODE -ne 0) { Write-Host "Cloud Build FAILED" -ForegroundColor Red; exit 1 }

# ── deploy to Cloud Run ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Cloud Run Deploy ─────────────────────────────────" -ForegroundColor Cyan
$deployArgs = @(
    "run", "deploy", $Service,
    "--project",        $Project,
    "--region",         $Region,
    "--image",          $Image,
    "--platform",       "managed",
    "--allow-unauthenticated",
    "--memory",         "512Mi",
    "--cpu",            "1",
    "--concurrency",    "80",
    "--timeout",        "60s",
    "--min-instances",  "0",
    "--max-instances",  "10",
    "--port",           "8080"
)
& gcloud @deployArgs
if ($LASTEXITCODE -ne 0) { Write-Host "Cloud Run deploy FAILED" -ForegroundColor Red; exit 1 }

# ── get service URL ───────────────────────────────────────────────────────────
$ServiceUrl = (gcloud run services describe $Service `
    --project $Project `
    --region $Region `
    --format "value(status.url)" 2>$null).Trim()

Write-Host ""
Write-Host "  ✅ Deployed successfully!" -ForegroundColor Green
Write-Host "  URL: $ServiceUrl" -ForegroundColor White

# ── post-deploy smoke test ────────────────────────────────────────────────────
if (-not $SkipTest) {
    Write-Host ""
    Write-Host "── Smoke test ───────────────────────────────────────" -ForegroundColor Cyan
    Start-Sleep -Seconds 2  # give Cloud Run a moment to spin up
    try {
        $health = Invoke-RestMethod -Uri "$ServiceUrl/health" -Method Get -TimeoutSec 10
        Write-Host "  [PASS] /health → $($health | ConvertTo-Json -Compress)" -ForegroundColor Green
    }
    catch {
        Write-Host "  [WARN] /health check failed — service may still be starting." -ForegroundColor Yellow
        Write-Host "  Try: curl $ServiceUrl/health" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "  To test against live service:" -ForegroundColor Gray
Write-Host "  .\tests\test.ps1 your_model.fbx -Host $ServiceUrl" -ForegroundColor White
Write-Host ""

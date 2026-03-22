# setup.ps1 — One-shot GitHub + Cloud Build setup for FBXService
#
# Run once from C:\tools\FBXService
# After this, every git push to main triggers an auto-deploy to Cloud Run.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$GithubUser = "noa7"
$RepoName   = "FBXService"
$Project    = "ioloc-491009"
$Region     = "europe-west1"
$Service    = "fbx-service"

function Step($msg) { Write-Host "`n── $msg" -ForegroundColor Cyan }
function OK($msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "  ✗ $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "  FBXService Setup" -ForegroundColor Cyan
Write-Host "  GitHub  : $GithubUser/$RepoName"
Write-Host "  GCP     : $Project  ($Region)"
Write-Host ""

# =============================================================================
# STEP 1 — GitHub repo
# =============================================================================
Step "Creating GitHub repo $GithubUser/$RepoName"

# Check if repo already exists
$existing = gh repo view "$GithubUser/$RepoName" 2>$null
if ($existing) {
    OK "Repo already exists — skipping create"
} else {
    gh repo create "$GithubUser/$RepoName" --public --description "FBX inspector HTTP service" --confirm
    OK "Repo created"
}

# =============================================================================
# STEP 2 — Git init + push
# =============================================================================
Step "Pushing code to GitHub"

if (-not (Test-Path ".git")) {
    git init
    git branch -M main
    OK "Git repo initialised"
} else {
    OK "Git repo already exists"
}

# Set remote (overwrite if exists)
git remote remove origin 2>$null
git remote add origin "https://github.com/$GithubUser/$RepoName.git"

git add -A
git commit -m "Initial commit" 2>$null || OK "Nothing new to commit"
git push -u origin main --force

OK "Code pushed to https://github.com/$GithubUser/$RepoName"

# =============================================================================
# STEP 3 — Enable required GCP APIs
# =============================================================================
Step "Enabling GCP APIs (Cloud Build, Cloud Run, Container Registry)"

gcloud services enable `
    cloudbuild.googleapis.com `
    run.googleapis.com `
    containerregistry.googleapis.com `
    --project $Project --quiet

OK "APIs enabled"

# =============================================================================
# STEP 4 — Grant Cloud Build permission to deploy Cloud Run
# =============================================================================
Step "Granting Cloud Build → Cloud Run deploy permission"

# Get the Cloud Build service account (format: PROJECT_NUMBER@cloudbuild.gserviceaccount.com)
$ProjectNumber = (gcloud projects describe $Project --format="value(projectNumber)").Trim()
$CbSa = "$ProjectNumber@cloudbuild.gserviceaccount.com"

gcloud projects add-iam-policy-binding $Project `
    --member="serviceAccount:$CbSa" `
    --role="roles/run.admin" `
    --quiet | Out-Null

gcloud projects add-iam-policy-binding $Project `
    --member="serviceAccount:$CbSa" `
    --role="roles/iam.serviceAccountUser" `
    --quiet | Out-Null

OK "Cloud Build SA ($CbSa) can deploy Cloud Run"

# =============================================================================
# STEP 5 — Connect Cloud Build to GitHub + create trigger
# =============================================================================
Step "Creating Cloud Build trigger (push to main → deploy)"

# Check if trigger already exists
$existingTrigger = gcloud builds triggers list `
    --project $Project --region global `
    --format "value(name)" `
    --filter "name=$RepoName-main" 2>$null

if ($existingTrigger) {
    OK "Trigger already exists — skipping"
} else {
    # Cloud Build GitHub connection must exist. Create trigger via gcloud.
    # This uses the GitHub App connection (user authorises once in browser below).
    gcloud builds triggers create github `
        --project=$Project `
        --region=global `
        --repo-owner=$GithubUser `
        --repo-name=$RepoName `
        --branch-pattern="^main$" `
        --build-config="cloudbuild.yaml" `
        --name="$RepoName-main" `
        --description="Push to main → build + deploy fbx-service"

    OK "Trigger created: push to main auto-deploys"
}

# =============================================================================
# STEP 6 — First build (kick off immediately)
# =============================================================================
Step "Triggering first build now"

gcloud builds triggers run "$RepoName-main" `
    --project=$Project `
    --region=global `
    --branch=main

OK "Build submitted — watch progress:"
Write-Host "  https://console.cloud.google.com/cloud-build/builds?project=$Project" -ForegroundColor White

# =============================================================================
# Done
# =============================================================================
Write-Host ""
Write-Host "  ✅ All done!" -ForegroundColor Green
Write-Host ""
Write-Host "  Repo     : https://github.com/$GithubUser/$RepoName" -ForegroundColor White
Write-Host "  Builds   : https://console.cloud.google.com/cloud-build/builds?project=$Project" -ForegroundColor White
Write-Host "  Cloud Run: https://console.cloud.google.com/run/detail/$Region/$Service/metrics?project=$Project" -ForegroundColor White
Write-Host ""
Write-Host "  Future deploys: just  git push  and Cloud Build does the rest." -ForegroundColor Gray
Write-Host ""

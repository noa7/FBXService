# test.ps1 — Run all FbxService commands against a real .fbx file and report results.
#
# Usage:
#   .\test.ps1 model.fbx
#   .\test.ps1 model.fbx -Host http://localhost:5290
#   .\test.ps1 model.fbx -Host https://fbx-service-xxxx.run.app
#   .\test.ps1 model.fbx -Command meshes          (single command only)
#   .\test.ps1 model.fbx -Verbose                  (add ?verbose to errors)
#   .\test.ps1 model.fbx -Raw                      (add ?raw to mesh calls)

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$FbxPath,

    [string]$Host = "http://localhost:25290",

    [string]$Command = "",   # if set, run only this command

    [switch]$Verbose,
    [switch]$Raw,
    [switch]$Json            # dump full JSON instead of summary
)

$ErrorActionPreference = "Stop"

# ── helpers ───────────────────────────────────────────────────────────────────

function Write-Pass($msg) { Write-Host "  [PASS] $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Head($msg) { Write-Host "`n  ── $msg" -ForegroundColor Cyan }

function Invoke-FbxCommand {
    param([string]$Cmd, [string]$Target = "", [switch]$Raw, [switch]$Verbose)

    $url = "$Host/inspect"
    if ($Cmd)    { $url += "/$Cmd" }
    if ($Target) { $url += "/$Target" }

    $flags = @()
    if ($Raw)     { $flags += "raw" }
    if ($Verbose) { $flags += "verbose" }
    if ($flags)   { $url += "?" + ($flags -join "&") }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod `
            -Uri $url `
            -Method Post `
            -Form @{ file = Get-Item $FbxPath } `
            -ContentType "multipart/form-data"
        $sw.Stop()
        return [pscustomobject]@{
            Ok       = $true
            Data     = $response
            Ms       = $sw.ElapsedMilliseconds
            Url      = $url
        }
    }
    catch {
        $sw.Stop()
        return [pscustomobject]@{
            Ok       = $false
            Error    = $_.Exception.Message
            Ms       = $sw.ElapsedMilliseconds
            Url      = $url
        }
    }
}

function Show-JsonSummary($data, [string]$label) {
    if ($Json) {
        Write-Host ($data | ConvertTo-Json -Depth 20) -ForegroundColor White
        return
    }
    # Print a few key fields depending on what came back
    if ($null -ne $data.success -and $data.success -eq $false) {
        Write-Fail "$label → $($data.error) [$($data.errorType)]"
        if ($data.hint)       { Write-Info "  hint: $($data.hint)" }
        if ($data.stackTrace) { Write-Info "  stack: $($data.stackTrace | Select-Object -First 3)" }
        return
    }
    switch ($label) {
        "list" {
            $data.categories | ForEach-Object {
                $cnt = if ($_.count -ne $null) { " ($($_.count))" } else { "" }
                Write-Info "  $($_.name)$cnt — $($_.description)"
            }
        }
        "settings" {
            Write-Info "  $($data.coordinateSystem)"
            Write-Info "  frameRate=$($data.frameRate)  unitScale=$($data.unitScaleFactor)"
        }
        "nodes" {
            $data.nodes | Select-Object -First 8 | ForEach-Object {
                Write-Info "  [$($_.subclass)] $($_.name)  T=$($_.translation)  R=$($_.rotation)"
            }
            if ($data.nodes.Count -gt 8) { Write-Info "  ... and $($data.nodes.Count - 8) more" }
        }
        "meshes" {
            $data.meshes | ForEach-Object {
                Write-Info "  $($_.name)  verts=$($_.controlPointCount)  polys=$($_.polygonCount)  uvLayers=$($_.uvLayers.Count)"
            }
        }
        "materials" {
            $data.materials | ForEach-Object {
                Write-Info "  $($_.name)  shading=$($_.shadingModel)  opacity=$($_.opacity)"
            }
        }
        "textures" {
            $data.textures | ForEach-Object {
                $emb = if ($_.hasEmbeddedData) { " [embedded $($_.embeddedByteSize)b]" } else { "" }
                Write-Info "  $($_.name)$emb  → $($_.relativeFilename)"
            }
        }
        "all" {
            Write-Info "  nodes=$($data.stats.nodeCount)  meshes=$($data.stats.geometryCount)  mats=$($data.stats.materialCount)  textures=$($data.stats.textureCount)  warnings=$($data.stats.warningCount)"
            if ($data.warnings.Count -gt 0) {
                $data.warnings | ForEach-Object { Write-Host "  WARN: $_" -ForegroundColor Yellow }
            }
        }
        default {
            # Just show raw for unknown commands
            Write-Info ($data | ConvertTo-Json -Depth 4)
        }
    }
}

# ── pre-flight ────────────────────────────────────────────────────────────────

if (-not (Test-Path $FbxPath)) {
    Write-Host "ERROR: File not found: $FbxPath" -ForegroundColor Red
    exit 1
}

$fileSize = (Get-Item $FbxPath).Length
$fileSizeKb = [math]::Round($fileSize / 1024, 1)

Write-Host ""
Write-Host "  FBX Service Test Runner" -ForegroundColor Cyan
Write-Host "  Host : $Host" -ForegroundColor White
Write-Host "  File : $FbxPath ($fileSizeKb KB)" -ForegroundColor White
Write-Host ""

# Check service is up
try {
    $health = Invoke-RestMethod -Uri "$Host/health" -Method Get -TimeoutSec 3
    Write-Pass "Service reachable — $($health | ConvertTo-Json -Compress)"
}
catch {
    Write-Fail "Cannot reach $Host/health — is the service running?"
    Write-Host "  Start it with: .\dev.ps1" -ForegroundColor Yellow
    exit 1
}

# ── run commands ──────────────────────────────────────────────────────────────

$commands = if ($Command) { @($Command) } else {
    @("list", "settings", "nodes", "meshes", "materials", "textures", "all")
}

$pass = 0; $fail = 0; $totalMs = 0

foreach ($cmd in $commands) {
    Write-Head "$cmd"
    $r = Invoke-FbxCommand -Cmd $cmd -Raw:$Raw -Verbose:$Verbose
    $totalMs += $r.Ms

    if (-not $r.Ok) {
        Write-Fail "$cmd → HTTP error: $($r.Error)"
        $fail++
        continue
    }

    $isSuccess = ($null -eq $r.Data.success) -or ($r.Data.success -eq $true)
    if ($isSuccess) {
        Write-Pass "$cmd  ($($r.Ms)ms)"
        $pass++
    } else {
        Write-Fail "$cmd  ($($r.Ms)ms)"
        $fail++
    }

    Show-JsonSummary $r.Data $cmd
}

# If we have meshes, also test mesh/<name> for the first one
if (-not $Command) {
    Write-Head "mesh/<name> (first mesh)"
    $meshList = Invoke-FbxCommand -Cmd "meshes"
    if ($meshList.Ok -and $meshList.Data.meshes.Count -gt 0) {
        $firstName = $meshList.Data.meshes[0].name
        $r = Invoke-FbxCommand -Cmd "mesh" -Target $firstName -Raw:$Raw
        $totalMs += $r.Ms
        if ($r.Ok -and $r.Data.success -ne $false) {
            Write-Pass "mesh/$firstName  ($($r.Ms)ms)"
            Write-Info "  verts=$($r.Data.controlPointCount)  polys=$($r.Data.polygonCount)  uvLayers=$($r.Data.uvLayers.Count)"
            $pass++
        } else {
            Write-Fail "mesh/$firstName"
            $fail++
        }
    } else {
        Write-Info "  (no meshes found)"
    }
}

# ── summary ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  ────────────────────────────────────────" -ForegroundColor DarkGray
$color = if ($fail -eq 0) { "Green" } else { "Red" }
Write-Host "  $pass passed  $fail failed  total $($totalMs)ms" -ForegroundColor $color
Write-Host ""

exit $fail

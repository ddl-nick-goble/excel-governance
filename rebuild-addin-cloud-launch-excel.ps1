# rebuild-addin-cloud-launch-excel.ps1 - Cloud rebuild + Excel launch helper
Write-Host "=== DGT Add-in Cloud Rebuild + Excel Launch ===" -ForegroundColor Cyan
Write-Host ""

$repoRoot = Split-Path -Parent $PSCommandPath
Set-Location $repoRoot

# Check if Excel is running
$excelProcesses = Get-Process -Name "EXCEL" -ErrorAction SilentlyContinue
if ($excelProcesses) {
    Write-Host "WARNING: Excel is currently running!" -ForegroundColor Yellow
    Write-Host "Found $($excelProcesses.Count) Excel process(es):" -ForegroundColor Yellow
    $excelProcesses | ForEach-Object { Write-Host "  - PID: $($_.Id)" -ForegroundColor Yellow }
    Write-Host ""

    $response = Read-Host "Would you like to close Excel and continue? (y/n)"
    if ($response -eq 'y' -or $response -eq 'Y') {
        Write-Host "Closing Excel..." -ForegroundColor Yellow
        Stop-Process -Name "EXCEL" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Host "Aborting cloud rebuild. Please close Excel manually and try again." -ForegroundColor Red
        exit 1
    }
}

# Run the cloud rebuild script
$cloudScript = Join-Path $repoRoot "rebuild-addin-cloud.ps1"
if (-not (Test-Path $cloudScript)) {
    Write-Host "Missing script: $cloudScript" -ForegroundColor Red
    exit 1
}

& $cloudScript
if ($LASTEXITCODE -ne 0) {
    Write-Host "Cloud rebuild failed." -ForegroundColor Red
    exit 1
}

# Find latest artifact folder
$artifactRoot = Join-Path $repoRoot "artifacts\rebuild-addin"
if (-not (Test-Path $artifactRoot)) {
    Write-Host "Artifact root not found: $artifactRoot" -ForegroundColor Red
    exit 1
}

$latestArtifactDir = Get-ChildItem -Path $artifactRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latestArtifactDir) {
    Write-Host "No artifact directories found in: $artifactRoot" -ForegroundColor Red
    exit 1
}

# Locate downloaded XLLs
$xll32 = Get-ChildItem -Path $latestArtifactDir.FullName -Recurse -Filter "DominoGovernanceTracker-AddIn-packed.xll" -ErrorAction SilentlyContinue |
    Select-Object -First 1
$xll64 = Get-ChildItem -Path $latestArtifactDir.FullName -Recurse -Filter "DominoGovernanceTracker-AddIn64-packed.xll" -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $xll32 -or -not $xll64) {
    Write-Host "Could not find packed XLLs in: $($latestArtifactDir.FullName)" -ForegroundColor Red
    exit 1
}

# Copy to the same publish paths as local rebuild
$publishDir = Join-Path $repoRoot "src\DominoGovernanceTracker\bin\Debug\net472\publish"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$dest32 = Join-Path $publishDir "DominoGovernanceTracker-AddIn-packed.xll"
$dest64 = Join-Path $publishDir "DominoGovernanceTracker-AddIn64-packed.xll"

Copy-Item -Force -Path $xll32.FullName -Destination $dest32
Copy-Item -Force -Path $xll64.FullName -Destination $dest64

Write-Host "Copied artifacts to publish folder:" -ForegroundColor Cyan
Write-Host "  32-bit: $dest32" -ForegroundColor White
Write-Host "  64-bit: $dest64" -ForegroundColor White

# Launch Excel and load the 64-bit add-in explicitly
Write-Host "Launching Excel with 64-bit add-in..." -ForegroundColor Green
Start-Process "excel.exe" -ArgumentList "`"$dest64`""

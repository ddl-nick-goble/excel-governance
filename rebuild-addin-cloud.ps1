$env:Path += ";C:\Program Files\GitHub CLI"
gh --version
# rebuild-addin-cloud.ps1 - Trigger GitHub Actions rebuild and download artifacts
Write-Host "=== DGT Add-in Cloud Rebuild ===" -ForegroundColor Cyan
Write-Host ""

$repoRoot = Split-Path -Parent $PSCommandPath
Set-Location $repoRoot

# Ensure GitHub CLI is installed
$gh = "gh"
if (-not (Get-Command $gh -ErrorAction SilentlyContinue)) {
    $ghCandidate = "C:\Program Files\GitHub CLI\gh.exe"
    if (Test-Path $ghCandidate) {
        $gh = $ghCandidate
    } else {
        Write-Host "GitHub CLI (gh) is not installed or not on PATH." -ForegroundColor Red
        Write-Host "Install it from https://cli.github.com/ and run 'gh auth login'." -ForegroundColor Yellow
        exit 1
    }
}

# Ensure gh is authenticated
$authStatus = & $gh auth status 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "GitHub CLI is not authenticated." -ForegroundColor Red
    Write-Host "Run: gh auth login" -ForegroundColor Yellow
    exit 1
}

# Ensure workflow exists
$workflowPath = Join-Path $repoRoot ".github\workflows\rebuild-addin.yml"
if (-not (Test-Path $workflowPath)) {
    Write-Host "Missing workflow: $workflowPath" -ForegroundColor Red
    Write-Host "Create it first so GitHub can run the rebuild." -ForegroundColor Yellow
    exit 1
}

# Check git status
$gitStatus = git status --porcelain
if ($gitStatus) {
    Write-Host "WARNING: You have uncommitted changes." -ForegroundColor Yellow
    Write-Host "Cloud rebuild uses the latest pushed commit, not your local changes." -ForegroundColor Yellow
    Write-Host ""
    $response = Read-Host "Continue anyway? (y/n)"
    if ($response -ne 'y' -and $response -ne 'Y') {
        Write-Host "Aborting cloud rebuild." -ForegroundColor Red
        exit 1
    }
}

# Push latest commit if needed
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
$headSha = (git rev-parse HEAD).Trim()
$upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "No upstream configured for branch '$branch'." -ForegroundColor Yellow
    Write-Host "Push with: git push -u origin $branch" -ForegroundColor Yellow
    exit 1
}

$localAhead = git rev-list --count '@{u}..HEAD'
if ([int]$localAhead -gt 0) {
    Write-Host "Local branch is ahead of remote by $localAhead commit(s)." -ForegroundColor Yellow
    $pushResp = Read-Host "Push now so cloud build uses latest commit? (y/n)"
    if ($pushResp -eq 'y' -or $pushResp -eq 'Y') {
        git push
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Push failed." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Aborting cloud rebuild." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Triggering GitHub Actions workflow..." -ForegroundColor Green
$workflowName = "rebuild-addin.yml"
$repo = (git config --get remote.origin.url).Trim()

# Trigger the workflow
$runTrigger = & $gh workflow run $workflowName
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to trigger workflow." -ForegroundColor Red
    exit 1
}

# Find the run for this branch/commit
Write-Host "Waiting for run to start..." -ForegroundColor Green
$runId = $null
$maxWaitSeconds = 120
$elapsed = 0
while (-not $runId -and $elapsed -lt $maxWaitSeconds) {
    $runsJson = & $gh run list --workflow $workflowName --branch $branch --limit 5 --json databaseId,headSha,status,createdAt
    if ($LASTEXITCODE -eq 0) {
        $runs = $runsJson | ConvertFrom-Json
        $match = $runs | Where-Object { $_.headSha -eq $headSha } | Select-Object -First 1
        if ($match) {
            $runId = $match.databaseId
            break
        }
    }
    Start-Sleep -Seconds 5
    $elapsed += 5
}

if (-not $runId) {
    Write-Host "Could not find a matching workflow run for commit $headSha." -ForegroundColor Red
    Write-Host "Check runs with: gh run list --workflow $workflowName" -ForegroundColor Yellow
    exit 1
}

Write-Host "Run ID: $runId" -ForegroundColor Cyan
Write-Host "Watching build..." -ForegroundColor Green

& $gh run watch $runId
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed or was canceled." -ForegroundColor Red
    exit 1
}

# Download artifacts
$artifactDir = Join-Path $repoRoot ("artifacts\rebuild-addin\" + $runId)
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

Write-Host "Downloading artifacts to: $artifactDir" -ForegroundColor Green

& $gh run download $runId -D $artifactDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Artifact download failed." -ForegroundColor Red
    exit 1
}

Write-Host "" 
Write-Host "=== Cloud Build Complete ===" -ForegroundColor Green
Write-Host "Artifacts:" -ForegroundColor Cyan
Write-Host "  $artifactDir" -ForegroundColor White

# =============================================================================
# Automated GCP Cloud Run Deployment Script for Beam Cloud AI Backend
# =============================================================================
param(
    [string]$ProjectId = "contextlens-512015",
    [string]$Region = "asia-south1",
    [string]$ServiceName = "beam-cloud-ai"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Beam Cloud AI Service — GCP Cloud Run Deployment" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "Project ID:   $ProjectId"
Write-Host "Region:       $Region"
Write-Host "Service Name: $ServiceName`n"

# 1. Verify gcloud authentication & configuration
Write-Host "Checking gcloud credentials..." -ForegroundColor Yellow
$activeAccount = (gcloud auth list --filter=status:ACTIVE --format="value(account)" 2>$null)
if (-not $activeAccount) {
    Write-Error "No active gcloud account found. Please run 'gcloud auth login' first."
}
Write-Host "Logged in as: $activeAccount" -ForegroundColor Green

# 2. Set active project
Write-Host "Setting active project to $ProjectId..." -ForegroundColor Yellow
gcloud config set project $ProjectId --quiet

# 3. Enable required Google Cloud APIs
Write-Host "`nEnabling required GCP APIs (Cloud Run, Cloud Build, Artifact Registry)..." -ForegroundColor Yellow
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com --project $ProjectId

# 4. Parse .env file if it exists to pass environment variables to Cloud Run
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = "c:\Users\Asus\Projects\Raycast\beam-backend" }
$envPath = Join-Path $scriptDir ".env"
$envVars = @()

if (Test-Path $envPath) {
    Write-Host "`nLoading secrets and variables from .env..." -ForegroundColor Yellow
    Get-Content $envPath | ForEach-Object {
        $line = $_.Trim()
        if ($line -and -not $line.StartsWith("#") -and $line.Contains("=")) {
            $parts = $line.Split("=", 2)
            $key = $parts[0].Trim()
            $val = $parts[1].Trim().Trim('"').Trim("'")
            if ($val -and -not $val.StartsWith("your_")) {
                $envVars += "$key=$val"
            }
        }
    }
}

# Construct gcloud run deploy command parameters
$deployArgs = @(
    "run", "deploy", $ServiceName,
    "--source", $scriptDir,
    "--project", $ProjectId,
    "--region", $Region,
    "--platform", "managed",
    "--allow-unauthenticated",
    "--port", "8080",
    "--memory", "512Mi",
    "--cpu", "1",
    "--min-instances", "0",
    "--max-instances", "5",
    "--timeout", "300"
)

if ($envVars.Count -gt 0) {
    $envString = ($envVars -join ",")
    $deployArgs += "--set-env-vars"
    $deployArgs += $envString
}

# 5. Deploy to Cloud Run
Write-Host "`nDeploying container to Cloud Run (building via Cloud Build)..." -ForegroundColor Cyan
& gcloud @deployArgs

# 6. Retrieve Service URL
$ServiceUrl = (gcloud run services describe $ServiceName --project $ProjectId --region $Region --format "value(status.url)" 2>$null)

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host "Beam AI Cloud Backend successfully deployed to GCP!" -ForegroundColor Green
Write-Host "Service URL: $ServiceUrl" -ForegroundColor Yellow
Write-Host "Health Check: $ServiceUrl/health" -ForegroundColor Yellow
Write-Host "========================================================`n" -ForegroundColor Green
Write-Host "Set this Service URL in your Beam settings!" -ForegroundColor Cyan

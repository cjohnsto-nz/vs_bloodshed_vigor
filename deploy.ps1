# Bloodshed Mod Deployment Script
# This script stops the game, builds the project, copies the output, and restarts the game.

# --- Configuration ---
$ErrorActionPreference = 'Stop' # Exit script on any error
$ProjectName = "Bloodshed"
$ProjectRoot = $PSScriptRoot # This special variable gets the directory where the script is located
$ModsDir = "C:\Users\chris\AppData\Roaming\VintagestoryData\Mods"
$VSProcessName = "Vintagestory"
$VSExePath = "C:\Users\chris\AppData\Roaming\Vintagestory\Vintagestory.exe"

# --- Pre-Build: Stop Game ---
Write-Host "Checking for running Vintage Story process..." -ForegroundColor Cyan
$vsProcess = Get-Process -Name $VSProcessName -ErrorAction SilentlyContinue
if ($vsProcess) {
    Write-Host "Vintage Story is running. Stopping process..."
    Stop-Process -Name $VSProcessName -Force
    # Give it a moment to release file locks
    Start-Sleep -Seconds 2
}

# --- Build Step ---
# Force clean build artifacts
Write-Host "Removing old build artifacts..."
if (Test-Path "Bloodshed\bin") { Remove-Item -Recurse -Force "Bloodshed\bin" }
if (Test-Path "Bloodshed\obj") { Remove-Item -Recurse -Force "Bloodshed\obj" }

Write-Host "Cleaning project..." -ForegroundColor Cyan
Set-Location "Bloodshed"
dotnet clean

Write-Host "Building Bloodshed project..." -ForegroundColor Cyan
dotnet build

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED. Deployment aborted." -ForegroundColor Red
    Set-Location ".."
    exit 1
}

Write-Host "Build SUCCEEDED." -ForegroundColor Green
Set-Location ".."

# --- Deploy Step ---
$SourceDir = Join-Path $ProjectRoot "Bloodshed\bin\Debug\Mods\mod"
$TempDir = Join-Path $env:TEMP "BloodshedTempDeploy"

if (-not (Test-Path $SourceDir)) {
    Write-Host "Error: Built mod directory not found at '$SourceDir'." -ForegroundColor Red
    Write-Host "Build may have failed or output path is incorrect." -ForegroundColor Red
    exit 1
}

Write-Host "Preparing deployment from '$SourceDir'..." -ForegroundColor Cyan

# Clean up any previous temp deployment
if (Test-Path $TempDir) {
    Remove-Item -Recurse -Force $TempDir
}

# Copy to temp directory first
Copy-Item -Recurse $SourceDir $TempDir
Write-Host "Copied to temporary directory: $TempDir" -ForegroundColor Yellow

# Remove existing mod from Mods directory
$TargetDir = Join-Path $ModsDir $ProjectName
if (Test-Path $TargetDir) {
    Write-Host "Removing existing mod from '$TargetDir'..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $TargetDir
}

# Copy from temp to final destination
Copy-Item -Recurse $TempDir $TargetDir
Write-Host "Deployed to: $TargetDir" -ForegroundColor Green

# Clean up temp directory
Remove-Item -Recurse -Force $TempDir

# --- Post-Deploy: Start Game ---
Write-Host "Starting Vintage Story..." -ForegroundColor Cyan
Start-Process $VSExePath

Write-Host "Deployment completed successfully!" -ForegroundColor Green
Write-Host "Bloodshed mod has been deployed to: $TargetDir" -ForegroundColor Green

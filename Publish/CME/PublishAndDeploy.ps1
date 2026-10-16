param (
	[string]$SolutionDir = "",
	[string]$SolutionName = ""
)

# 1. Fallback logic: If VS passes empty strings, use the solution this script sits in (Publish\CME\ is two levels below it)
if ([string]::IsNullOrWhiteSpace($SolutionDir) -or $SolutionDir -eq '""' -or $SolutionDir -eq '"\"')
{
	$SolutionDir = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
}

# The build lands beside this script; relative, because the WSL commands below run from $SolutionDir
[string]$stageDirectory = "./Publish/CME/Staging"

if ([string]::IsNullOrWhiteSpace($SolutionName) -or $SolutionName -eq '""') 
{
	$SolutionName = (Get-Item $SolutionDir).Name
}

Clear-Host
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "    Deploying $SolutionName to CME Server (AOT)" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# 2. Prompt for the commit comment
[string]$comment = Read-Host "Enter Comment for Snapshot"

if ([string]::IsNullOrWhiteSpace($comment)) 
{
	Write-Host "`nDeployment cancelled: Comment is required." -ForegroundColor Red
	Write-Host ""
	Read-Host "Press Enter to exit"
	exit
}

# Format the comment and folder names
[string]$safeComment = $comment -replace '[^a-zA-Z0-9\-_]', '_'
[string]$dateStr = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"

# Define the new directory structure
[string]$remoteBase = "~/csharp/$SolutionName"
[string]$remoteActive = "$remoteBase/$SolutionName"
[string]$remoteArchive = "$remoteBase/${SolutionName}_${dateStr}_${safeComment}"

# Navigate to the project directory
Set-Location -Path $SolutionDir

# 3. Build Release Payload (Native AOT via WSL)
Write-Host "`n[1/3] Building $SolutionName (Release - Native AOT via WSL)..." -ForegroundColor Yellow

# Output directory: Publish/CME/Staging (was ./deploy_stage until 2026-10-16)
wsl dotnet publish -c Release -r linux-x64 -o $stageDirectory /p:WSL_BUILD=true /p:CleanOnPublish=true

if ($LASTEXITCODE -ne 0) 
{
	Write-Host "`n[ERROR] Release build failed! Deployment aborted." -ForegroundColor Red
	Read-Host "Press Enter to close this window"
	exit
}

# 4. Sync to CME Server via WSL rsync
[string]$wslKeyPath = "~/.ssh/bigaj"
[string]$remoteTarget = "bigaj@10.172.13.228:$remoteActive/"

Write-Host "`n[2/3] Syncing to CME Server ($remoteTarget)..." -ForegroundColor Yellow

# Ensure remote active directory exists
ssh -i C:\Home\ssh\bigaj bigaj@10.172.13.228 "mkdir -p $remoteActive"

# Sync using WSL. 
# Added --exclude to ignore intermediate files.
# Added -m (--prune-empty-dirs) to prevent empty project folders from syncing.
wsl rsync -avzPm --delete --exclude='bin/' --exclude='obj/' -e "ssh -i $wslKeyPath -o StrictHostKeyChecking=no" "$stageDirectory/" $remoteTarget
if ($LASTEXITCODE -ne 0) 
{
	Write-Host "`n[ERROR] rsync failed! The deployment did not reach the server." -ForegroundColor Red
	Read-Host "Press Enter to close this window"
	exit
}

# 5. Create the History Snapshot
Write-Host "`n[3/3] Creating history snapshot on CME server..." -ForegroundColor Yellow
ssh -i C:\Home\ssh\bigaj bigaj@10.172.13.228 "cp -a $remoteActive $remoteArchive"

if ($LASTEXITCODE -ne 0) 
{
	Write-Host "`n[ERROR] Failed to create the history snapshot on the remote server." -ForegroundColor Red
	Read-Host "Press Enter to close this window"
	exit
}

Write-Host "`n=====================================================" -ForegroundColor Green
Write-Host "  Deployment Complete (Release AOT only)!" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "Active version   : $remoteActive/" -ForegroundColor DarkGray
Write-Host "History snapshot : $remoteArchive" -ForegroundColor DarkGray
Write-Host ""

# Keep the window open until you press Enter
Read-Host "Press Enter to close this window"
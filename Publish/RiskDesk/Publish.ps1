# Builds the risk-desk package: the Remote Mirror and the Workspace as standalone exes for any x64 Windows PC, with Start.bat
# the two config templates and the short guide beside them, into Staging\BigajRiskDesk beside this script, and zips it to Staging\BigajRiskDesk.zip
# (see Spec.md "Risk desk package"). Sends nothing anywhere. Run from anywhere: .\Publish\RiskDesk\Publish.ps1
$ErrorActionPreference = 'Stop'
$solutionDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stageDirectory = Join-Path $PSScriptRoot 'Staging'
$packageDirectory = Join-Path $stageDirectory 'BigajRiskDesk'   # Why a named folder inside Staging: the zip then unzips to BigajRiskDesk
$zipPath = Join-Path $stageDirectory 'BigajRiskDesk.zip'

# Step 1: an empty package folder, so no Data or log from an earlier run ships
if (Test-Path $packageDirectory) { Remove-Item -Recurse -Force $packageDirectory }
New-Item -ItemType Directory -Force $packageDirectory | Out-Null

# Step 2: both programs for AVX2, one build node (in parallel two builds of one shared project collide on its obj files). Why AVX2: the
# box's own build is x86-64-v4 (AVX-512), which stops with an illegal instruction on most desk PCs
foreach ($projectName in 'Mirror', 'Workspace') {
    dotnet publish (Join-Path $solutionDirectory "$projectName\$projectName.csproj") -c Release -r win-x64 -p:IlcInstructionSet=x86-64-v3 -m:1 -o $packageDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish.ps1: dotnet publish $projectName failed with exit code $LASTEXITCODE; see the output above" }
}

# Step 3: the launcher and the config templates; debug symbols, the JSON generator and the libraries' runtime files stay behind
Copy-Item (Join-Path $PSScriptRoot 'Start.bat'), (Join-Path $PSScriptRoot 'MirrorConfig.json'), (Join-Path $PSScriptRoot 'WorkspaceConfig.json') $packageDirectory
Remove-Item (Join-Path $packageDirectory '*.pdb'), (Join-Path $packageDirectory '*.dbg'), (Join-Path $packageDirectory 'Generator.*'), (Join-Path $packageDirectory '*.runtimeconfig.json'), (Join-Path $packageDirectory '*.deps.json')

# Step 4: the short guide beside Start.bat with only the screenshots it shows, minus its owner-check comment; the detailed guide stays behind
$guideSourceDirectory = Join-Path $PSScriptRoot 'Guide'
$guideHtml = [System.IO.File]::ReadAllText((Join-Path $guideSourceDirectory 'RiskDeskGuide.html'))
New-Item -ItemType Directory -Force (Join-Path $packageDirectory 'images') | Out-Null
foreach ($imagePath in ([regex]::Matches($guideHtml, 'images/[^"]+\.png') | ForEach-Object { $_.Value } | Sort-Object -Unique)) {
    Copy-Item (Join-Path $guideSourceDirectory $imagePath) (Join-Path $packageDirectory $imagePath)
}
[System.IO.File]::WriteAllText((Join-Path $packageDirectory 'RiskDeskGuide.html'), ($guideHtml -replace '(?s)<!--\s*OWNER CHECK.*?-->\s*', ''))

# Step 5: the zip to hand out, unzipping to a BigajRiskDesk folder
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path $packageDirectory -DestinationPath $zipPath
Write-Host "Publish.ps1: $zipPath"

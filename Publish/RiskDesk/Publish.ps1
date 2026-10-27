# Builds the risk-desk packages, one per folder in Templates (Adrian, NewRelease, THREADRIPPER, ...): the Remote Mirror and the Workspace as
# standalone exes for any x64 Windows PC, with Start.bat, the template's files (its two configs and its Data folder: the server's folder
# and default layouts) and the short guide beside them, into Staging\BigajRiskDesk_<template> beside this script, each zipped to
# Staging\BigajRiskDesk_<template>.zip (see Spec.md "Risk desk package"), and the phone app every phone installs, Staging\HFT.apk (see
# Spec.md "The phone"). Sends nothing anywhere. Run from anywhere: .\Publish\RiskDesk\Publish.ps1
$ErrorActionPreference = 'Stop'
$solutionDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stageDirectory = Join-Path $PSScriptRoot 'Staging'
$buildDirectory = Join-Path $stageDirectory 'Build'   # the exes, built once and copied into every package
$templateDirectories = Get-ChildItem (Join-Path $PSScriptRoot 'Templates') -Directory   # each: MirrorConfig.json, WorkspaceConfig.json, Data\

# Step 1: an empty build folder, so nothing from an earlier build ships
if (Test-Path $buildDirectory) { Remove-Item -Recurse -Force $buildDirectory }
New-Item -ItemType Directory -Force $buildDirectory | Out-Null

# Step 2: both programs for AVX2, one build node (in parallel two builds of one shared project collide on its obj files). Why AVX2: the
# box's own build is x86-64-v4 (AVX-512), which stops with an illegal instruction on most desk PCs
foreach ($projectName in 'Mirror', 'Workspace') {
    dotnet publish (Join-Path $solutionDirectory "$projectName\$projectName.csproj") -c Release -r win-x64 -p:IlcInstructionSet=x86-64-v3 -m:1 -o $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish.ps1: dotnet publish $projectName failed with exit code $LASTEXITCODE; see the output above" }
}

# Step 3: debug symbols, the JSON generator and the libraries' runtime files stay behind
Remove-Item (Join-Path $buildDirectory '*.pdb'), (Join-Path $buildDirectory '*.dbg'), (Join-Path $buildDirectory 'Generator.*'), (Join-Path $buildDirectory '*.runtimeconfig.json'), (Join-Path $buildDirectory '*.deps.json')

# Step 4: the short guide, minus its owner-check comment, with only the screenshots it shows; the detailed guide stays behind
$guideSourceDirectory = Join-Path $PSScriptRoot 'Guide'
$guideHtml = [System.IO.File]::ReadAllText((Join-Path $guideSourceDirectory 'RiskDeskGuide.html'))
$guideImagePaths = [regex]::Matches($guideHtml, 'images/[^"]+\.png') | ForEach-Object { $_.Value } | Sort-Object -Unique

foreach ($templateDirectory in $templateDirectories) {
    # Step 5: an empty package folder per template, named so its zip unzips to BigajRiskDesk_<template>
    $packageDirectory = Join-Path $stageDirectory "BigajRiskDesk_$($templateDirectory.Name)"
    $zipPath = "$packageDirectory.zip"
    if (Test-Path $packageDirectory) { Remove-Item -Recurse -Force $packageDirectory }
    New-Item -ItemType Directory -Force $packageDirectory | Out-Null

    # Step 6: the exes, the launcher, the template's configs and Data folder, and the guide
    Copy-Item (Join-Path $buildDirectory '*') $packageDirectory -Recurse
    Copy-Item (Join-Path $PSScriptRoot 'Start.bat') $packageDirectory
    Copy-Item (Join-Path $templateDirectory.FullName '*') $packageDirectory -Recurse
    New-Item -ItemType Directory -Force (Join-Path $packageDirectory 'images') | Out-Null
    foreach ($imagePath in $guideImagePaths) {
        Copy-Item (Join-Path $guideSourceDirectory $imagePath) (Join-Path $packageDirectory $imagePath)
    }
    [System.IO.File]::WriteAllText((Join-Path $packageDirectory 'RiskDeskGuide.html'), ($guideHtml -replace '(?s)<!--\s*OWNER CHECK.*?-->\s*', ''))

    # Step 7: the zip to hand out
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $packageDirectory -DestinationPath $zipPath
    Write-Host "Publish.ps1: $zipPath"
}

# Step 8: the phone app once, its config built in (Phone\MirrorConfig.json: every phone dials the Threadripper's relay). Why this PC's
# debug key, which a Release build signs with by default: a phone installs a newer APK over the old one only under the same key
$phonePublishDirectory = Join-Path $stageDirectory 'Phone'
dotnet publish (Join-Path $solutionDirectory 'Phone\Phone.csproj') -f net10.0-android -c Release -o $phonePublishDirectory -p:JavaSdkDirectory="$env:LOCALAPPDATA\Android\Jdk" -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk"
if ($LASTEXITCODE -ne 0) { throw "Publish.ps1: dotnet publish Phone failed with exit code $LASTEXITCODE; see the output above" }
Copy-Item (Join-Path $phonePublishDirectory 'com.bigaj.hft-Signed.apk') (Join-Path $stageDirectory 'HFT.apk') -Force
Write-Host "Publish.ps1: $(Join-Path $stageDirectory 'HFT.apk')"

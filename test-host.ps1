# SysMonCmdPal host-side build and test verification.
#
# This script intentionally uses Visual Studio MSBuild for the build step, then
# runs dotnet test with --no-build. Direct dotnet test can fail for this project
# because the .NET SDK MSBuild does not carry the Visual Studio AppxPackage tasks.

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [switch]$SkipBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$SolutionPath = Join-Path $ProjectRoot "SysMonCmdPal.sln"
$TestProjectPath = Join-Path $ProjectRoot "SysMonCmdPal.Tests\SysMonCmdPal.Tests.csproj"

function Log($Message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Get-MSBuildPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products "*" -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools with MSBuild and Windows App SDK prerequisites."
}

function Get-AppxMSBuildToolsPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Microsoft\VisualStudio\v18.0\AppxPackage\",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"
    )

    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    return $null
}

function Invoke-CheckedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

Set-Location $ProjectRoot

if (-not $SkipBuild) {
    $msbuild = Get-MSBuildPath
    $appxTools = Get-AppxMSBuildToolsPath
    $buildArgs = @(
        $SolutionPath,
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform",
        "/v:m"
    )

    if (-not $NoRestore) {
        $buildArgs += "/restore"
    }

    if ($appxTools) {
        $buildArgs += "/p:AppxMSBuildToolsPath=$appxTools"
    }

    Log "Building with Visual Studio MSBuild: $msbuild"
    Invoke-CheckedCommand $msbuild $buildArgs
}
else {
    Log "Skipping build"
}

$testArgs = @(
    "test",
    $TestProjectPath,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "--no-build",
    "--no-restore",
    "--nologo"
)

Log "Running tests without rebuilding"
Invoke-CheckedCommand "dotnet" $testArgs
Log "Host verification completed"

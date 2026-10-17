# Purpose: build the main program and plugin; Debug by default, Release when --release is passed

# Parameters
param(
    [switch]$Release
)

# Support the --release style (PowerShell 7+ parses --release into $args rather than a switch)
if ($args -contains '--release' -or $args -contains 'release') { $Release = $true }

# Error handling
$ErrorActionPreference = "Stop"
trap {
    Write-Host "Command aborted: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "$($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    exit 1
}

# Paths and configuration
$config = if ($Release) { "Release" } else { "Debug" }
$repoRoot = Split-Path -Parent $PSScriptRoot
$mainOutDir = Join-Path $repoRoot ".run"
$pluginOutDir = Join-Path $repoRoot ".run\config\plugins\DragonMaid"
# Pin the target runtime to Windows x64
$runtimeIdentifier = "win-x64"
# Shared Windows resource directories inside packages
$runtimeKeepNames = @("win", "win-x64")

# Remove non-Windows-x64 runtime directories
function Remove-NonWinX64Runtimes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetDir
    )

    $runtimesDir = Join-Path $TargetDir "runtimes"
    if (-not (Test-Path -LiteralPath $runtimesDir)) { return }
    Get-ChildItem -LiteralPath $runtimesDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if (-not ($runtimeKeepNames -contains $_.Name)) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
        }
    }
}

# Final cleanup of both the main and plugin directories
function Normalize-RuntimesForWinX64 {
    foreach ($dir in @($mainOutDir, $pluginOutDir)) {
        Remove-NonWinX64Runtimes -TargetDir $dir
    }
}

# Remove main program and unrelated artifacts from the plugin directory
function Keep-OnlyPluginArtifacts {
    $keepFileNames = @(
        "LongMaidDisplayPlugin.dll",
        "LongMaidDisplayPlugin.pdb",
        "LongMaidDisplayPlugin.deps.json",
        "s_skill.json"
    )
    if (-not (Test-Path -LiteralPath $pluginOutDir)) { return }
    Get-ChildItem -LiteralPath $pluginOutDir -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSIsContainer) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
        } elseif (-not ($keepFileNames -contains $_.Name)) {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
        }
    }
}

# Make sure the main program and plugin output directories exist
foreach ($dir in @($mainOutDir, $pluginOutDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}
$buildProjects = @(
    @{ Name = "main program"; Path = (Join-Path $repoRoot "src\SnakeTail.csproj"); OutDir = $mainOutDir; ExtraArgs = @("/property:OutDir=$mainOutDir\", "/property:RuntimeIdentifier=$runtimeIdentifier", "/property:PlatformTarget=x64") },
    @{ Name = "DragonMaid plugin"; Path = (Join-Path $repoRoot "plugins\LongMaidDisplayPlugin\LongMaidDisplayPlugin.csproj"); OutDir = $pluginOutDir; ExtraArgs = @("/property:OutDir=$pluginOutDir\", "/property:RuntimeIdentifier=$runtimeIdentifier", "/property:PlatformTarget=x64") }
)

Write-Host "Build configuration: $config" -ForegroundColor Cyan

# Build the main program, then the plugin
foreach ($item in $buildProjects) {
    if (-not (Test-Path -LiteralPath $item.Path)) {
        throw "Build failed: project file not found ($($item.Name))`nPath: $($item.Path)"
    }

    Write-Host "Building: $($item.Name)" -ForegroundColor Cyan
    # Output goes into the .run directory structure
    dotnet build $item.Path --configuration $config --nologo --verbosity quiet /property:GenerateFullPaths=true @($item.ExtraArgs)
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed: $($item.Name) (exit code $LASTEXITCODE)`nProject: $($item.Path)"
    }
}

# Remove runtime assets for unrelated platforms (final state)
Normalize-RuntimesForWinX64
# Keep only plugin artifacts in the plugin directory
Keep-OnlyPluginArtifacts

Write-Host "Build complete." -ForegroundColor Green

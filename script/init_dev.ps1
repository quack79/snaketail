# Purpose: initialize the dev environment (.run directory, config file seeding, NuGet restore)

param()

$ErrorActionPreference = "Stop"
trap {
    Write-Host "Command aborted: $_" -ForegroundColor Red
    Write-Host "$($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    exit 1
}

function Initialize-DevEnvironment {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $runDir = Join-Path $repoRoot ".run"
    $logDir = Join-Path $runDir "log"
    $configDir = Join-Path $runDir "config"
    $pluginConfigDir = Join-Path $runDir "config\plugins"
    foreach ($d in @($runDir, $logDir, $configDir, $pluginConfigDir)) {
        if (-not (Test-Path $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
            Write-Host "Created: $d" -ForegroundColor Green
        }
    }
    $srcCfg = Join-Path $repoRoot "src\app.config"
    $dstCfg = Join-Path $configDir "app.config"
    if ((Test-Path $srcCfg) -and -not (Test-Path $dstCfg)) {
        Copy-Item -LiteralPath $srcCfg -Destination $dstCfg -Force
        Write-Host "Copied default config: $dstCfg" -ForegroundColor Green
    }
    $sln = Join-Path $repoRoot "SnakeTail.sln"
    Write-Host "dotnet restore ..." -ForegroundColor Cyan
    dotnet restore $sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
    Write-Host "Dev environment initialized." -ForegroundColor Green
}

Initialize-DevEnvironment

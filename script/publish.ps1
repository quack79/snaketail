# Purpose: build Release and copy the publishable files to .dist under the repo root (in a versioned subdirectory)

param()

$ErrorActionPreference = "Stop"
trap {
    Write-Host "Command aborted: $_" -ForegroundColor Red
    Write-Host "$($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    exit 1
}

function Publish-ReleaseToDist {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $proj = Join-Path $repoRoot "src\SnakeTail.csproj"
    $raw = Get-Content -LiteralPath $proj -Raw -Encoding UTF8
    if ($raw -notmatch '<Version>([^<]+)</Version>') {
        throw "Could not parse <Version> from the csproj"
    }
    $version = $Matches[1].Trim()
    $buildScript = Join-Path $repoRoot "script\build.ps1"
    & $buildScript -Release
    if ($LASTEXITCODE -ne 0) { throw "Release build failed" }

    $outDir = Join-Path $repoRoot ".run"
    if (-not (Test-Path (Join-Path $outDir "SnakeTail.exe"))) {
        throw "Build output not found: $outDir\SnakeTail.exe"
    }
    $distRoot = Join-Path $repoRoot ".dist"
    $distDir = Join-Path $distRoot "SnakeTail-$version"
    if (Test-Path $distDir) {
        Remove-Item -LiteralPath $distDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    # Exclude the run log directory so local logs don't end up in the release package
    Get-ChildItem -LiteralPath $outDir -Force | Where-Object { $_.Name -ne "log" } | Copy-Item -Destination $distDir -Recurse -Force
    Write-Host "Published to: $distDir" -ForegroundColor Green
}

Publish-ReleaseToDist

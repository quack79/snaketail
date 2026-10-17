# Purpose: build, force-kill the target program, clear run logs and start SnakeTail; supports --test to run unit tests

param(
    [switch]$Release,
    [switch]$Test
)

# Support the --release/--test style (PowerShell 7+ parses --xxx into $args rather than a switch)
$filterValue = $null
$i = 0
while ($i -lt $args.Count) {
    $a = $args[$i]
    if ($a -eq '--release' -or $a -eq 'release') { $Release = $true; $i++; continue }
    if ($a -eq '-Release') { $Release = $true; $i++; continue }
    if ($a -eq '--test') { $Test = $true; $i++; continue }
    if ($a -eq '-Test') { $Test = $true; $i++; continue }
    if ($a -eq '--filter') {
        if ($i + 1 -ge $args.Count) {
            Write-Host "Error: --filter is missing its value" -ForegroundColor Red
            Write-Host "Usage: .\0run.ps1 [-Release] [--release] [--test] [--filter ""FullyQualifiedName~TestName""]" -ForegroundColor Yellow
            exit 1
        }
        $filterValue = $args[$i + 1]
        $i += 2
        continue
    }
    Write-Host "Error: unsupported argument: $a" -ForegroundColor Red
    Write-Host "Supported arguments: -Release, --release, --test, -Test, --filter ""expression""" -ForegroundColor Yellow
    exit 1
}

$ErrorActionPreference = "Stop"
trap {
    Write-Host "Command aborted: $_" -ForegroundColor Red
    Write-Host "$($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    exit 1
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$config = if ($Release) { "Release" } else { "Debug" }
$runDir = Join-Path $scriptRoot ".run"
$runLogDir = Join-Path $runDir "log"
$runConfigDir = Join-Path $runDir "config"

function Get-ProblemLogPaths {
    $paths = @()
    if (Test-Path $runLogDir) {
        Get-ChildItem -Path $runLogDir -File -ErrorAction SilentlyContinue | ForEach-Object {
            $fullName = $_.FullName
            $name = $_.Name
            $hasContent = $_.Length -gt 0
            if (-not $hasContent) { return }
            if ($name -match 'crash|error') { $paths += $fullName; return }
            if ($_.Extension -eq '.log') {
                $content = Get-Content -Path $fullName -Raw -ErrorAction SilentlyContinue
                if ($content -and $content -match 'error') { $paths += $fullName }
            }
        }
    }
    return $paths
}

# 1. Force-kill the target program
Write-Host "Step 1/5: force-killing target program ..." -ForegroundColor Cyan
Get-Process -Name "SnakeTail" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# 2. Build
Write-Host "Step 2/5: building ($config) ..." -ForegroundColor Cyan
$buildScript = Join-Path $scriptRoot "script\build.ps1"
if ($Release) { & $buildScript -Release } else { & $buildScript }
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# 3. Clean up: working directory .run, empty log; ensure config exists and seed app.config from src
Write-Host "Step 3/5: cleaning up ..." -ForegroundColor Cyan
if (-not (Test-Path $runDir)) { New-Item -ItemType Directory -Path $runDir -Force | Out-Null }
if (-not (Test-Path $runLogDir)) { New-Item -ItemType Directory -Path $runLogDir -Force | Out-Null }
Get-ChildItem -Path $runLogDir -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse
if (-not (Test-Path $runConfigDir)) { New-Item -ItemType Directory -Path $runConfigDir -Force | Out-Null }
$srcAppConfig = Join-Path $scriptRoot "src\app.config"
$dstAppConfig = Join-Path $runConfigDir "app.config"
if ((Test-Path $srcAppConfig) -and -not (Test-Path $dstAppConfig)) {
    Copy-Item -LiteralPath $srcAppConfig -Destination $dstAppConfig -Force
}

# 4. Run
Write-Host "Step 4/5: running ..." -ForegroundColor Cyan
# If the project were a plugin it would be installed into the target program here (this is the main program, so skipped)

if ($Test) {
    $testsProj = $null
    $testsDir = Join-Path $scriptRoot "tests"
    if (Test-Path $testsDir) {
        $testsProj = Get-ChildItem -Path $testsDir -Filter "*.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $testsProj) {
        Write-Host "Error: no unit test project yet (no .csproj found under tests)" -ForegroundColor Red
        exit 1
    }
    $testArgs = @("test", $testsProj.FullName, "--configuration", $config)
    if ($filterValue) { $testArgs += "--filter"; $testArgs += $filterValue }
    & dotnet $testArgs
    $testExitCode = $LASTEXITCODE
    Write-Host "Step 5/5: checking results ..." -ForegroundColor Cyan
    $problemLogs = Get-ProblemLogPaths
    if ($problemLogs.Count -gt 0) {
        foreach ($p in $problemLogs) { Write-Host $p }
    }
    if ($testExitCode -ne 0) { exit $testExitCode }
    exit 0
}

$exe = Join-Path $scriptRoot ".run\SnakeTail.exe"
if (-not (Test-Path $exe)) {
    throw "Executable not found: $exe"
}
Start-Process -FilePath $exe -WorkingDirectory $runDir -Wait

Write-Host "Step 5/5: checking results ..." -ForegroundColor Cyan
$problemLogs = Get-ProblemLogPaths
if ($problemLogs.Count -gt 0) {
    foreach ($p in $problemLogs) { Write-Host $p }
}

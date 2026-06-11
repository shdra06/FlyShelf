#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Runs the Windows App Certification Kit (WACK) against the FlyShelf MSIX package.
    
.DESCRIPTION
    This script:
    1. Finds the latest .msix file in MicrosoftBuild\Output\
    2. Installs it locally (so WACK can test the installed app)
    3. Runs WACK validation
    4. Opens the HTML report when done

.NOTES
    Must be run as Administrator (WACK requires elevation).
    Run: powershell -ExecutionPolicy Bypass -File Run_WACK.ps1
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $scriptDir "Output"
$reportDir = Join-Path $outputDir "WackReport"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  FlyShelf — WACK Testing Pipeline" -ForegroundColor Cyan  
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ─── Step 1: Find MSIX package ───
Write-Host "[1/4] Locating MSIX package..." -ForegroundColor Yellow

$msixFiles = Get-ChildItem -Path $outputDir -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue
if (-not $msixFiles) {
    $msixFiles = Get-ChildItem -Path $outputDir -Filter "*.msixbundle" -Recurse -ErrorAction SilentlyContinue
}
if (-not $msixFiles) {
    Write-Host "  ERROR: No .msix or .msixbundle found in $outputDir" -ForegroundColor Red
    Write-Host "  Run Build_Store.bat first!" -ForegroundColor Red
    exit 1
}

$msixPath = ($msixFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
Write-Host "  Found: $msixPath" -ForegroundColor Green
Write-Host ""

# ─── Step 2: Locate WACK ───
Write-Host "[2/4] Locating Windows App Certification Kit..." -ForegroundColor Yellow

$wackExe = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe"
if (-not (Test-Path $wackExe)) {
    Write-Host "  ERROR: WACK not found at $wackExe" -ForegroundColor Red
    Write-Host "  Install Windows SDK from https://developer.microsoft.com/windows/downloads/windows-sdk/" -ForegroundColor Red
    exit 1
}
Write-Host "  WACK found: $wackExe" -ForegroundColor Green
Write-Host ""

# ─── Step 3: Run WACK ───
Write-Host "[3/4] Running WACK validation (this may take several minutes)..." -ForegroundColor Yellow
Write-Host "  The app will be installed, launched, and tested automatically." -ForegroundColor DarkGray
Write-Host "  DO NOT interact with the app during testing." -ForegroundColor DarkGray
Write-Host ""

if (-not (Test-Path $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}

$reportXml = Join-Path $reportDir "wack_report.xml"

# Run WACK against the package file
& $wackExe test -apptype desktop -setuppath $msixPath -reportoutputpath $reportXml
$wackExit = $LASTEXITCODE

Write-Host ""

# ─── Step 4: Report results ───
Write-Host "[4/4] Test results:" -ForegroundColor Yellow

if (Test-Path $reportXml) {
    # Parse the XML report for pass/fail summary
    [xml]$report = Get-Content $reportXml
    $overall = $report.REPORT.OVERALL_RESULT
    
    if ($overall -eq "PASS") {
        Write-Host ""
        Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Green
        Write-Host "  ║  ✅ WACK PASSED — Store Ready!       ║" -ForegroundColor Green
        Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor Red
        Write-Host "  ║  ❌ WACK FAILED — Issues Found       ║" -ForegroundColor Red
        Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Red
        
        # List failed tests
        Write-Host ""
        Write-Host "  Failed tests:" -ForegroundColor Red
        $tests = $report.REPORT.REQUIREMENTS.REQUIREMENT | Where-Object { $_.RESULT -eq "FAIL" }
        foreach ($test in $tests) {
            Write-Host "    ✗ $($test.TEST_NAME): $($test.DESCRIPTION)" -ForegroundColor Red
        }
    }
    
    Write-Host ""
    Write-Host "  Full report: $reportXml" -ForegroundColor Cyan
    
    # Try to open the report
    if (Test-Path $reportXml) {
        Start-Process $reportXml
    }
} else {
    Write-Host "  WARNING: Report file not generated." -ForegroundColor Yellow
    Write-Host "  WACK exit code: $wackExit" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Done!" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

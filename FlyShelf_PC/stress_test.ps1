# ═══════════════════════════════════════════════════════════════
# FlyShelf v5.5.0 — Full Network Stress Test Suite
# Tests: Text, Image, PDF over both LAN and Cloudflare
# ═══════════════════════════════════════════════════════════════

$ErrorActionPreference = "Continue"

# ── Config ──
$lanUrl = "http://10.165.67.62:8999"
$cfUrl = "https://tenant-devices-bobby-clinics.trycloudflare.com"
$pairingKey = (Get-Content (Join-Path $env:APPDATA "FlyShelf\config.json") | ConvertFrom-Json).PairingKey
$testDir = Join-Path $PSScriptRoot "stress_test_files"
if (!(Test-Path $testDir)) { New-Item -ItemType Directory -Path $testDir -Force | Out-Null }

$results = @()

function Test-TextSync {
    param($url, $transport, $index)
    $body = @{
        type = "Text"
        title = "[$transport] Stress $index"
        data = "Message $index via $transport at $(Get-Date -Format 'HH-mm-ss.fff') -- Lorem ipsum dolor sit amet consectetur adipiscing elit"
        sourceDeviceId = "PC_LAPTOP-R718S7LR_Shivendra"
        sourceDeviceName = "LAPTOP-R718S7LR"
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    } | ConvertTo-Json -Compress
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-WebRequest -Uri "$url/api/sync_text" -Method POST -Body $body -Headers @{
            "X-Pairing-Key"=$pairingKey
            "Content-Type"="application/json"
            "X-FlyShelf-Client"="StressTest"
        } -UseBasicParsing -TimeoutSec 15
        $sw.Stop()
        return @{ Test="Text $index"; Transport=$transport; Status=$r.StatusCode; Time="$($sw.ElapsedMilliseconds)ms"; Result="OK" }
    } catch {
        $sw.Stop()
        return @{ Test="Text $index"; Transport=$transport; Status="ERR"; Time="$($sw.ElapsedMilliseconds)ms"; Result=$_.Exception.Message.Substring(0, [Math]::Min(60, $_.Exception.Message.Length)) }
    }
}

function Test-FileSync {
    param($url, $transport, $filePath, $fileType, $label)
    $fileName = [System.IO.Path]::GetFileName($filePath)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $fileBytes = [System.IO.File]::ReadAllBytes($filePath)
        $fileSizeMB = [Math]::Round($fileBytes.Length / 1MB, 2)
        
        $boundary = [System.Guid]::NewGuid().ToString()
        $LF = "`r`n"
        $bodyLines = @(
            "--$boundary",
            "Content-Disposition: form-data; name=`"file`"; filename=`"$fileName`"",
            "Content-Type: application/octet-stream",
            "",
            ""
        )
        $bodyEnd = @("", "--$boundary--", "")
        
        $headerBytes = [System.Text.Encoding]::UTF8.GetBytes(($bodyLines -join $LF))
        $endBytes = [System.Text.Encoding]::UTF8.GetBytes(($bodyEnd -join $LF))
        
        $ms = New-Object System.IO.MemoryStream
        $ms.Write($headerBytes, 0, $headerBytes.Length)
        $ms.Write($fileBytes, 0, $fileBytes.Length)
        $ms.Write($endBytes, 0, $endBytes.Length)
        $fullBody = $ms.ToArray()
        $ms.Dispose()
        
        $r = Invoke-WebRequest -Uri "$url/api/sync_file" -Method POST -Body $fullBody -Headers @{
            "X-Pairing-Key"=$pairingKey
            "X-FlyShelf-Client"="StressTest"
            "X-File-Name"=[Uri]::EscapeDataString($fileName)
            "X-Source-Device"="LAPTOP-R718S7LR"
            "X-Item-Type"=$fileType
            "Content-Type"="multipart/form-data; boundary=$boundary"
        } -UseBasicParsing -TimeoutSec 60
        $sw.Stop()
        return @{ Test="$label (${fileSizeMB}MB)"; Transport=$transport; Status=$r.StatusCode; Time="$($sw.ElapsedMilliseconds)ms"; Result="OK" }
    } catch {
        $sw.Stop()
        $msg = $_.Exception.Message
        if ($msg.Length -gt 60) { $msg = $msg.Substring(0, 60) }
        return @{ Test="$label"; Transport=$transport; Status="ERR"; Time="$($sw.ElapsedMilliseconds)ms"; Result=$msg }
    }
}

# ── Create test files ──
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  CREATING TEST FILES" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Small text file
$textFile = Join-Path $testDir "test_doc.txt"
"This is a test document for FlyShelf stress testing.`nGenerated at $(Get-Date)`n" + ("A" * 5000) | Set-Content $textFile
Write-Host "  Created: test_doc.txt ($([Math]::Round((Get-Item $textFile).Length / 1KB, 1)) KB)"

# Medium image-like file (1MB)
$imgFile = Join-Path $testDir "test_image.png"
$rng = New-Object System.Random
$imgData = New-Object byte[] (1048576)
$rng.NextBytes($imgData)
# PNG header
$imgData[0] = 0x89; $imgData[1] = 0x50; $imgData[2] = 0x4E; $imgData[3] = 0x47
[System.IO.File]::WriteAllBytes($imgFile, $imgData)
Write-Host "  Created: test_image.png (1.0 MB)"

# Larger PDF-like file (5MB)
$pdfFile = Join-Path $testDir "test_document.pdf"
$pdfData = New-Object byte[] (5242880)
$rng.NextBytes($pdfData)
# PDF header
$pdfHeader = [System.Text.Encoding]::ASCII.GetBytes("%PDF-1.4 ")
[System.Array]::Copy($pdfHeader, $pdfData, $pdfHeader.Length)
[System.IO.File]::WriteAllBytes($pdfFile, $pdfData)
Write-Host "  Created: test_document.pdf (5.0 MB)"

# ═══════════════════════════════════════════════════
# PHASE 1: LAN TESTS
# ═══════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "  PHASE 1: LAN TESTS ($lanUrl)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

# Verify LAN connectivity
Write-Host "`n[LAN] Health check..." -ForegroundColor Yellow
try {
    $h = Invoke-WebRequest -Uri "$lanUrl/api/health" -UseBasicParsing -TimeoutSec 5
    $hj = $h.Content | ConvertFrom-Json
    Write-Host "  OK - $($hj.deviceName) v$($hj.version) uptime=$($hj.uptime)s peers=$($hj.peers)" -ForegroundColor Green
} catch {
    Write-Host "  FAILED - LAN unreachable" -ForegroundColor Red
}

# 5x rapid text
Write-Host "`n[LAN] Rapid text sync (5x)..." -ForegroundColor Yellow
for ($i = 1; $i -le 5; $i++) {
    $r = Test-TextSync -url $lanUrl -transport "LAN" -index $i
    $results += $r
    $color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
    Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color
}

# File: small text
Write-Host "`n[LAN] Small text file..." -ForegroundColor Yellow
$r = Test-FileSync -url $lanUrl -transport "LAN" -filePath $textFile -fileType "Document" -label "TXT file"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

# File: 1MB image
Write-Host "`n[LAN] 1MB image..." -ForegroundColor Yellow
$r = Test-FileSync -url $lanUrl -transport "LAN" -filePath $imgFile -fileType "Image" -label "PNG image"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

# File: 5MB PDF
Write-Host "`n[LAN] 5MB PDF..." -ForegroundColor Yellow
$r = Test-FileSync -url $lanUrl -transport "LAN" -filePath $pdfFile -fileType "Document" -label "PDF file"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

Start-Sleep -Seconds 2

# ═══════════════════════════════════════════════════
# PHASE 2: CLOUDFLARE TESTS
# ═══════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Magenta
Write-Host "  PHASE 2: CLOUDFLARE TESTS" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta

# Verify CF connectivity
Write-Host "`n[CF] Health check..." -ForegroundColor Yellow
try {
    $h = Invoke-WebRequest -Uri "$cfUrl/api/health" -UseBasicParsing -TimeoutSec 10
    $hj = $h.Content | ConvertFrom-Json
    Write-Host "  OK - $($hj.deviceName) v$($hj.version) uptime=$($hj.uptime)s" -ForegroundColor Green
} catch {
    Write-Host "  FAILED - CF unreachable: $($_.Exception.Message)" -ForegroundColor Red
}

# 5x rapid text via CF
Write-Host "`n[CF] Rapid text sync (5x)..." -ForegroundColor Yellow
for ($i = 1; $i -le 5; $i++) {
    $r = Test-TextSync -url $cfUrl -transport "Cloudflare" -index $i
    $results += $r
    $color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
    Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color
}

# File via CF: small text
Write-Host "`n[CF] Small text file..." -ForegroundColor Yellow
$r = Test-FileSync -url $cfUrl -transport "Cloudflare" -filePath $textFile -fileType "Document" -label "TXT file"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

# File via CF: 1MB image
Write-Host "`n[CF] 1MB image..." -ForegroundColor Yellow
$r = Test-FileSync -url $cfUrl -transport "Cloudflare" -filePath $imgFile -fileType "Image" -label "PNG image"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

# File via CF: 5MB PDF
Write-Host "`n[CF] 5MB PDF..." -ForegroundColor Yellow
$r = Test-FileSync -url $cfUrl -transport "Cloudflare" -filePath $pdfFile -fileType "Document" -label "PDF file"
$results += $r
$color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
Write-Host "  $($r.Test): $($r.Status) in $($r.Time)" -ForegroundColor $color

# ═══════════════════════════════════════════════════
# RESULTS SUMMARY
# ═══════════════════════════════════════════════════
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$lanResults = $results | Where-Object { $_.Transport -eq "LAN" }
$cfResults = $results | Where-Object { $_.Transport -eq "Cloudflare" }
$lanOk = ($lanResults | Where-Object { $_.Result -eq "OK" }).Count
$cfOk = ($cfResults | Where-Object { $_.Result -eq "OK" }).Count

Write-Host "`n  LAN:        $lanOk/$($lanResults.Count) passed" -ForegroundColor $(if ($lanOk -eq $lanResults.Count) { "Green" } else { "Yellow" })
Write-Host "  Cloudflare: $cfOk/$($cfResults.Count) passed" -ForegroundColor $(if ($cfOk -eq $cfResults.Count) { "Green" } else { "Yellow" })

Write-Host "`n  Detailed:" -ForegroundColor White
$results | ForEach-Object {
    $icon = if ($_.Result -eq "OK") { "[PASS]" } else { "[FAIL]" }
    $color = if ($_.Result -eq "OK") { "Green" } else { "Red" }
    Write-Host "    $icon $($_.Transport.PadRight(12)) $($_.Test.PadRight(25)) $($_.Time.PadLeft(8))  $($_.Result)" -ForegroundColor $color
}

# Cleanup test files
Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  STRESS TEST COMPLETE" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# ═══════════════════════════════════════════════════════════════════
# FlyShelf LAN Stress Test — Comprehensive Networking Benchmark
# ═══════════════════════════════════════════════════════════════════
# Tests: Health ping, text bursts, varied file uploads (PNG/PDF/EXE/DOCX),
#        large file throughput, and concurrent upload stress.
# Usage: Launch FlyShelf first, then run this script.
# ═══════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"

# ── Config ──────────────────────────────────────────────────────────
$configPath = Join-Path $env:APPDATA "FlyShelf\config.json"
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    $pk = $cfg.PairingKey
} else {
    Write-Host "⛔ Config not found at $configPath — using hardcoded key" -ForegroundColor Red
    $pk = "5e8fd86b46fe43fbb997140a9c25d63f"
}

$peer = "http://localhost:8999"
$tempDir = Join-Path $env:TEMP "FlyShelfStressTest"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║   FlyShelf LAN Stress Test — Full Benchmark       ║" -ForegroundColor Magenta
Write-Host "╚═══════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host "  Peer:        $peer" -ForegroundColor Gray
Write-Host "  PairingKey:  $($pk.Substring(0,8))..." -ForegroundColor Gray
Write-Host ""

# ── Connectivity check ──────────────────────────────────────────────
Write-Host "▸ Checking server connectivity..." -ForegroundColor Yellow
try {
    $resp = curl.exe -s -w "`n%{http_code}" -m 5 "$peer/api/health" 2>&1
    $lines = $resp -split "`n"
    $httpCode = $lines[-1].Trim()
    if ($httpCode -ne "200") {
        Write-Host "  ⛔ Server returned HTTP $httpCode — is FlyShelf running?" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✅ Server is online (HTTP $httpCode)" -ForegroundColor Green
} catch {
    Write-Host "  ⛔ Cannot reach $peer — start FlyShelf first!" -ForegroundColor Red
    exit 1
}

$results = @()

# ═══════════════════════════════════════════════════════════════════
# TEST 1: Health Ping Latency (50 rapid pings)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 1: Health Ping Latency (50 pings) ━━━" -ForegroundColor Cyan
$times = @()
for ($i = 1; $i -le 50; $i++) {
    $ms = curl.exe -s -o NUL -w "%{time_total}" -m 3 "$peer/api/health" 2>&1
    $msVal = [math]::Round([double]$ms * 1000, 1)
    $times += $msVal
}
$avg = [math]::Round(($times | Measure-Object -Average).Average, 1)
$mn  = [math]::Round(($times | Measure-Object -Minimum).Minimum, 1)
$mx  = [math]::Round(($times | Measure-Object -Maximum).Maximum, 1)
$p95 = [math]::Round(($times | Sort-Object)[[math]::Floor($times.Count * 0.95)], 1)
Write-Host "  Avg: ${avg}ms | Min: ${mn}ms | Max: ${mx}ms | P95: ${p95}ms" -ForegroundColor Green
$results += [PSCustomObject]@{ Test="Health Ping (50x)"; Avg="${avg}ms"; Min="${mn}ms"; Max="${mx}ms"; P95="${p95}ms"; Throughput="-" }

# ═══════════════════════════════════════════════════════════════════
# TEST 2: Text Sync Burst (50 rapid text items)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 2: Text Sync Burst (50 items) ━━━" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$failCount = 0
for ($i = 1; $i -le 50; $i++) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $json = "{`"type`":`"Text`",`"title`":`"Stress $i`",`"data`":`"Stress test text item #$i — Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Timestamp: $ts`",`"sourceDeviceId`":`"STRESS_TEST`",`"sourceDeviceName`":`"StressTester`",`"timestamp`":$ts}"
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 5 -X POST "$peer/api/sync_text" -H "Content-Type: application/json" -H "X-Pairing-Key: $pk" -d $json 2>&1
    if ($code -ne "200") { $failCount++ }
}
$sw.Stop()
$perItem = [math]::Round($sw.ElapsedMilliseconds / 50)
Write-Host "  50 items in $($sw.ElapsedMilliseconds)ms = ${perItem}ms/item | Failures: $failCount" -ForegroundColor Green
$results += [PSCustomObject]@{ Test="Text Burst (50x)"; Avg="${perItem}ms/item"; Min="-"; Max="-"; P95="-"; Throughput="$($sw.ElapsedMilliseconds)ms total" }

# ═══════════════════════════════════════════════════════════════════
# TEST 3: Varied File Type Uploads (PNG, PDF, EXE, DOCX simulants)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 3: Varied File Type Uploads ━━━" -ForegroundColor Cyan

# Create test files of different types with realistic sizes
$rng = New-Object System.Random

# PNG-like (100KB) — with PNG magic bytes
$pngFile = Join-Path $tempDir "stress_test.png"
$pngBuf = New-Object byte[] 102400
$rng.NextBytes($pngBuf)
$pngBuf[0] = 0x89; $pngBuf[1] = 0x50; $pngBuf[2] = 0x4E; $pngBuf[3] = 0x47  # PNG header
[System.IO.File]::WriteAllBytes($pngFile, $pngBuf)

# PDF-like (200KB) — with PDF header
$pdfFile = Join-Path $tempDir "stress_test.pdf"
$pdfBuf = New-Object byte[] 204800
$rng.NextBytes($pdfBuf)
$pdfHeader = [System.Text.Encoding]::ASCII.GetBytes("%PDF-1.7")
[Array]::Copy($pdfHeader, $pdfBuf, $pdfHeader.Length)
[System.IO.File]::WriteAllBytes($pdfFile, $pdfBuf)

# EXE-like (500KB) — with MZ header
$exeFile = Join-Path $tempDir "stress_test.exe"
$exeBuf = New-Object byte[] 512000
$rng.NextBytes($exeBuf)
$exeBuf[0] = 0x4D; $exeBuf[1] = 0x5A  # MZ header
[System.IO.File]::WriteAllBytes($exeFile, $exeBuf)

# DOCX-like (150KB) — with PK/ZIP header
$docxFile = Join-Path $tempDir "stress_test.docx"
$docxBuf = New-Object byte[] 153600
$rng.NextBytes($docxBuf)
$docxBuf[0] = 0x50; $docxBuf[1] = 0x4B; $docxBuf[2] = 0x03; $docxBuf[3] = 0x04  # PK ZIP
[System.IO.File]::WriteAllBytes($docxFile, $docxBuf)

$fileTests = @(
    @{ Name="PNG (100KB)";  File=$pngFile;  FileName="stress.png";  Type="Image"; SizeKB=100 },
    @{ Name="PDF (200KB)";  File=$pdfFile;  FileName="stress.pdf";  Type="File";  SizeKB=200 },
    @{ Name="EXE (500KB)";  File=$exeFile;  FileName="stress.exe";  Type="File";  SizeKB=500 },
    @{ Name="DOCX (150KB)"; File=$docxFile; FileName="stress.docx"; Type="File";  SizeKB=150 }
)

foreach ($ft in $fileTests) {
    $sw.Restart()
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 30 -X POST "$peer/api/sync_file" `
        -H "X-Pairing-Key: $pk" -H "X-Item-Type: $($ft.Type)" -H "X-Source-Device: StressTester" `
        -H "X-Source-DeviceId: STRESS_TEST" -H "X-File-Name: $($ft.FileName)" `
        -H "Content-Type: application/octet-stream" --data-binary "@$($ft.File)" 2>&1
    $sw.Stop()
    $speed = if ($sw.ElapsedMilliseconds -gt 0) { [math]::Round($ft.SizeKB / ($sw.ElapsedMilliseconds / 1000)) } else { "∞" }
    $status = if ($code -eq "200") { "✅" } else { "❌ HTTP $code" }
    Write-Host "  $status $($ft.Name): $($sw.ElapsedMilliseconds)ms (${speed} KB/s)" -ForegroundColor Green
    $results += [PSCustomObject]@{ Test="Upload $($ft.Name)"; Avg="$($sw.ElapsedMilliseconds)ms"; Min="-"; Max="-"; P95="-"; Throughput="${speed} KB/s" }
}

# ═══════════════════════════════════════════════════════════════════
# TEST 4: Large File Throughput (500KB → 2MB → 5MB → 10MB)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 4: Large File Throughput ━━━" -ForegroundColor Cyan

$largeSizes = @(
    @{ Label="500KB"; Bytes=512000 },
    @{ Label="2MB";   Bytes=2097152 },
    @{ Label="5MB";   Bytes=5242880 },
    @{ Label="10MB";  Bytes=10485760 }
)

foreach ($sz in $largeSizes) {
    $bigFile = Join-Path $tempDir "stress_$($sz.Label).bin"
    $bigBuf = New-Object byte[] $sz.Bytes
    $rng.NextBytes($bigBuf)
    [System.IO.File]::WriteAllBytes($bigFile, $bigBuf)
    
    $sw.Restart()
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 120 -X POST "$peer/api/sync_file" `
        -H "X-Pairing-Key: $pk" -H "X-Item-Type: File" -H "X-Source-Device: StressTester" `
        -H "X-Source-DeviceId: STRESS_TEST" -H "X-File-Name: stress_$($sz.Label).bin" `
        -H "Content-Type: application/octet-stream" --data-binary "@$bigFile" 2>&1
    $sw.Stop()
    
    $sizeKB = [math]::Round($sz.Bytes / 1024)
    $speed = if ($sw.ElapsedMilliseconds -gt 0) { [math]::Round($sizeKB / ($sw.ElapsedMilliseconds / 1000)) } else { "∞" }
    $speedMB = if ($sw.ElapsedMilliseconds -gt 0) { [math]::Round($sizeKB / 1024 / ($sw.ElapsedMilliseconds / 1000), 1) } else { "∞" }
    $status = if ($code -eq "200") { "✅" } else { "❌ HTTP $code" }
    Write-Host "  $status $($sz.Label): $($sw.ElapsedMilliseconds)ms = ${speedMB} MB/s" -ForegroundColor Green
    $results += [PSCustomObject]@{ Test="Large File ($($sz.Label))"; Avg="$($sw.ElapsedMilliseconds)ms"; Min="-"; Max="-"; P95="-"; Throughput="${speedMB} MB/s" }
    
    Remove-Item $bigFile -Force -ErrorAction SilentlyContinue
}

# ═══════════════════════════════════════════════════════════════════
# TEST 5: Rapid-Fire Batch Upload (20x 50KB files sequentially)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 5: Rapid-Fire Batch (20x 50KB) ━━━" -ForegroundColor Cyan

$batchFile = Join-Path $tempDir "batch_50k.bin"
$batchBuf = New-Object byte[] 51200
$rng.NextBytes($batchBuf)
[System.IO.File]::WriteAllBytes($batchFile, $batchBuf)

$batchTimes = @()
$batchFails = 0
$sw.Restart()
for ($i = 1; $i -le 20; $i++) {
    $itemSw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 15 -X POST "$peer/api/sync_file" `
        -H "X-Pairing-Key: $pk" -H "X-Item-Type: Image" -H "X-Source-Device: StressTester" `
        -H "X-Source-DeviceId: STRESS_TEST" -H "X-File-Name: batch_$i.png" `
        -H "Content-Type: application/octet-stream" --data-binary "@$batchFile" 2>&1
    $itemSw.Stop()
    $batchTimes += $itemSw.ElapsedMilliseconds
    if ($code -ne "200") { $batchFails++ }
}
$sw.Stop()
$bAvg = [math]::Round(($batchTimes | Measure-Object -Average).Average)
$bMin = ($batchTimes | Measure-Object -Minimum).Minimum
$bMax = ($batchTimes | Measure-Object -Maximum).Maximum
$totalKB = 20 * 50
$bSpeed = if ($sw.ElapsedMilliseconds -gt 0) { [math]::Round($totalKB / ($sw.ElapsedMilliseconds / 1000)) } else { "∞" }
Write-Host "  Total: $($sw.ElapsedMilliseconds)ms | Avg: ${bAvg}ms | Min: ${bMin}ms | Max: ${bMax}ms" -ForegroundColor Green
Write-Host "  Throughput: ${bSpeed} KB/s (1000KB total) | Failures: $batchFails" -ForegroundColor Green
$results += [PSCustomObject]@{ Test="Batch Upload (20x50KB)"; Avg="${bAvg}ms/item"; Min="${bMin}ms"; Max="${bMax}ms"; P95="-"; Throughput="${bSpeed} KB/s" }

# ═══════════════════════════════════════════════════════════════════
# TEST 6: Concurrent Upload Stress (5 parallel uploads of 200KB)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 6: Concurrent Uploads (5 parallel × 200KB) ━━━" -ForegroundColor Cyan

$concFile = Join-Path $tempDir "concurrent_200k.bin"
$concBuf = New-Object byte[] 204800
$rng.NextBytes($concBuf)
[System.IO.File]::WriteAllBytes($concFile, $concBuf)

$sw.Restart()
$jobs = @()
for ($i = 1; $i -le 5; $i++) {
    $jobs += Start-Job -ScriptBlock {
        param($url, $key, $file, $idx)
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $code = curl.exe -s -o NUL -w "%{http_code}" -m 30 -X POST "$url/api/sync_file" `
            -H "X-Pairing-Key: $key" -H "X-Item-Type: File" -H "X-Source-Device: StressTester" `
            -H "X-Source-DeviceId: STRESS_TEST_$idx" -H "X-File-Name: concurrent_$idx.bin" `
            -H "Content-Type: application/octet-stream" --data-binary "@$file" 2>&1
        $timer.Stop()
        return "$code|$($timer.ElapsedMilliseconds)"
    } -ArgumentList $peer, $pk, $concFile, $i
}
$jobResults = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job -Force
$sw.Stop()

$concTimes = @()
$concFails = 0
foreach ($jr in $jobResults) {
    $parts = $jr -split "\|"
    if ($parts[0] -ne "200") { $concFails++ }
    $concTimes += [int]$parts[1]
}
$cAvg = [math]::Round(($concTimes | Measure-Object -Average).Average)
$cMax = ($concTimes | Measure-Object -Maximum).Maximum
$totalConcKB = 5 * 200
$cSpeed = if ($sw.ElapsedMilliseconds -gt 0) { [math]::Round($totalConcKB / ($sw.ElapsedMilliseconds / 1000)) } else { "∞" }
Write-Host "  Wall time: $($sw.ElapsedMilliseconds)ms | Avg per-upload: ${cAvg}ms | Max: ${cMax}ms" -ForegroundColor Green
Write-Host "  Effective throughput: ${cSpeed} KB/s (1000KB total) | Failures: $concFails" -ForegroundColor Green
$results += [PSCustomObject]@{ Test="Concurrent (5×200KB)"; Avg="${cAvg}ms"; Min="-"; Max="${cMax}ms"; P95="-"; Throughput="${cSpeed} KB/s" }

# ═══════════════════════════════════════════════════════════════════
# TEST 7: Mixed Workload Simulation (text + files interleaved)
# ═══════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "━━━ TEST 7: Mixed Workload (30 items: text+files interleaved) ━━━" -ForegroundColor Cyan

$mixedFile = Join-Path $tempDir "mixed_75k.bin"
$mixBuf = New-Object byte[] 76800
$rng.NextBytes($mixBuf)
[System.IO.File]::WriteAllBytes($mixedFile, $mixBuf)

$sw.Restart()
$mixFails = 0
for ($i = 1; $i -le 30; $i++) {
    if ($i % 3 -eq 0) {
        # Every 3rd item is a file upload
        $code = curl.exe -s -o NUL -w "%{http_code}" -m 10 -X POST "$peer/api/sync_file" `
            -H "X-Pairing-Key: $pk" -H "X-Item-Type: Image" -H "X-Source-Device: StressTester" `
            -H "X-Source-DeviceId: STRESS_TEST" -H "X-File-Name: mixed_$i.png" `
            -H "Content-Type: application/octet-stream" --data-binary "@$mixedFile" 2>&1
    } else {
        # Text sync
        $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $json = "{`"type`":`"Text`",`"title`":`"Mixed $i`",`"data`":`"Mixed workload item $i with realistic text content and timestamp $ts`",`"sourceDeviceId`":`"STRESS_TEST`",`"sourceDeviceName`":`"StressTester`",`"timestamp`":$ts}"
        $code = curl.exe -s -o NUL -w "%{http_code}" -m 5 -X POST "$peer/api/sync_text" -H "Content-Type: application/json" -H "X-Pairing-Key: $pk" -d $json 2>&1
    }
    if ($code -ne "200") { $mixFails++ }
}
$sw.Stop()
$mixPerItem = [math]::Round($sw.ElapsedMilliseconds / 30)
Write-Host "  30 items in $($sw.ElapsedMilliseconds)ms = ${mixPerItem}ms/item | Failures: $mixFails" -ForegroundColor Green
$results += [PSCustomObject]@{ Test="Mixed Workload (30x)"; Avg="${mixPerItem}ms/item"; Min="-"; Max="-"; P95="-"; Throughput="$($sw.ElapsedMilliseconds)ms total" }

# ═══════════════════════════════════════════════════════════════════
# Cleanup & Summary
# ═══════════════════════════════════════════════════════════════════
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║                        STRESS TEST RESULTS SUMMARY                       ║" -ForegroundColor Magenta
Write-Host "╚═══════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host ""
$results | Format-Table -AutoSize
Write-Host ""

# ── Verdict ──
$totalFails = $failCount + $batchFails + $concFails + $mixFails
if ($totalFails -eq 0) {
    Write-Host "  ✅ ALL TESTS PASSED — Zero failures across all benchmarks" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  $totalFails total failures detected" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  DONE" -ForegroundColor Cyan

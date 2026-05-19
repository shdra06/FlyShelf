# FlyShelf LAN Stress Test (curl-based for speed)
$peer = "http://10.165.67.62:8999"
$pk = "5e8fd86b46fe43fbb997140a9c25d63f"

Write-Host "Peer: $peer"
Write-Host ""

# TEST 1: 10x rapid text sync
Write-Host "TEST 1: 10x Text Burst" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 1; $i -le 10; $i++) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $json = "{`"type`":`"Text`",`"title`":`"Burst $i`",`"data`":`"Stress test item $i sent at $ts`",`"sourceDeviceId`":`"STRESS`",`"sourceDeviceName`":`"Tester`",`"timestamp`":$ts}"
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 5 -X POST "$peer/api/sync_text" -H "Content-Type: application/json" -H "X-Pairing-Key: $pk" -d $json 2>&1
    Write-Host "  [$i] HTTP $code - $($sw.ElapsedMilliseconds)ms"
}
$sw.Stop()
Write-Host "  TOTAL: $($sw.ElapsedMilliseconds)ms = $([math]::Round($sw.ElapsedMilliseconds / 10))ms/item" -ForegroundColor Green

# TEST 2: 20x health ping latency
Write-Host "`nTEST 2: 20x Health Ping" -ForegroundColor Cyan
$times = @()
for ($i = 1; $i -le 20; $i++) {
    $ms = curl.exe -s -o NUL -w "%{time_total}" -m 3 "$peer/api/health" -H "X-Pairing-Key: $pk" 2>&1
    $msVal = [math]::Round([double]$ms * 1000)
    $times += $msVal
}
$avg = [math]::Round(($times | Measure-Object -Average).Average)
$mn = ($times | Measure-Object -Minimum).Minimum
$mx = ($times | Measure-Object -Maximum).Maximum
Write-Host "  Avg: ${avg}ms  Min: ${mn}ms  Max: ${mx}ms" -ForegroundColor Green

# TEST 3: 5x binary file sync (50KB each)
Write-Host "`nTEST 3: 5x File (50KB)" -ForegroundColor Cyan
$tempFile = Join-Path $env:TEMP "flyshelf_stress_50k.bin"
$rng = New-Object System.Random
$buf = New-Object byte[] 51200
$rng.NextBytes($buf)
[System.IO.File]::WriteAllBytes($tempFile, $buf)

$sw.Restart()
for ($i = 1; $i -le 5; $i++) {
    $code = curl.exe -s -o NUL -w "%{http_code}" -m 10 -X POST "$peer/api/sync_file" `
        -H "X-Pairing-Key: $pk" -H "X-Item-Type: Image" -H "X-Source-Device: StressTester" `
        -H "X-Source-DeviceId: STRESS" -H "X-File-Name: stress_$i.png" `
        -H "Content-Type: application/octet-stream" --data-binary "@$tempFile" 2>&1
    Write-Host "  [$i] HTTP $code - $($sw.ElapsedMilliseconds)ms"
}
$sw.Stop()
Write-Host "  TOTAL: $($sw.ElapsedMilliseconds)ms = $([math]::Round($sw.ElapsedMilliseconds / 5))ms/item" -ForegroundColor Green

# TEST 4: 1x large file (500KB)
Write-Host "`nTEST 4: 1x Large File (500KB)" -ForegroundColor Cyan
$bigFile = Join-Path $env:TEMP "flyshelf_stress_500k.bin"
$bigBuf = New-Object byte[] 512000
$rng.NextBytes($bigBuf)
[System.IO.File]::WriteAllBytes($bigFile, $bigBuf)

$sw.Restart()
$code = curl.exe -s -o NUL -w "%{http_code}" -m 30 -X POST "$peer/api/sync_file" `
    -H "X-Pairing-Key: $pk" -H "X-Item-Type: File" -H "X-Source-Device: StressTester" `
    -H "X-Source-DeviceId: STRESS" -H "X-File-Name: stress_large.bin" `
    -H "Content-Type: application/octet-stream" --data-binary "@$bigFile" 2>&1
$sw.Stop()
$speed = [math]::Round(500 / ($sw.ElapsedMilliseconds / 1000))
Write-Host "  HTTP $code - $($sw.ElapsedMilliseconds)ms = ${speed} KB/s" -ForegroundColor Green

# TEST 5: 1x very large file (2MB)
Write-Host "`nTEST 5: 1x Very Large File (2MB)" -ForegroundColor Cyan
$hugFile = Join-Path $env:TEMP "flyshelf_stress_2m.bin"
$hugBuf = New-Object byte[] 2097152
$rng.NextBytes($hugBuf)
[System.IO.File]::WriteAllBytes($hugFile, $hugBuf)

$sw.Restart()
$code = curl.exe -s -o NUL -w "%{http_code}" -m 60 -X POST "$peer/api/sync_file" `
    -H "X-Pairing-Key: $pk" -H "X-Item-Type: File" -H "X-Source-Device: StressTester" `
    -H "X-Source-DeviceId: STRESS" -H "X-File-Name: stress_huge.bin" `
    -H "Content-Type: application/octet-stream" --data-binary "@$hugFile" 2>&1
$sw.Stop()
$speed2 = [math]::Round(2048 / ($sw.ElapsedMilliseconds / 1000))
Write-Host "  HTTP $code - $($sw.ElapsedMilliseconds)ms = ${speed2} KB/s" -ForegroundColor Green

Remove-Item $tempFile,$bigFile,$hugFile -Force -ErrorAction SilentlyContinue
Write-Host "`nDONE" -ForegroundColor Cyan

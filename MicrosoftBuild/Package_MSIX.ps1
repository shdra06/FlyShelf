$ErrorActionPreference = "Stop"

$projectDir = "e:\exeapps\FlyShelf\FlyShelf_PC"
$buildDir = "e:\exeapps\FlyShelf\MicrosoftBuild"
$outputDir = "$buildDir\Output"
$publishDir = "$projectDir\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$makeappx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.28000.0\x64\makeappx.exe"

Write-Host "1. Running dotnet publish..."
Set-Location $projectDir
dotnet clean
dotnet publish FlyShelf.csproj -c Release -r win-x64 -p:StorePublish=true -p:SelfContained=true -p:Platform=x64

# Ensure cloudflared.exe is bundled in the MSIX
$cfSource = "$buildDir\agent\cloudflared.exe"
if (Test-Path $cfSource) {
    $cfDest = "$publishDir\agent"
    New-Item -ItemType Directory -Force -Path $cfDest | Out-Null
    Copy-Item $cfSource "$cfDest\cloudflared.exe" -Force
    Write-Host "  Bundled cloudflared.exe ($([math]::Round((Get-Item $cfSource).Length/1MB, 1)) MB)"
} else {
    Write-Host "  WARNING: cloudflared.exe not found at $cfSource - MSIX will not include it"
}

Write-Host "2. Copying AppxManifest.xml..."
Copy-Item -Path "$buildDir\Package.appxmanifest" -Destination "$publishDir\AppxManifest.xml" -Force

Write-Host "3. Renaming Assets for strict MakeAppx matching..."
Set-Location "$publishDir\Assets"
Get-ChildItem -Filter "*.scale-100.png" | ForEach-Object {
    $newName = $_.Name.Replace(".scale-100", "")
    Copy-Item -Path $_.FullName -Destination $newName -Force
}

Write-Host "4. Packing with MakeAppx..."
Set-Location $outputDir
$msixPath = "$outputDir\FlyShelf_3.0.0.0_x64.msix"

if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

& $makeappx pack /nv /d $publishDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    Write-Host "MakeAppx failed!"
    exit 1
}

Write-Host "MSIX Packaging Complete: $msixPath"

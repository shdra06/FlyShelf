# Build_Sparse_Resource.ps1
# Automates the packaging and signing of the sparse package to be embedded in FlyShelf.exe

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir "..\FlyShelf_PC"
$resourcesDir = Join-Path $projectDir "Resources"

if (-not (Test-Path $resourcesDir)) {
    New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
}

$outputMsix = Join-Path $resourcesDir "FlyShelfSparse.msix"
$outputCer = Join-Path $resourcesDir "FlyShelfSparse.cer"

Write-Host "======================================================"
Write-Host "  FlyShelf - Building Sparse Embedded Resources"
Write-Host "======================================================"

# --- Step 1: Locate SDK Tools ---
$sdkBinPath = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeappx = $null
$signtool = $null
if (Test-Path $sdkBinPath) {
    $makeappx = (Get-ChildItem -Path $sdkBinPath -Filter "makeappx.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*\x64\*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $signtool = (Get-ChildItem -Path $sdkBinPath -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*\x64\*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

if (-not $makeappx -or -not (Test-Path $makeappx)) {
    Write-Host "  WARNING: makeappx.exe not found. Skipping sparse resource compilation." -ForegroundColor Yellow
    Write-Host "  (Ensure Windows SDK is installed to compile local AI sparse assets)" -ForegroundColor Yellow
    exit 0
}

if (-not $signtool -or -not (Test-Path $signtool)) {
    Write-Host "  WARNING: signtool.exe not found. Skipping sparse resource compilation." -ForegroundColor Yellow
    exit 0
}

# --- Step 2: Create Temp Staging Area ---
$tempDir = Join-Path $scriptDir "TempSparseStagingResource"
if (Test-Path $tempDir) {
    Remove-Item -Path $tempDir -Recurse -Force | Out-Null
}
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

# Copy assets
$tempAssets = Join-Path $tempDir "Assets"
New-Item -ItemType Directory -Path $tempAssets -Force | Out-Null
if (Test-Path (Join-Path $scriptDir "Assets")) {
    Copy-Item -Path (Join-Path $scriptDir "Assets\*") -Destination $tempAssets -Force
}

# Create Manifest
$manifestContent = '<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap10 rescap">

  <Identity
    Name="Flyshelf.FlyShelfSparse"
    Publisher="CN=FlyShelfWebsiteCert"
    Version="3.0.0.0"
    ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>FlyShelf Sparse</DisplayName>
    <PublisherDisplayName>FlyShelf Team</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Capabilities>
    <Capability Name="internetClient" />
    <Capability Name="privateNetworkClientServer" />
    <rescap:Capability Name="runFullTrust" />
    <rescap:Capability Name="systemAIModels" />
    <rescap:Capability Name="unvirtualizedResources" />
  </Capabilities>

  <Applications>
    <Application Id="FlyShelf"
                 Executable="FlyShelf.exe"
                 uap10:TrustLevel="mediumIL"
                 uap10:RuntimeBehavior="win32App">
      <uap:VisualElements
        DisplayName="FlyShelf"
        Description="Seamless Clipboard Sync - Sparse Package"
        BackgroundColor="#1A1A2E"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:SplashScreen Image="Assets\SplashScreen.png" BackgroundColor="#1a1a2e"/>
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>'

$manifestPath = Join-Path $tempDir "AppxManifest.xml"
Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8

# --- Step 3: Compile Sparse MSIX ---
if (Test-Path $outputMsix) { Remove-Item $outputMsix -Force }
& $makeappx pack /d $tempDir /p $outputMsix /nv
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ERROR: makeappx pack failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit 1
}
Write-Host "  MSIX Package compiled: $outputMsix"

# --- Step 4: Create or Retrieve Developer Certificate ---
$certSubject = "CN=FlyShelfWebsiteCert"
$cert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1

if (-not $cert) {
    Write-Host "  Creating new self-signed user certificate..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature -FriendlyName "FlyShelf Sparse Cert" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}

# Export public certificate
Export-Certificate -Cert $cert -FilePath $outputCer -Force | Out-Null
Write-Host "  Exported Certificate: $outputCer"

# --- Step 5: Sign the MSIX Package ---
& $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $outputMsix
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ERROR: signtool failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit 1
}
Write-Host "  Signed sparse MSIX package."

# Clean up temp staging directory
if (Test-Path $tempDir) {
    Remove-Item -Path $tempDir -Recurse -Force | Out-Null
}

Write-Host "  Sparse package assets generated successfully." -ForegroundColor Green
Write-Host ""

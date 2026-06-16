#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates and registers a Sparse Package for the development build of FlyShelf.
    
.DESCRIPTION
    This script:
    1. Locates Windows SDK tools (makeappx.exe and signtool.exe).
    2. Stages a sparse AppxManifest.xml and visual assets in a temporary folder.
    3. Packages the sparse manifest into an MSIX.
    4. Generates a self-signed certificate, installs it to 'Trusted People', and signs the MSIX.
    5. Registers the signed MSIX as a Sparse Package pointing to the FlyShelf bin/Debug folder.
    6. Enables you to run the development FlyShelf.exe directly with Windows package identity and local AI enabled.

.NOTES
    Must be run as Administrator (installing certificates and registering packages requires elevation).
    Run: powershell -ExecutionPolicy Bypass -File Register_Sparse.ps1
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectBinDir = Join-Path $scriptDir "..\FlyShelf_PC\bin\Debug\net10.0-windows10.0.19041.0"
$projectBinDir = [System.IO.Path]::GetFullPath($projectBinDir)

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  FlyShelf — Sparse Package Dev Registration" -ForegroundColor Cyan  
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $projectBinDir)) {
    Write-Host "  ERROR: Project output directory not found at:" -ForegroundColor Red
    Write-Host "  $projectBinDir" -ForegroundColor Red
    Write-Host "  Please run run.bat first to build the project." -ForegroundColor Red
    exit 1
}

# ─── Step 1: Locate SDK Tools ───
Write-Host "[1/6] Locating Windows SDK tools..." -ForegroundColor Yellow

$sdkBinPath = "C:\Program Files (x86)\Windows Kits\10\bin"
if (-not (Test-Path $sdkBinPath)) {
    Write-Host "  ERROR: Windows SDK folder not found at $sdkBinPath" -ForegroundColor Red
    Write-Host "  Please install the Windows 10/11 SDK." -ForegroundColor Red
    exit 1
}

$makeappx = (Get-ChildItem -Path $sdkBinPath -Filter "makeappx.exe" -Recurse | Where-Object { $_.FullName -like "*\x64\*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$signtool = (Get-ChildItem -Path $sdkBinPath -Filter "signtool.exe" -Recurse | Where-Object { $_.FullName -like "*\x64\*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName

if (-not $makeappx -or -not (Test-Path $makeappx)) {
    Write-Host "  ERROR: makeappx.exe not found in Windows Kits." -ForegroundColor Red
    exit 1
}
if (-not $signtool -or -not (Test-Path $signtool)) {
    Write-Host "  ERROR: signtool.exe not found in Windows Kits." -ForegroundColor Red
    exit 1
}

Write-Host "  Found makeappx: $makeappx" -ForegroundColor Green
Write-Host "  Found signtool: $signtool" -ForegroundColor Green
Write-Host ""

# ─── Step 2: Create Temp Staging Area ───
Write-Host "[2/6] Staging sparse manifest and assets..." -ForegroundColor Yellow

$tempDir = Join-Path $scriptDir "TempSparseStaging"
if (Test-Path $tempDir) {
    Remove-Item -Path $tempDir -Recurse -Force | Out-Null
}
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

# Copy assets
$tempAssets = Join-Path $tempDir "Assets"
New-Item -ItemType Directory -Path $tempAssets -Force | Out-Null
Copy-Item -Path (Join-Path $scriptDir "Assets\*") -Destination $tempAssets -Force

# Create Manifest
$manifestContent = @"
<?xml version="1.0" encoding="utf-8"?>
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
    <AllowExternalContent>true</AllowExternalContent>
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
        DisplayName="FlyShelf Sparse"
        Description="Seamless Clipboard Sync - Sparse Package"
        BackgroundColor="#1A1A2E"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:SplashScreen Image="Assets\SplashScreen.png" BackgroundColor="#1a1a2e"/>
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>
"@

$manifestPath = Join-Path $tempDir "AppxManifest.xml"
Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8
Write-Host "  Staged sparse AppxManifest.xml and assets successfully." -ForegroundColor Green
Write-Host ""

# ─── Step 3: Package Sparse MSIX ───
Write-Host "[3/6] Compiling sparse MSIX package..." -ForegroundColor Yellow
$outputMsix = Join-Path $scriptDir "Output\FlyShelfSparse.msix"

if (-not (Test-Path (Split-Path $outputMsix))) {
    New-Item -ItemType Directory -Path (Split-Path $outputMsix) -Force | Out-Null
}
if (Test-Path $outputMsix) {
    Remove-Item -Path $outputMsix -Force | Out-Null
}

# Run makeappx with /nv flag (no validation, since binaries are external)
& $makeappx pack /d $tempDir /p $outputMsix /nv
Write-Host "  MSIX Package compiled: $outputMsix" -ForegroundColor Green
Write-Host ""

# ─── Step 4: Create & Install Self-Signed Developer Certificate ───
Write-Host "[4/6] Creating self-signed certificate..." -ForegroundColor Yellow

$certSubject = "CN=FlyShelfWebsiteCert"
$cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1

if (-not $cert) {
    Write-Host "  Creating new self-signed certificate..." -ForegroundColor DarkGray
    $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature -FriendlyName "FlyShelf Sparse Cert" -CertStoreLocation "Cert:\LocalMachine\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}

# Export and install into Trusted People to verify signature locally
$certPath = Join-Path $scriptDir "FlyShelfWebsiteCert.cer"
Export-Certificate -Cert $cert -FilePath $certPath -Force | Out-Null

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "LocalMachine")
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$existingCerts = $store.Certificates | Where-Object { $_.Subject -eq $certSubject }
if (-not $existingCerts) {
    $x509Cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)
    $store.Add($x509Cert)
    Write-Host "  Installed certificate into Trusted People." -ForegroundColor Green
} else {
    Write-Host "  Certificate already trusted in Trusted People." -ForegroundColor Green
}
$store.Close()

# Clean up temp exported certificate file
if (Test-Path $certPath) { Remove-Item $certPath -Force }
Write-Host ""

# ─── Step 5: Sign the Sparse Package ───
Write-Host "[5/6] Signing MSIX package..." -ForegroundColor Yellow
& $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $outputMsix
Write-Host "  MSIX signed successfully." -ForegroundColor Green
Write-Host ""

# ─── Step 6: Register the Sparse Package ───
Write-Host "[6/6] Registering Sparse Package to OS..." -ForegroundColor Yellow

# Clean up any existing registration of the package
$packageFamily = "Flyshelf.FlyShelfSparse_3wcvhmdw97v74" # Computed family name based on identity name and publisher hash
$registered = Get-AppxPackage -Name "Flyshelf.FlyShelfSparse"
if ($registered) {
    Write-Host "  Unregistering existing sparse package..." -ForegroundColor DarkGray
    Remove-AppxPackage -Package $registered.PackageFullName
}

# Register package pointing to the actual external location (bin/Debug folder)
Add-AppxPackage -Path $outputMsix -ExternalLocation $projectBinDir
Write-Host "  SUCCESS! Sparse Package registered successfully." -ForegroundColor Green
Write-Host "  External Folder: $projectBinDir" -ForegroundColor DarkGray
Write-Host ""

# Clean up temp staging directory
Remove-Item -Path $tempDir -Recurse -Force | Out-Null

Write-Host "======================================================" -ForegroundColor Green
Write-Host "  Standalone build is now registered with Package Identity!" -ForegroundColor Green
Write-Host "  You can launch FlyShelf.exe directly or using run.bat," -ForegroundColor Green
Write-Host "  and local AI features (Phi Silica) will be fully enabled!" -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green

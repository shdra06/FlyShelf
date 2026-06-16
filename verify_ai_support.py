import sys
import os
import platform
import subprocess
import json

# ANSI color codes for rich console output
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
CYAN = "\033[96m"
BOLD = "\033[1m"
RESET = "\033[0m"

# Enable VT100 console mode on Windows for color support
if platform.system() == "Windows":
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        kernel32.SetConsoleMode(kernel32.GetStdHandle(-11), 7)
    except Exception:
        pass

def get_windows_build():
    try:
        val = platform.version()
        parts = val.split('.')
        if len(parts) >= 3:
            return int(parts[2])
    except Exception:
        pass
    return 0

def check_gpus():
    gpus = []
    try:
        cmd = 'powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name"'
        output = subprocess.check_output(cmd, shell=True, text=True, stderr=subprocess.DEVNULL).strip()
        if output:
            gpus = [line.strip() for line in output.split('\n') if line.strip()]
    except Exception:
        pass
    return gpus

def check_windows_app_sdk():
    packages = []
    try:
        cmd = 'powershell -NoProfile -Command "Get-AppxPackage -Name *WindowsAppRuntime* | Select-Object Name, Version | ConvertTo-Json"'
        output = subprocess.check_output(cmd, shell=True, text=True, stderr=subprocess.DEVNULL).strip()
        if output:
            data = json.loads(output)
            if isinstance(data, dict):
                packages.append(data)
            elif isinstance(data, list):
                packages.extend(data)
    except Exception:
        pass
    return packages

def check_ai_packages():
    packages = []
    try:
        cmd = 'powershell -NoProfile -Command "Get-AppxPackage -Name *CoreAI* | Select-Object Name, Version | ConvertTo-Json"'
        output = subprocess.check_output(cmd, shell=True, text=True, stderr=subprocess.DEVNULL).strip()
        if output:
            data = json.loads(output)
            if isinstance(data, dict):
                packages.append(data)
            elif isinstance(data, list):
                packages.extend(data)
    except Exception:
        pass
    return packages

def check_winrt_type_status():
    try:
        cmd = 'powershell -NoProfile -Command "[Windows.Foundation.Metadata.ApiInformation, Windows.Foundation, ContentType=WindowsRuntime]::IsTypePresent(\'Microsoft.Windows.AI.Text.LanguageModel\')"'
        process = subprocess.Popen(cmd, shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        stdout, stderr = process.communicate()
        
        if "0x80073D54" in stderr or "package identity" in stderr:
            return "PRESENT_REQUIRES_IDENTITY"
        elif "True" in stdout:
            return "PRESENT_ACTIVE"
        elif "False" in stdout:
            return "ABSENT"
        else:
            return f"UNKNOWN ({stderr.strip()})"
    except Exception as e:
        return f"ERROR ({str(e)})"

def check_certificate_store(store_location):
    """Checks if CN=FlyShelfWebsiteCert is installed in TrustedPeople store."""
    try:
        cmd = f'powershell -NoProfile -Command "Get-ChildItem Cert:\\{store_location}\\TrustedPeople | Where-Object {{ $_.Subject -eq \'CN=FlyShelfWebsiteCert\' }} | Select-Object Thumbprint, Subject | ConvertTo-Json"'
        output = subprocess.check_output(cmd, shell=True, text=True, stderr=subprocess.DEVNULL).strip()
        if output:
            data = json.loads(output)
            if isinstance(data, dict) and "Thumbprint" in data:
                return data
            elif isinstance(data, list) and len(data) > 0:
                return data[0]
    except Exception:
        pass
    return None

def check_sparse_registration():
    """Checks if Flyshelf.FlyShelfSparse package is registered."""
    try:
        cmd = 'powershell -NoProfile -Command "Get-AppxPackage -Name Flyshelf.FlyShelfSparse | Select-Object PackageFullName, InstallLocation | ConvertTo-Json"'
        output = subprocess.check_output(cmd, shell=True, text=True, stderr=subprocess.DEVNULL).strip()
        if output:
            data = json.loads(output)
            if isinstance(data, dict) and "PackageFullName" in data:
                return data
    except Exception:
        pass
    return None

def main():
    print(f"{BOLD}{CYAN}======================================================{RESET}")
    print(f"{BOLD}{CYAN}      FlyShelf Local AI System Diagnostic Tool        {RESET}")
    print(f"{BOLD}{CYAN}======================================================{RESET}\n")

    # 1. OS Check
    os_name = platform.system()
    os_version = platform.release()
    build = get_windows_build()
    
    print(f"[*] {BOLD}Checking Windows OS Version:{RESET}")
    print(f"    - System:  {os_name}")
    print(f"    - Release: {os_version}")
    print(f"    - Build:   {build}")
    
    os_ok = (os_name == "Windows" and build >= 26100)
    if os_ok:
        print(f"    {GREEN}[+] OS Version: Supported (Windows 11 24H2+ / Build >= 26100){RESET}")
    else:
        print(f"    {RED}[-] OS Version: NOT fully supported. (Requires Windows 11 Build >= 26100 for Windows Copilot Runtime){RESET}")
    print("")

    # 2. Hardware Check
    print(f"[*] {BOLD}Checking Hardware Configuration:{RESET}")
    gpus = check_gpus()
    
    has_rtx = False
    for gpu in gpus:
        print(f"    - GPU: {gpu}")
        if "RTX" in gpu.upper():
            has_rtx = True
            
    hw_ok = has_rtx
    if hw_ok:
        print(f"    {GREEN}[+] Hardware acceleration: Supported (NVIDIA RTX GPU detected){RESET}")
    else:
        print(f"    {RED}[-] Hardware acceleration: NOT detected. (Requires RTX 30+ GPU or Copilot+ NPU){RESET}")
    print("")

    # 3. Windows App SDK Runtime Check
    print(f"[*] {BOLD}Checking Windows App SDK Runtime:{RESET}")
    sdk_packages = check_windows_app_sdk()
    if sdk_packages:
        print(f"    {GREEN}[+] Windows App SDK Runtime is installed:{RESET}")
        for pkg in sdk_packages[:3]: # limit print to 3
            print(f"        - {pkg.get('Name')} (v{pkg.get('Version')})")
        if len(sdk_packages) > 3:
            print(f"        - ...and {len(sdk_packages) - 3} other versions")
    else:
        print(f"    {RED}[-] Windows App SDK Runtime: None found. (Run the app or install Windows App Runtime 1.6+){RESET}")
    print("")

    # 4. Windows AI Packages Check
    print(f"[*] {BOLD}Checking Windows System AI Components:{RESET}")
    ai_packages = check_ai_packages()
    if ai_packages:
        print(f"    {GREEN}[+] System AI Packages found:{RESET}")
        for pkg in ai_packages:
            print(f"        - {pkg.get('Name')} (v{pkg.get('Version')})")
    else:
        print(f"    {RED}[-] System AI Packages: None found. (MicrosoftWindows.Client.CoreAI is missing. Ensure your OS has Windows Copilot/AI features enabled. Download via Windows Update or Features on Demand){RESET}")
    print("")

    # 5. Metadata Projection Check
    print(f"[*] {BOLD}Checking WinRT Local AI API Availability:{RESET}")
    type_status = check_winrt_type_status()
    if type_status == "PRESENT_REQUIRES_IDENTITY":
        print(f"    {GREEN}[+] Local AI API (Phi Silica): Available in OS, but requires Package Identity (MSIX context) to activate.{RESET}")
    elif type_status == "PRESENT_ACTIVE":
        print(f"    {GREEN}[+] Local AI API (Phi Silica): Available and active.{RESET}")
    elif type_status == "ABSENT":
        print(f"    {RED}[-] Local AI API (Phi Silica): NOT available in current Windows installation.{RESET}")
    else:
        print(f"    {YELLOW}[?] Local AI API Status: {type_status}{RESET}")
    print("")

    # 6. Certificate Trust Check
    print(f"[*] {BOLD}Checking Developer Certificate Trust:{RESET}")
    lm_cert = check_certificate_store("LocalMachine")
    cu_cert = check_certificate_store("CurrentUser")
    
    cert_ok = False
    if lm_cert:
        print(f"    {GREEN}[+] Certificate 'CN=FlyShelfWebsiteCert' is trusted machine-wide (LocalMachine\\TrustedPeople).{RESET}")
        cert_ok = True
    elif cu_cert:
        print(f"    {YELLOW}[!] Certificate 'CN=FlyShelfWebsiteCert' is only trusted user-wide (CurrentUser\\TrustedPeople).{RESET}")
        print(f"        (Note: AppX registration may fail if not trusted machine-wide. LocalMachine is recommended.){RESET}")
        cert_ok = True
    else:
        print(f"    {RED}[-] Certificate 'CN=FlyShelfWebsiteCert' is NOT found in any Trusted People stores.{RESET}")
    print("")

    # 7. Sparse Package Registration Check
    print(f"[*] {BOLD}Checking Sparse Package Registration:{RESET}")
    sparse_reg = check_sparse_registration()
    if sparse_reg:
        print(f"    {GREEN}[+] Sparse Package is registered:{RESET}")
        print(f"        - Package Name: Flyshelf.FlyShelfSparse")
        print(f"        - Full Name:    {sparse_reg.get('PackageFullName')}")
        print(f"        - External Dir: {sparse_reg.get('InstallLocation')}")
        sparse_ok = True
    else:
        print(f"    {RED}[-] Sparse Package 'Flyshelf.FlyShelfSparse' is NOT registered in Windows.{RESET}")
        sparse_ok = False
    print("")

    # Conclusion
    print(f"{BOLD}{CYAN}======================================================{RESET}")
    print(f"{BOLD}{CYAN}                     DIAGNOSTIC SUMMARY               {RESET}")
    print(f"{BOLD}{CYAN}======================================================{RESET}")
    
    if not os_ok:
        print(f"{RED}{BOLD}FAIL: OS NOT SUPPORTED{RESET}")
        print(" -> Local AI requires Windows 11 Build 26100+ (Version 24H2 or newer).")
        print(" -> Action: Please update Windows to 24H2 or join the Windows Insider Program.")
    elif not hw_ok:
        print(f"{RED}{BOLD}FAIL: HARDWARE NOT SUPPORTED{RESET}")
        print(" -> Local AI requires an NVIDIA GeForce RTX 30+ series GPU or a Copilot+ NPU (e.g. Snapdragon X Elite).")
        print(" -> Action: Ensure you are running on a machine with a supported GPU/NPU, and NVIDIA drivers are up to date.")
    elif not ai_packages:
        print(f"{RED}{BOLD}FAIL: SYSTEM AI COMPONENTS MISSING{RESET}")
        print(" -> The 'MicrosoftWindows.Client.CoreAI' OS package is missing or disabled.")
        print(" -> Action: Enable Windows Copilot/AI features in Settings, or run Windows Update to download the system model components.")
    elif not cert_ok or not sparse_ok:
        print(f"{YELLOW}{BOLD}WARNING: ENVIRONMENT REQUIRES REGISTRATION{RESET}")
        print(" -> Your laptop supports local AI! However, the standalone .exe is missing its Package Identity.")
        print(" -> Registration failed because the developer certificate is not trusted machine-wide.")
        print(f"\n{BOLD}HOW TO FIX THIS NOW:{RESET}")
        print(f" 1. Go to the project directory: {BOLD}MicrosoftBuild\\{RESET}")
        print(f" 2. Right-click {BOLD}Register_Sparse.bat{RESET} and select {BOLD}Run as Administrator{RESET}.")
        print(" 3. This will trust the cert, sign the package, and register it to the OS.")
        print(" 4. Launch FlyShelf.exe (or run.bat) again.")
    else:
        print(f"{GREEN}{BOLD}SUCCESS: SYSTEM FULLY READY{RESET}")
        print(" -> Your laptop supports local AI, the certificate is trusted, and the sparse package is registered.")
        print(" -> Launch FlyShelf.exe directly or via run.bat. It will run with package identity and AI active.")

    print(f"{BOLD}{CYAN}======================================================{RESET}")

if __name__ == "__main__":
    main()

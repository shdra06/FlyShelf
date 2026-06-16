import sys
import os
import platform
import subprocess
import json

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
        # Use PowerShell CIM instance since wmic is deprecated/removed in Win11 24H2+
        cmd = 'powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name"'
        output = subprocess.check_output(cmd, shell=True, text=True).strip()
        if output:
            gpus = [line.strip() for line in output.split('\n') if line.strip()]
    except Exception:
        pass
    return gpus

def check_windows_app_sdk():
    packages = []
    try:
        cmd = 'powershell -NoProfile -Command "Get-AppxPackage -Name *WindowsAppRuntime* | Select-Object Name, Version | ConvertTo-Json"'
        output = subprocess.check_output(cmd, shell=True, text=True).strip()
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
        output = subprocess.check_output(cmd, shell=True, text=True).strip()
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
        # Attempt to run IsTypePresent. It will throw HRESULT 0x80073D54 if package identity is missing,
        # but if the type is completely absent in the OS it will return False/different exception.
        cmd = 'powershell -NoProfile -Command "[Windows.Foundation.Metadata.ApiInformation, Windows.Foundation, ContentType=WindowsRuntime]::IsTypePresent(\'Microsoft.Windows.AI.Text.LanguageModel\')"'
        process = subprocess.Popen(cmd, shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        stdout, stderr = process.communicate()
        
        if "0x80073D54" in stderr or "package identity" in stderr:
            return "PRESENT (Requires Package Identity / MSIX context to instantiate)"
        elif "True" in stdout:
            return "PRESENT & ACTIVE"
        elif "False" in stdout:
            return "ABSENT (Not supported by current OS build)"
        else:
            return f"UNKNOWN ({stderr.strip()})"
    except Exception as e:
        return f"ERROR ({str(e)})"

def main():
    print("======================================================")
    print("      Windows Copilot Runtime Local AI Test Tool      ")
    print("======================================================")
    print("")

    # 1. OS Check
    os_name = platform.system()
    os_version = platform.release()
    build = get_windows_build()
    
    print(f"[*] Checking OS:")
    print(f"    - System:  {os_name}")
    print(f"    - Release: {os_version}")
    print(f"    - Build:   {build}")
    
    os_ok = (os_name == "Windows" and build >= 26100)
    if os_ok:
        print("    [+] OS Version: Supported (Windows 11 24H2+ / Build >= 26100)")
    else:
        print("    [-] OS Version: NOT fully supported. (Requires Windows 11 Build >= 26100)")
        
    print("")

    # 2. Hardware Check
    print(f"[*] Checking Hardware:")
    gpus = check_gpus()
    
    has_rtx = False
    for gpu in gpus:
        print(f"    - GPU: {gpu}")
        if "RTX" in gpu:
            has_rtx = True
            
    hw_ok = has_rtx
    if hw_ok:
        print("    [+] Hardware acceleration: Supported (RTX GPU detected)")
    else:
        print("    [-] Hardware acceleration: NOT detected. (Requires RTX 30+ GPU or Copilot+ NPU)")
        
    print("")

    # 3. Windows App SDK Runtime Check
    print(f"[*] Checking Windows App SDK Runtime packages:")
    sdk_packages = check_windows_app_sdk()
    if sdk_packages:
        for pkg in sdk_packages:
            print(f"    - Found: {pkg.get('Name')} (v{pkg.get('Version')})")
    else:
        print("    - None found.")
    print("")

    # 4. Windows AI Packages Check
    print(f"[*] Checking Windows System AI Packages:")
    ai_packages = check_ai_packages()
    if ai_packages:
        for pkg in ai_packages:
            print(f"    - Found: {pkg.get('Name')} (v{pkg.get('Version')})")
    else:
        print("    - None found.")
    print("")

    # 5. Metadata Projection Check
    print(f"[*] Checking WinRT 'Microsoft.Windows.AI.Text.LanguageModel' Metadata:")
    type_status = check_winrt_type_status()
    print(f"    - Status: {type_status}")
    print("")

    # Conclusion
    print("======================================================")
    print("                       CONCLUSION                     ")
    print("======================================================")
    if os_ok and hw_ok:
        print("Your PC meets the hardware and OS requirements for local AI.")
        if not sdk_packages:
            print("  -> However, Windows App SDK Runtime is missing. Install Windows App SDK Runtime.")
        if "PRESENT" not in type_status:
            print("  -> Windows Copilot AI system components are missing. Make sure your OS has Copilot enabled.")
        else:
            print("Your system is ready. Run the FlyShelf standalone .exe, which will now automatically")
            print("register the signed sparse package on startup and activate the AI features.")
    else:
        if not os_ok:
            print("Please update to Windows 11 Build 26100+ (24H2) or join Insider Preview.")
        if not hw_ok:
            print("Ensure you are running on a machine with an NVIDIA RTX 30+ series GPU or Copilot+ NPU.")
    print("======================================================")

if __name__ == "__main__":
    main()

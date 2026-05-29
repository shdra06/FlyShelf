import os
import sys

# Reconfigure stdout to support UTF-8
try:
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

log_path = os.path.expandvars(r'%APPDATA%\FlyShelf\Logs\activity_log.txt')
if os.path.exists(log_path):
    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
    
    matches = []
    for line in lines:
        if any(tag in line for tag in ["[SUMMON]", "[SUMMON_FAIL]", "[TELEMETRY]", "[HOTKEY]"]):
            matches.append(line.strip())
            
    print(f"Total summon/hotkey matches: {len(matches)}")
    print("--- Last 100 Summon / Dismiss / Hotkey Logs ---")
    for line in matches[-100:]:
        try:
            print(line)
        except Exception:
            print(line.encode('ascii', errors='replace').decode('ascii'))
else:
    print(f"Log path does not exist: {log_path}")

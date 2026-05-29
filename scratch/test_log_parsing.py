import os
import sys

# Force stdout to use utf-8
try:
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

LOG_PATH = os.path.expandvars(r'%APPDATA%\FlyShelf\Logs\activity_log.txt')
if os.path.exists(LOG_PATH):
    with open(LOG_PATH, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
    print("Total lines in log file:", len(lines))
    print("--- LAST 30 LINES ---")
    for line in lines[-30:]:
        print(line.strip())
else:
    print("Not found")

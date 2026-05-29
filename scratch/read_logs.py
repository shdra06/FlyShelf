import os
import sys

# Reconfigure stdout to support UTF-8 characters
try:
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

log_path = os.path.expandvars(r'%APPDATA%\FlyShelf\Logs\activity_log.txt')
if os.path.exists(log_path):
    with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
    print(f"Total lines in log: {len(lines)}")
    # Print the last 150 lines
    for line in lines[-150:]:
        try:
            print(line.strip())
        except Exception:
            # Fallback for console encoding issues
            print(line.encode('ascii', errors='replace').decode('ascii').strip())
else:
    print(f"Log path does not exist: {log_path}")

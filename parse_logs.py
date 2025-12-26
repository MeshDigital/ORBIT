import json
import os

log_file = 'logs/log20251226.json'

if not os.path.exists(log_file):
    print("Log file not found")
    exit()

with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

print(f"Total lines: {len(lines)}")
print("Last 10 Errors/Warnings with 'Spotify':")

count = 0
for line in reversed(lines):
    if count >= 10: break
    if '"@l":"Error"' in line or '"@l":"Warning"' in line:
        try:
            entry = json.loads(line)
            msg = entry.get('@mt', '')
            if 'Spotify' in msg:
                print(f"[{entry.get('@t')}] {entry.get('@l')}: {msg}")
                if 'Reason' in msg: # Specific check for my new log
                     # Extract reason if it's parametrized
                     pass 
                if '@x' in entry:
                    print(f"   Exception: {entry['@x'][:200]}...") # First 200 chars
                count += 1
        except:
            pass

@echo off
"C:\Program Files\Git\cmd\git.exe" add -A
"C:\Program Files\Git\cmd\git.exe" commit -m "feat(library): optimize column layout for better visual hierarchy" -m "- Moved Status column to position #2 (right after Album Art)" -m "- Moved Metadata Status column to position #3" -m "- Critical download information now visible without scrolling" -m "- Improved scannability of library view"
"C:\Program Files\Git\cmd\git.exe" log -1 --oneline

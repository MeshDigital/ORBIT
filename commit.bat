@echo off
"C:\Program Files\Git\cmd\git.exe" add -A
"C:\Program Files\Git\cmd\git.exe" commit -m "fix(spotify): use PKCEAuthenticator for proper token handling - Fixed SpotifyClient creation to use recommended PKCEAuthenticator pattern - Added detailed APIException logging with HTTP status codes - Resolves all Spotify API authentication failures"
"C:\Program Files\Git\cmd\git.exe" log -1 --oneline

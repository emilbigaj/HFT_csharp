@echo off
rem The risk desk's double-click: starts this folder's Remote Mirror unless it already runs (a Mirror run from elsewhere does
rem not count), then the Workspace, which waits for the Mirror's copy of the server and opens the windows WorkspaceConfig.json
rem lists (see Spec.md "Risk desk package"). The Mirror reads MirrorConfig.json from this folder, its working directory.
cd /d "%~dp0"
powershell -NoProfile -Command "if (-not (Get-Process Mirror -ErrorAction SilentlyContinue | Where-Object Path -eq '%~dp0Mirror.exe')) { Start-Process '%~dp0Mirror.exe' -WorkingDirectory '%~dp0' }"
start "Workspace" "%~dp0Workspace.exe"

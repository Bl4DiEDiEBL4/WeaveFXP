@echo off
setlocal
rem Build in staging first; only the requested release executable can block publishing.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %errorlevel%

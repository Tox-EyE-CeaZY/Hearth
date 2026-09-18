@echo off
rem Checks widget work (see tools\check-widgets.ps1). It must end with PASSED.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-widgets.ps1" %*
pause

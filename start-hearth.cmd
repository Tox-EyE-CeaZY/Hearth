@echo off
rem Double-click to build and (re)start Hearth. Arguments pass through, e.g. -NoBuild.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-hearth.ps1" %*
if errorlevel 1 pause

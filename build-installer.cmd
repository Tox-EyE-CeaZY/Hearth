@echo off
rem Double-click to build the Hearth installer (artifacts\installer).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
if errorlevel 1 pause

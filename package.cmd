@echo off
rem Double-click wrapper. Real work lives in package.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" %*
if errorlevel 1 pause

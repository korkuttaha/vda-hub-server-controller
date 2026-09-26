@echo off
chcp 65001 >nul
echo ==========================================================
echo   VDA Hub Server Controller - Derleme Başlatılıyor...
echo ==========================================================

powershell.exe -ExecutionPolicy Bypass -File "%~dp0build.ps1"

pause

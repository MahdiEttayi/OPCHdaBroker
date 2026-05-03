@echo off
echo ═══════════════════════════════════════════════════
echo   OPC HDA Broker — Windows Service Uninstaller
echo ═══════════════════════════════════════════════════
echo.

set SERVICE_NAME=OpcHdaBroker

sc stop %SERVICE_NAME%
timeout /t 3 /nobreak >nul
sc delete %SERVICE_NAME%

echo.
echo   ✓  Service removed.
pause

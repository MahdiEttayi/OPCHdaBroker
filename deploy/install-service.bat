@echo off
echo ═══════════════════════════════════════════════════
echo   OPC HDA Broker — Windows Service Installer
echo ═══════════════════════════════════════════════════
echo.

set SERVICE_NAME=OpcHdaBroker
set DISPLAY_NAME=OPC HDA Broker
set DESCRIPTION=REST API proxy for KepServerEX Local Historian (OPC HDA)
set EXE_PATH=%~dp0OpcHdaBroker.exe

echo   Service Name : %SERVICE_NAME%
echo   Executable   : %EXE_PATH%
echo.

:: Create the service
sc create %SERVICE_NAME% binPath= "%EXE_PATH%" DisplayName= "%DISPLAY_NAME%" start= auto
sc description %SERVICE_NAME% "%DESCRIPTION%"
sc failure %SERVICE_NAME% reset= 86400 actions= restart/5000/restart/10000/restart/30000

echo.
echo   ✓  Service created. Start with:
echo      sc start %SERVICE_NAME%
echo.
echo   Note: If listening on all interfaces (http://+:5000),
echo   run this first (as Admin):
echo      netsh http add urlacl url=http://+:5000/ user=Everyone
echo.
pause

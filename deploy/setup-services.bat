@echo off
:: ═══════════════════════════════════════════════════════════════
::  OPC HDA Broker — Service Dependencies Setup
::  Run this script AS ADMINISTRATOR to configure all services
::  the broker depends on to start automatically on boot.
:: ═══════════════════════════════════════════════════════════════

echo.
echo   ╔═══════════════════════════════════════════════════════╗
echo   ║  OPC HDA Broker — Service Dependencies Setup         ║
echo   ╚═══════════════════════════════════════════════════════╝
echo.

:: ── Check for admin privileges ────────────────────────────────
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo   [ERROR] This script must be run as Administrator!
    echo   Right-click and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

:: ═════════════════════════════════════════════════════════════
:: 1. HTTP Service (http.sys kernel driver)
::    Required by: OWIN HttpListener (the broker's web server)
::    Default: Demand (manual) — we set it to Demand so it
::    starts automatically when the broker requests it.
::    NOTE: "demand" is correct for kernel drivers — they start
::    when first used. "auto" can cause boot issues for drivers.
:: ═════════════════════════════════════════════════════════════
echo   [1/4] HTTP Service (http.sys)...
sc.exe config http start= demand >nul 2>&1
if %errorlevel% equ 0 (
    echo         ✓  Set to Demand (starts when broker starts)
) else (
    echo         ✗  Failed — may need manual fix
)
:: Start it now if not running
sc.exe query http | findstr "RUNNING" >nul 2>&1
if %errorlevel% neq 0 (
    net start http >nul 2>&1
    echo         ✓  Started HTTP service
) else (
    echo         ✓  Already running
)

:: ═════════════════════════════════════════════════════════════
:: 2. KepServerEX 6 Runtime
::    Required by: The broker connects to this via OPC HDA COM
::    Must start BEFORE the broker
:: ═════════════════════════════════════════════════════════════
echo.
echo   [2/4] KepServerEX 6 Runtime...
sc.exe query KEPServerEXV6 >nul 2>&1
if %errorlevel% equ 0 (
    sc.exe config KEPServerEXV6 start= auto >nul 2>&1
    echo         ✓  Set to Auto-Start
    sc.exe query KEPServerEXV6 | findstr "RUNNING" >nul 2>&1
    if %errorlevel% neq 0 (
        net start KEPServerEXV6 >nul 2>&1
        echo         ✓  Started KepServerEX
    ) else (
        echo         ✓  Already running
    )
) else (
    echo         ⚠  Service not found — KepServerEX may use a different service name
    echo         ⚠  Check with: sc.exe query type= service state= all ^| findstr "Kepware"
)

:: ═════════════════════════════════════════════════════════════
:: 3. OPC HDA Broker
::    The broker itself — starts after KepServerEX
::    Auto-restart on failure (5s, 10s, 30s delays)
:: ═════════════════════════════════════════════════════════════
echo.
echo   [3/4] OPC HDA Broker Service...
sc.exe query OpcHdaBroker >nul 2>&1
if %errorlevel% equ 0 (
    sc.exe config OpcHdaBroker start= auto >nul 2>&1
    sc.exe failure OpcHdaBroker reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul 2>&1
    echo         ✓  Set to Auto-Start with failure recovery
    sc.exe query OpcHdaBroker | findstr "RUNNING" >nul 2>&1
    if %errorlevel% neq 0 (
        net start OpcHdaBroker >nul 2>&1
        echo         ✓  Started OPC HDA Broker
    ) else (
        echo         ✓  Already running
    )
) else (
    echo         ⚠  Service not installed yet
    echo         ⚠  Install with: deploy\install-service.bat
)

:: ═════════════════════════════════════════════════════════════
:: 4. Grafana (optional)
:: ═════════════════════════════════════════════════════════════
echo.
echo   [4/4] Grafana (optional)...
sc.exe query grafana >nul 2>&1
if %errorlevel% equ 0 (
    sc.exe config grafana start= auto >nul 2>&1
    echo         ✓  Set to Auto-Start
    sc.exe query grafana | findstr "RUNNING" >nul 2>&1
    if %errorlevel% neq 0 (
        net start grafana >nul 2>&1
        echo         ✓  Started Grafana
    ) else (
        echo         ✓  Already running
    )
) else (
    echo         -  Not installed (skipped)
)

:: ═════════════════════════════════════════════════════════════
:: 5. Reserve HTTP URL (for service mode)
:: ═════════════════════════════════════════════════════════════
echo.
echo   [+] HTTP URL Reservation...
netsh http show urlacl url=http://+:5000/ >nul 2>&1
if %errorlevel% neq 0 (
    netsh http add urlacl url=http://+:5000/ user=Everyone >nul 2>&1
    echo         ✓  Reserved http://+:5000/
) else (
    echo         ✓  Already reserved
)

:: ═════════════════════════════════════════════════════════════
:: Summary
:: ═════════════════════════════════════════════════════════════
echo.
echo   ═══════════════════════════════════════════════════════
echo   Boot order after restart:
echo.
echo     1. Windows starts http.sys driver (on-demand)
echo     2. KepServerEX 6 starts (auto)
echo     3. OPC HDA Broker starts (auto) → connects to Kepware
echo     4. Grafana starts (auto) → queries the broker API
echo   ═══════════════════════════════════════════════════════
echo.
pause

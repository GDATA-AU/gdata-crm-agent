@echo off
:: Setup.bat — GDATA CRM Agent
::
:: Run this once to install the Windows service and launch the configuration wizard.
:: You will be prompted for administrator rights via UAC.

:: ── Self-elevate if not already running as admin ─────────────────────────────
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

setlocal

set "SERVICE_NAME=gdata-agent"
set "DISPLAY_NAME=GDATA CRM Agent"
set "DESCRIPTION=Polls the customer portal for extraction jobs and processes them locally."
set "SERVICE_EXE=%~dp0service\CrmAgent.exe"
set "TRAY_EXE=%~dp0tray\CrmAgentTray.exe"

:: ── Validate files exist ──────────────────────────────────────────────────────
if not exist "%SERVICE_EXE%" (
    echo ERROR: Could not find service\CrmAgent.exe
    echo Please ensure you extracted the full zip archive into its own folder.
    pause
    exit /b 1
)
if not exist "%TRAY_EXE%" (
    echo ERROR: Could not find tray\CrmAgentTray.exe
    echo Please ensure you extracted the full zip archive into its own folder.
    pause
    exit /b 1
)

:: ── Install or update service ─────────────────────────────────────────────────
sc query %SERVICE_NAME% >nul 2>&1
if %errorLevel% equ 0 (
    echo %DISPLAY_NAME% service is already installed — updating binary path...
    sc stop %SERVICE_NAME% >nul 2>&1
    sc config %SERVICE_NAME% binPath= "\"%SERVICE_EXE%\"" DisplayName= "%DISPLAY_NAME%"
    sc description %SERVICE_NAME% "%DESCRIPTION%"
    goto :launch_tray
)

echo Installing %DISPLAY_NAME% service...
sc create %SERVICE_NAME% binPath= "\"%SERVICE_EXE%\"" start= auto DisplayName= "%DISPLAY_NAME%"
sc description %SERVICE_NAME% "%DESCRIPTION%"
sc failure %SERVICE_NAME% reset= 86400 actions= restart/10000/restart/30000/restart/60000
echo Done.

:: ── Launch the configuration wizard ──────────────────────────────────────────
:launch_tray
echo.
echo Launching configuration wizard...
start "" "%TRAY_EXE%"
exit /b 0


@echo off
REM install-service.bat
REM
REM Installs crm-agent as a Windows service.
REM Must be run as Administrator.
REM
REM Usage:
REM   install-service.bat              (install & start)
REM   install-service.bat --uninstall  (stop & remove)
REM
REM Prerequisites:
REM   - dotnet publish has been run, or the exe is in the publish folder
REM   - appsettings.json is configured

setlocal

set SERVICE_NAME=gdata-agent
set DISPLAY_NAME=GDATA CRM Agent
set DESCRIPTION=Polls the customer portal for extraction jobs and processes them locally.
set EXE_PATH=%~dp0..\..\publish\CrmAgent.exe

if "%~1"=="--uninstall" (
    echo Stopping %SERVICE_NAME%...
    sc stop %SERVICE_NAME% >nul 2>&1
    timeout /t 3 /nobreak >nul
    echo Removing %SERVICE_NAME% service...
    sc delete %SERVICE_NAME%
    echo Done.
    exit /b 0
)

if not exist "%EXE_PATH%" (
    echo ERROR: %EXE_PATH% not found.
    echo Run 'dotnet publish -c Release -o publish' first.
    exit /b 1
)

echo Installing %SERVICE_NAME% as a Windows service...
sc create %SERVICE_NAME% ^
    binPath= "\"%EXE_PATH%\"" ^
    start= auto ^
    DisplayName= "%DISPLAY_NAME%"

sc description %SERVICE_NAME% "%DESCRIPTION%"
sc failure %SERVICE_NAME% reset= 86400 actions= restart/10000/restart/30000/restart/60000

echo Starting %SERVICE_NAME%...
sc start %SERVICE_NAME%

echo.
echo %SERVICE_NAME% installed and started.
echo.
echo Useful commands:
echo   sc query %SERVICE_NAME%          - check status
echo   sc stop %SERVICE_NAME%           - stop
echo   sc start %SERVICE_NAME%          - start
echo   eventvwr.msc                     - view logs in Event Viewer

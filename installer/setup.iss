; setup.iss — Inno Setup installer script for GDATA CRM Agent
;
; Prerequisites:
;   1. Inno Setup 6.x installed (https://jrsoftware.org/isinfo.php)
;   2. The project has been published:
;        dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -o publish
;   3. Compile this script:
;        iscc installer\setup.iss
;
; Output: installer\Output\crm-agent-setup.exe

#define AppName    "GDATA CRM Agent"
#define AppVersion "1.0.0"
#define AppPublisher "GDATA-AU"
#define ServiceName "gdata-agent"
#define ExeName "CrmAgent.exe"
; Path to the published outputs, relative to this script
#define PublishDir     ".\..\publish"
#define TrayPublishDir ".\..\publish-tray"
#define TrayExeName    "GDATAAgentTray.exe"

[Setup]
AppId={{A3F6B2D1-4C8E-4F2A-9D3B-7E1C5A0F8B2D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/GDATA-AU/crm-agent
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=Output
OutputBaseFilename=crm-agent-setup
Compression=lzma2/ultra64
SolidCompression=yes
; Require admin so we can register the Windows service
PrivilegesRequired=admin
; Minimum Windows 10
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\dotnet\CrmAgent.Tray\app.ico
; Allow upgrade installs (same AppId)
CloseApplications=yes
RestartIfNeededByRun=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\tray\{#TrayExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; (intentionally empty — connection details are collected by the tray app on first launch)

[Files]
; Worker Service
Source: "{#PublishDir}\*";     DestDir: "{app}";       Flags: ignoreversion recursesubdirs createallsubdirs
; Tray configuration app (runs as the logged-in user, manages config + service state)
Source: "{#TrayPublishDir}\*"; DestDir: "{app}\tray"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Auto-start the tray app for all users on login (HKLM so it works regardless of which account logs in)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "GDATAAgent"; \
  ValueData: """{app}\tray\{#TrayExeName}"""; \
  Flags: uninsdeletevalue
; Remove legacy Run entry left by older installers (prevents duplicate tray auto-starts on upgrade)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: none; ValueName: "LGACrmAgent"; \
  Flags: deletevalue
; Remove legacy Run entry from pre-rename installers
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: none; ValueName: "GDATACrmAgent"; \
  Flags: deletevalue

[Run]
; Launch the tray app after installation. On first run it detects no config and opens the setup form.
Filename: "{app}\tray\{#TrayExeName}"; Description: "Launch GDATA CRM Agent"; Flags: nowait postinstall skipifsilent

[Code]

//-----------------------------------------------------------------------
// Helpers
//-----------------------------------------------------------------------

//-----------------------------------------------------------------------
// Service helpers
//-----------------------------------------------------------------------

procedure StopAndDeleteService();
begin
  // Stop — ignore errors if already stopped or not installed
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, 0);
  // Brief pause for the service to terminate
  Sleep(2000);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, 0);
  Sleep(1000);
end;

// Kill the tray app process so its files can be deleted during uninstall.
procedure KillTrayApp();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#TrayExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Also kill legacy-named tray from pre-rename installs
  Exec('taskkill.exe', '/F /IM CrmAgentTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

// Remove the ProgramData config directory (contains API key — must not be left behind).
procedure DeleteProgramDataDir();
var
  Dir: String;
begin
  Dir := ExpandConstant('{commonappdata}\GDATA CRM Agent');
  if DirExists(Dir) then
    DelTree(Dir, True, True, True);
end;

// Grant the logged-in users group modify access to the ProgramData config directory
// so the tray app (running as user) can write appsettings.json.
procedure CreateProgramDataDir();
var
  Dir: String;
  ResultCode: Integer;
begin
  Dir := ExpandConstant('{commonappdata}\GDATA CRM Agent');
  if not DirExists(Dir) then
    CreateDir(Dir);
  // S-1-5-32-545 is the well-known SID for the built-in Users group (locale-independent)
  Exec('icacls.exe', '"' + Dir + '" /grant *S-1-5-32-545:(OI)(CI)M /T /Q',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Register the Windows service but do NOT start it.
// The tray app starts the service after the user completes first-run setup.
procedure RegisterService();
var
  ExePath: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#ExeName}');

  Exec('sc.exe',
    'create {#ServiceName} binPath= "\"' + ExePath + '\"" start= auto DisplayName= "GDATA CRM Agent"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('sc.exe',
    'description {#ServiceName} "Polls the council portal for extraction jobs and writes results to Azure Blob Storage."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('sc.exe',
    'failure {#ServiceName} reset= 86400 actions= restart/10000/restart/30000/restart/60000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Grant Interactive Users the ability to start and stop the service so the
  // tray app can control it without requiring admin elevation.
  Exec('sc.exe',
    'sdset {#ServiceName} D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Service is intentionally left Stopped here.
  // It will be started by the tray app once the user saves valid credentials.
end;

//-----------------------------------------------------------------------
// CurStepChanged — hook into install/uninstall lifecycle
//-----------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Kill the tray app so its files can be replaced during upgrades
    KillTrayApp();
    // Remove any existing service before overwriting files
    StopAndDeleteService();
  end;

  if CurStep = ssPostInstall then
  begin
    CreateProgramDataDir();
    RegisterService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillTrayApp();
    StopAndDeleteService();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Delete credentials stored in ProgramData — not tracked by Inno Setup's file list
    DeleteProgramDataDir();
  end;
end;

; Droppo installer (Inno Setup 6). Don't run this by hand: double-click build-installer.bat,
; which builds the app, gathers FFmpeg + qpdf and then compiles this script.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change AppId: it's how Windows knows a new version replaces the old one.
AppId={{338ABAEB-458A-4DEC-99C0-ACBC6BDFC4BC}
AppName=Droppo
AppVersion={#AppVersion}
AppVerName=Droppo {#AppVersion}
AppPublisher=Amir Hassan
VersionInfoVersion={#AppVersion}
; Per-user install in %LocalAppData%\Programs: no admin prompt, and the app runs at the same level as
; File Explorer (an elevated app can't see Explorer's drags).
PrivilegesRequired=lowest
DefaultDirName={autopf}\Droppo
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Droppo-Setup-{#AppVersion}
SetupIconFile=..\src\Droppo\Assets\app.ico
WizardImageFile=wizard-large.bmp,wizard-large-2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-2x.bmp
UninstallDisplayIcon={app}\Droppo.exe
UninstallDisplayName=Droppo
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; we close the running app ourselves (PrepareToInstall), quietly
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start Droppo when Windows starts (recommended)"

[InstallDelete]
; drop old bundled tool versions before copying the new ones
Type: filesandordirs; Name: "{app}\tools"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Droppo"; Filename: "{app}\Droppo.exe"; Comment: "Drop it. Done."

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Droppo"; ValueData: """{app}\Droppo.exe"" --startup"; Tasks: startup

[Run]
Filename: "{app}\Droppo.exe"; Description: "Start Droppo now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Droppo.exe"; Flags: runhidden; RunOnceId: "CloseDroppo"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nDrag a file, hold Shift, let go on what you want. Drop it. Done.
FinishedHeadingLabel=Droppo is ready
FinishedLabelNoIcons=Droppo now sits by the clock. Start dragging any file, then hold Shift.
FinishedLabel=Droppo now sits by the clock. Start dragging any file, then hold Shift.

[Code]
const
  OldAppKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B7E-4C1D-9A2F-3E5B7D9C1A40}_is1';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  OldUninstaller: String;
begin
  // updating while it runs: close it so its files can be replaced
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Droppo.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // the app used to be called CursorConvert: close and remove that version (settings are carried over by the app)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im CursorConvert.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if RegQueryStringValue(HKEY_CURRENT_USER, OldAppKey, 'UninstallString', OldUninstaller) then
    Exec(RemoveQuotes(OldUninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CursorConvert');
  Sleep(400);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // unticked "start with Windows" on an update: remove an older entry too
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('startup')) then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Droppo');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    // the app's own "Start with Windows" switch may have set this
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Droppo');
end;

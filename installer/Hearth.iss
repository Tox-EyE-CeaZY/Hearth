; Hearth installer (Inno Setup 6). Built by build-installer.ps1, which passes
; AppVersion, AppFullVersion, PublishDir, OutputDir and IconFile.
;
; Per-user install into %LocalAppData%\Programs\Hearth: no admin prompt, and
; Hearth itself runs unelevated (drag and drop from Explorer needs that).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppFullVersion
  #define AppFullVersion AppVersion
#endif

[Setup]
AppId={{6C3B0B52-2E1B-4E36-9A8B-6D2F4C1E7A31}
AppName=Hearth
AppVersion={#AppFullVersion}
AppVerName=Hearth {#AppVersion}
AppPublisher=Caleb Rieger
AppPublisherURL=https://github.com/Tox-EyE-CeaZY/Hearth
AppSupportURL=https://github.com/Tox-EyE-CeaZY/Hearth/issues
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
VersionInfoDescription=Hearth Setup
DefaultDirName={autopf}\Hearth
DefaultGroupName=Hearth
DisableProgramGroupPage=yes
; A fixed folder: the install and uninstall clear it out completely.
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Hearth-Setup-{#AppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Hearth.exe
UninstallDisplayName=Hearth
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Hearth is closed by quit-hearth.ps1 below, which saves the layout and puts
; the desktop icons back; Restart Manager would just end the process.
CloseApplications=no
RestartApplications=no

[Messages]
WelcomeLabel2=This will install Hearth [ver] on your computer.%n%nHearth replaces your desktop and Start menu with an Android-style home screen and widgets. Your files and Explorer stay as they are, and quitting Hearth puts the normal desktop straight back.

[Tasks]
Name: "startup"; Description: "Start Hearth when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "quit-hearth.ps1"; DestDir: "{app}/tools"; Flags: ignoreversion

[InstallDelete]
; A fresh copy each time, so files a newer build dropped don't linger.
Type: filesandordirs; Name: "{app}/*"

[Icons]
Name: "{group}\Hearth"; Filename: "{app}\Hearth.exe"; Comment: "Android-style home screen for Windows"
Name: "{group}\Quit Hearth"; Filename: "{app}\Hearth.exe"; Parameters: "--quit"; Comment: "Close Hearth and bring back the normal desktop"
Name: "{group}\Uninstall Hearth"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Hearth"; ValueData: """{app}\Hearth.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Hearth"; Tasks: not startup; Flags: deletevalue
; Created by Hearth for Windows notifications; removed with it.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\Hearth.Desktop"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Hearth.exe"; Description: "Start Hearth now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure QuitHearth(const Script: String);
var
  Code: Integer;
begin
  if not FileExists(Script) then Exit;
  Exec(ExpandConstant('{sys}') + '/WindowsPowerShell/v1.0/powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + Script + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('quit-hearth.ps1');
  QuitHearth(ExpandConstant('{tmp}') + '/quit-hearth.ps1');
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  QuitHearth(ExpandConstant('{app}') + '/tools/quit-hearth.ps1');
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
  begin
    if MsgBox('Also remove your Hearth settings, layout and widget data (notes, tasks, alarms, shelf)?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}') + '/Hearth', True, True, True);
      DelTree(ExpandConstant('{localappdata}') + '/Hearth', True, True, True);
    end;
  end;
end;

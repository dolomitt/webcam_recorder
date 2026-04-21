#define MyAppName "webcam_recorder"
#define MyAppPublisher "THALES"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef BuildDir
  #define BuildDir "..\\installer\\stage"
#endif

#ifndef OutputDir
  #define OutputDir "..\\installer\\dist"
#endif

[Setup]
AppId={{99A5A44E-233D-44CD-A87E-D3958DDF5D3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x86compatible
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppName}-setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "openconfig"; Description: "Open appsettings.json after installation"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\webcam_recorder.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\webcam_recorder.exe"; Tasks: desktopicon

[Run]
Filename: "notepad.exe"; Parameters: """{app}\appsettings.json"""; Description: "Edit appsettings.json"; Flags: postinstall shellexec; Tasks: openconfig
Filename: "{app}\webcam_recorder.exe"; Description: "Launch webcam_recorder"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet452OrNewerInstalled: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 379893);
end;

function InitializeSetup(): Boolean;
begin
  if not IsDotNet452OrNewerInstalled then
  begin
    MsgBox('.NET Framework 4.5.2 or newer is required. Please install it before installing webcam_recorder.', mbCriticalError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

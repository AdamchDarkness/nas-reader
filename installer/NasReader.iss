; Nas Reader - Windows installer (Inno Setup 6)
; Built by release.ps1:  ISCC /DAppVersion=1.1.0 installer\NasReader.iss
; Installs per user (no admin prompt), so updates can run silently from the app.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\NasReader.exe"
#endif

[Setup]
AppId={{6F1C2A4E-3B7D-4C9A-9E51-2D8B7A4F0C13}
AppName=Nas Reader
AppVersion={#AppVersion}
AppVerName=Nas Reader {#AppVersion}
AppPublisher=AdamchDarkness
AppPublisherURL=https://github.com/AdamchDarkness/nas-reader
AppSupportURL=https://github.com/AdamchDarkness/nas-reader/issues
AppUpdatesURL=https://github.com/AdamchDarkness/nas-reader/releases
DefaultDirName={localappdata}\Programs\NasReader
DefaultGroupName=Nas Reader
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=NasReader-Setup-{#AppVersion}
SetupIconFile=..\src\NasReader\app.ico
UninstallDisplayIcon={app}\NasReader.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Nas Reader"; Filename: "{app}\NasReader.exe"
Name: "{autodesktop}\Nas Reader"; Filename: "{app}\NasReader.exe"; Tasks: desktopicon

[Run]
; Also runs after a silent update, unless the app asked not to (/NORUN when it was closed by the user).
Filename: "{app}\NasReader.exe"; Description: "{cm:LaunchProgram,Nas Reader}"; Flags: nowait postinstall; Check: ShouldLaunch

[Code]
function ShouldLaunch: Boolean;
begin
  Result := Pos('/NORUN', UpperCase(GetCmdTail)) = 0;
end;

; RadioForge installer (Inno Setup 6). Build with scripts\build-installer.ps1
#define AppName "RadioForge"
#define AppVersion "1.2.0"
#define AppPublisher "RadioForge"
#define AppURL "https://github.com/unupunct/RadioForge"

[Setup]
AppId={{8B1E5C3A-6D2F-4C79-9A4E-52F0A7C1D001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion=1.2.0.0
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=.
OutputBaseFilename=RadioForge-Setup
SetupIconFile=..\Source\RadioForge.App\Assets\radioforge.ico
UninstallDisplayIcon={app}\RadioForge.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE
AppMutex=RadioForge.SingleInstance
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The app is published self-contained (.NET runtime included), so no separate runtime install is needed.
Source: "..\Release\RadioForge\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Documentation\*.md"; DestDir: "{app}\Documentation"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\RadioForge.exe"
Name: "{group}\{#AppName} documentation"; Filename: "{app}\Documentation\README.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\RadioForge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RadioForge.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// User data (settings, profiles, playlists, recordings history) lives in %APPDATA%\RadioForge and is never touched by
// upgrades. On uninstall the user is asked whether to remove it as well (default: keep).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Dir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Dir := ExpandConstant('{userappdata}\RadioForge');
    if DirExists(Dir) and (not UninstallSilent) then
      if MsgBox('Also delete your RadioForge settings, profiles, playlists and logs in' + #13#10 + Dir + '?' + #13#10#13#10 +
                'Choose No to keep them (recommended if you plan to reinstall). Your recorded audio files are never deleted.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Dir, True, True, True);
  end;
end;

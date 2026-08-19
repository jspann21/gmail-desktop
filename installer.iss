#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "gmail-desktop-setup"
#endif

[Setup]
AppId={{7043D4A4-3AB5-4DF8-B439-82E0B8E83138}
AppName=Gmail Desktop
AppVersion={#AppVersion}
AppPublisher=Gmail Desktop
DefaultDirName={localappdata}\Programs\Gmail Desktop
DefaultGroupName=Gmail Desktop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=installer-output
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=Assets\GmailDesktop.ico
UninstallDisplayIcon={app}\GmailDesktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Excludes: "*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Gmail Desktop"; Filename: "{app}\GmailDesktop.exe"
Name: "{autodesktop}\Gmail Desktop"; Filename: "{app}\GmailDesktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GmailDesktop.exe"; Description: "Launch Gmail Desktop"; Flags: nowait postinstall skipifsilent

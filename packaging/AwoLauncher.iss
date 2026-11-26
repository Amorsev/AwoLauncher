#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts"
#endif

[Setup]
AppId={{92E3B502-A295-42A5-B803-AF5C3E67837B}
AppName=AwoLauncher
AppVersion={#AppVersion}
AppPublisher=AwoLauncher
AppPublisherURL=https://github.com/Amorsev/AwoLauncher
DefaultDirName={localappdata}\Programs\AwoLauncher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#PublishDir}
OutputBaseFilename=AwoLauncher-Setup
SetupIconFile=..\Launcher.UI\Assets\awo-launcher.ico
UninstallDisplayIcon={app}\AwoLauncher.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\AwoLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\AwoLauncher"; Filename: "{app}\AwoLauncher.exe"
Name: "{autodesktop}\AwoLauncher"; Filename: "{app}\AwoLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AwoLauncher.exe"; Description: "{cm:LaunchProgram,AwoLauncher}"; Flags: nowait postinstall skipifsilent

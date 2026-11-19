; Satchel installer (Inno Setup 6.3+). Bundles the self-contained build, so nothing else needs installing.
; Per-user install: no admin prompt, lands in %LOCALAPPDATA%\Programs\Satchel, upgrades in place.
; Build with installer\build-installer.ps1, or:
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=<publish dir> /DOutputDir=<dir> installer\Satchel.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\out\self-contained"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Never change AppId: it's how upgrades and uninstall find the existing install.
AppId={{54018740-5BDF-4547-A00E-95C6A865BB83}
AppName=Satchel
AppVersion={#AppVersion}
AppVerName=Satchel {#AppVersion}
AppPublisher=Satchel
AppPublisherURL=https://github.com/coryfinnegan/Satchel
AppSupportURL=https://github.com/coryfinnegan/Satchel/issues
AppUpdatesURL=https://github.com/coryfinnegan/Satchel/releases
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=Satchel installer
PrivilegesRequired=lowest
DefaultDirName={autopf}\Satchel
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
UsePreviousAppDir=yes
UninstallDisplayName=Satchel
UninstallDisplayIcon={app}\Satchel.exe
SetupIconFile=..\src\Satchel\Assets\satchel.ico
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=Satchel-Setup-v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Satchel holds this mutex while running, so setup/uninstall can ask the user to close it first.
AppMutex=SatchelAddonManagerRunning
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Add to the Start menu"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Satchel"; Filename: "{app}\Satchel.exe"; Comment: "Ad-free World of Warcraft addon manager"; Tasks: startmenuicon
Name: "{autodesktop}\Satchel"; Filename: "{app}\Satchel.exe"; Comment: "Ad-free World of Warcraft addon manager"; Tasks: desktopicon

[Registry]
; Satchel's "Start with Windows" option writes this value; remove it with the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Satchel"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Satchel.exe"; Description: "{cm:LaunchProgram,Satchel}"; Flags: nowait postinstall skipifsilent

[Messages]
FinishedLabel=Satchel is installed. Your settings live in your AppData folder, so uninstalling later keeps them, and never touches your WoW addons.

; Inno Setup script — Windows installer (per-user install, no admin rights needed).
; Build: build\publish.ps1   (or: iscc /DAppVersion=0.1.0 /DPublishDir=..\..\artifacts\publish\win-x64 build\windows\PokeStudio.iss)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish\win-x64"
#endif
; Working title — change with AppInfo.cs once a trademark-safe name is chosen.
#define AppName "Poke Studio Customizer"
#define AppExe "PokeStudio.exe"

[Setup]
AppId={{6C9C2E57-7E4B-4C0E-9C1B-5D0F2B7A1E33}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Poke Studio Customizer contributors
DefaultDirName={localappdata}\Programs\PokeStudio
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=PokeStudio-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\PokeStudio.App\Assets\poke-studio.ico
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; User projects, settings and backups live in Documents and %APPDATA%/%LOCALAPPDATA% and are NOT removed on uninstall.

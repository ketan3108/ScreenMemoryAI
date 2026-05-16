#define AppName "ScreenMemory AI"

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#define AppPublisher "ScreenMemoryAI/K10N"
#define AppExeName "ScreenMemory.AI.App.exe"

#ifndef RepoRoot
#define RepoRoot "E:\ScreenMemoryAI"
#endif

#ifndef PublishDir
#define PublishDir "E:\ScreenMemoryAI\src\ScreenMemory.AI.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

#define VcRedistPath RepoRoot + "\src\ScreenMemory.AI.App\Assets\VC_redist.x64.exe"
#define AppIconPath RepoRoot + "\src\ScreenMemory.AI.App\Assets\AppIcon.ico"

[Setup]
AppId={{8F4A8D70-2D28-4A26-9E42-2A5B0A4AF001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#RepoRoot}\Installer
OutputBaseFilename=ScreenMemoryAI_Setup_v{#AppVersion}
SetupIconFile={#AppIconPath}
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Launch {#AppName} on Windows startup"; GroupDescription: "System Settings"; Flags: unchecked

[InstallDelete]
; Remove stale binaries from older builds while keeping user data in %APPDATA%\ScreenMemory AI untouched.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#if FileExists(VcRedistPath)
Source: "{#VcRedistPath}"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
#if FileExists(VcRedistPath)
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing required Microsoft C++ Runtime..."; Flags: waituntilterminated; Check: NeedsVcRedist
#endif
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsVcRedist: Boolean;
var
  Installed: Cardinal;
begin
  Result := True;

  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
  begin
    Result := Installed <> 1;
  end;
end;

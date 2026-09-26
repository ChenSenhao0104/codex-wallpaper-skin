#ifndef AppVersion
  #define AppVersion "0.8.0"
#endif

#ifndef SourceDir
  #error SourceDir must point to a published Codex Wallpaper Skin directory.
#endif

#ifndef OutputDir
  #error OutputDir must point to the release output directory.
#endif

#define AppFileVersion AppVersion + ".0"
#define AppExeName "CodexWallpaperSkin.exe"
#define ProductRegistryKey "Software\CodexWallpaperSkin"
#define StartupRegistryKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define StartupValueName "CodexWallpaperSkin.AutoRestore"

[Setup]
AppId={{2E164A64-2E9C-4BCA-A461-D1DD71C6032F}
AppName=Codex Wallpaper Skin
AppVersion={#AppVersion}
AppVerName=Codex Wallpaper Skin {#AppVersion}
AppPublisher=Codex Wallpaper Skin contributors
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=Codex Wallpaper Skin contributors
VersionInfoDescription=Codex Wallpaper Skin installer
VersionInfoProductName=Codex Wallpaper Skin
DefaultDirName={localappdata}\Programs\Codex Wallpaper Skin
DefaultGroupName=Codex Wallpaper Skin
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexWallpaperSkin-Setup-v{#AppVersion}-win-x64
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName=Codex Wallpaper Skin {#AppVersion}
UsePreviousAppDir=yes
UsePreviousGroup=yes
CloseApplications=no
RestartApplications=no
AppMutex=Local\CodexWallpaperSkin.Companion.Gui,Local\CodexWallpaperSkin.DeferredRestore.v2
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.LaunchProgram=Launch Codex Wallpaper Skin
chinesesimplified.LaunchProgram=启动 Codex Wallpaper Skin
english.KeepDataPrompt=Keep your wallpaper library, presets, settings, and controller logs for a future reinstall?%n%nChoose Yes (recommended) to keep them. Choose No only if you want to remove all Codex Wallpaper Skin user data.%n%nWallpaper source files and Codex data are never deleted.
chinesesimplified.KeepDataPrompt=是否保留壁纸库、预设、设置和控制器日志，以便以后重新安装？%n%n选择“是”（推荐）会保留这些数据。仅当你希望彻底删除 Codex Wallpaper Skin 的全部用户数据时选择“否”。%n%n壁纸源文件和 Codex 数据永远不会被删除。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Codex Wallpaper Skin"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Codex Wallpaper Skin"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#ProductRegistryKey}"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "{#ProductRegistryKey}"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#AppVersion}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;

function IsOwnedStartupCommand(const Command: String): Boolean;
var
  ExpectedPrefix: String;
begin
  ExpectedPrefix := '"' + ExpandConstant('{app}\{#AppExeName}') + '"';
  Result := Pos(Lowercase(ExpectedPrefix), Lowercase(Command)) = 1;
end;

procedure RemoveOwnedStartupRegistration;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, '{#StartupRegistryKey}', '{#StartupValueName}', Command) and
     IsOwnedStartupCommand(Command) then
  begin
    RegDeleteValue(HKCU, '{#StartupRegistryKey}', '{#StartupValueName}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedStartupRegistration;
    RemoveUserData := False;
    if not UninstallSilent then
    begin
      RemoveUserData := MsgBox(CustomMessage('KeepDataPrompt'), mbConfirmation,
        MB_YESNO or MB_DEFBUTTON1) = IDNO;
    end;
  end;

  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DelTree(ExpandConstant('{localappdata}\CodexWallpaperSkin'), True, True, True);
  end;
end;

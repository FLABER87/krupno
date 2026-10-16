; Krupno installer. It carries only the app itself (self-contained publish with .NET inside); the models are
; downloaded during installation from the models-v2 release and verified by SHA-256. The UI is English only.
; Built by installer\build.ps1, which also passes the parameters:
;   ISCC /DAppVersion=0.1.0 /DFileVersion=0.1.0.0 /DAppDir=<publish folder> /DOutputDir=<output folder> Krupno.iss

#if Ver < EncodeVer(6, 5, 0)
  #error Inno Setup 6.5 or newer is required: downloading files in the [Files] section appeared in 6.5
#endif
#ifndef AppVersion
  #error The version is not set (/DAppVersion=x.y.z); build with installer\build.ps1
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef AppDir
  #error The publish folder is not set (/DAppDir=...); build with installer\build.ps1
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define RepoUrl "https://github.com/FLABER87/krupno"
#define ModelsUrl RepoUrl + "/releases/download/models-v2"
#define ImageVerb "Software\Classes\SystemFileAssociations\image\shell\Krupno"
#define FolderVerb "Software\Classes\Directory\shell\Krupno"

; A [Files] line for a model from the models-v2 release. When the app is updated, a model with the same SHA-256
; is not downloaded again (ModelNeeded in [Code]).
#define Model(Component, File, Size, Hash) \
  'Source: "' + ModelsUrl + '/' + File + '"; DestDir: "{app}\models"; DestName: "' + File + '"; ' + \
  'ExternalSize: ' + Size + '; Hash: "' + Hash + '"; Components: models\' + Component + '; ' + \
  "Check: ModelNeeded('" + File + "', '" + Hash + "'); Flags: external download ignoreversion"

[Setup]
AppId={{2C15E368-F7C9-4CB9-9800-9F2548D61A83}
AppName=Krupno
AppVersion={#AppVersion}
AppVerName=Krupno {#AppVersion}
AppPublisher=FLABER87
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) 2026 FLABER87
VersionInfoVersion={#FileVersion}
VersionInfoProductName=Krupno
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=Krupno Setup
; Installs for the current user by default, without administrator rights: %LOCALAPPDATA%\Programs\Krupno.
; "For all users" installs to Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\Krupno
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic
SetupIconFile=..\src\Krupno.App\Assets\Krupno.ico
UninstallDisplayIcon={app}\Krupno.exe
UninstallDisplayName=Krupno
ChangesEnvironment=yes
ChangesAssociations=yes
SetupLogging=yes
OutputDir={#OutputDir}
OutputBaseFilename=Krupno-{#AppVersion}-setup-x64
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (all models)"
Name: "compact"; Description: "Compact installation (recommended model only)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

; Models are listed in the same order and with the same names as in the app window (ViewModels/ModelItem.cs).
[Components]
Name: "app"; Description: "Krupno (desktop app and krupno-cli command-line tool)"; Types: full compact custom; Flags: fixed
Name: "models"; Description: "Models (downloaded from GitHub during installation)"; Types: full compact
Name: "models\realplksr"; Description: "Photo, fast: BHI RealPLKSR (CC BY 4.0), recommended"; Types: full compact
Name: "models\dat2fp16"; Description: "Photo, maximum detail: BHI DAT2 fp16 (CC BY 4.0)"; Types: full
Name: "models\ultrasharp"; Description: "General purpose: UltraSharpV2 (CC BY-NC-SA 4.0, non-commercial use only)"; Types: full
Name: "models\dat2"; Description: "Photo, maximum detail: BHI DAT2 fp32 (CC BY 4.0), very slow"; Types: full
Name: "models\esrganfp16"; Description: "Classic: Real-ESRGAN x4plus fp16 (BSD-3-Clause)"; Types: full
Name: "models\esrgan"; Description: "Classic: Real-ESRGAN x4plus fp32 (BSD-3-Clause)"; Types: full
Name: "models\general"; Description: "Draft, very fast: Real-ESRGAN general x4v3 (BSD-3-Clause)"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "path"; Description: "Add the install folder to PATH, so krupno-cli can be run from any console"; GroupDescription: "Command line:"
Name: "contextmenu"; Description: "Add ""Upscale with Krupno"" to the Explorer context menu of images and folders"; GroupDescription: "Explorer integration:"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion; Components: app
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: app
Source: "models-README.txt"; DestDir: "{app}\models"; DestName: "README.txt"; Flags: ignoreversion; Components: app
{#Model("realplksr", "4xBHI_realplksr_dysample_real.onnx", "29_824_646", "4c5a800d7c8afbbb533724185a0f9784ca79e8813d6d6cd190ff5092c297a697")}
{#Model("dat2fp16", "4xBHI_dat2_real_fp16.onnx", "25_330_673", "afb2687e5eaf58b46b5679d2a5c4b8190b58dbcf6beedd492a18de88da264974")}
{#Model("ultrasharp", "4x-UltraSharpV2.onnx", "51_871_076", "7cbc3daf6b8e9800932196a252f8b88d19ecbbe63fdfc4976ce27ce537311f04")}
{#Model("dat2", "4xBHI_dat2_real.onnx", "51_871_070", "4daffb7c3e695b5a8f7586be3546fd306abdf00fbfce86acef94bdab696d9780")}
{#Model("esrganfp16", "RealESRGAN_x4plus_fp16.onnx", "33_770_922", "97b1d38c36492a7931c9844221d862c89680e5e47eb86e419ee262b4cea49d07")}
{#Model("esrgan", "RealESRGAN_x4plus.onnx", "67_167_465", "12938ed2823438c7b32f86964af788f8a3a0d73f9d9b5200d72f34765a447547")}
{#Model("general", "realesr-general-x4v3.onnx", "4_868_806", "5e3a288df65e2e1fe5e070317e16b2808846ea731434e91eab869046ae013da3")}

[Icons]
Name: "{autoprograms}\Krupno"; Filename: "{app}\Krupno.exe"
Name: "{autodesktop}\Krupno"; Filename: "{app}\Krupno.exe"; Tasks: desktopicon

; The menu item is shown only when a single file or folder is selected: with a command string, Explorer starts
; one process per selected file. Several files are easier to drag into the window.
; If the checkbox is cleared on reinstall, the item is removed (deletekey with Tasks: not contextmenu).
[Registry]
Root: HKA; Subkey: "{#ImageVerb}"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueData: "Upscale with Krupno"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Krupno.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}\command"; ValueType: string; ValueData: """{app}\Krupno.exe"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueData: "Upscale with Krupno"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Krupno.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}\command"; ValueType: string; ValueData: """{app}\Krupno.exe"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\Krupno.exe"; Description: "{cm:LaunchProgram,Krupno}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  UserEnvironmentKey = 'Environment';
  SystemEnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

var
  // ModelNeeded cache of the form '|name=1|name=0': Setup may call Check several times, and hashing a model takes a while.
  ModelChecks: String;

// Whether to download a model: no if {app}\models already has a file with the same SHA-256 (app update).
function ModelNeeded(const FileName, Hash: String): Boolean;
var
  Path: String;
  P: Integer;
begin
  P := Pos('|' + FileName + '=', ModelChecks);
  if P > 0 then
  begin
    Result := ModelChecks[P + Length(FileName) + 2] = '1';
    Exit;
  end;
  Path := ExpandConstant('{app}\models\') + FileName;
  Result := True;
  if FileExists(Path) then
    try
      Result := CompareText(GetSHA256OfFile(Path), Hash) <> 0;
    except
      Result := True;
    end;
  if Result then
    ModelChecks := ModelChecks + '|' + FileName + '=1'
  else
    ModelChecks := ModelChecks + '|' + FileName + '=0';
end;

// The user PATH when installing for the current user, the system PATH when installing for all users.
function EnvironmentRoot: Integer;
begin
  if IsAdminInstallMode then
    Result := HKEY_LOCAL_MACHINE
  else
    Result := HKEY_CURRENT_USER;
end;

function EnvironmentKey: String;
begin
  if IsAdminInstallMode then
    Result := SystemEnvironmentKey
  else
    Result := UserEnvironmentKey;
end;

procedure AddToPath(const Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Paths) then
    Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    Exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  if not RegWriteExpandStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Paths + Dir) then
    Log('Could not add ' + Dir + ' to PATH');
end;

procedure RemoveFromPath(const Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Paths) then
    Exit;
  // Wrap in ";" to match the whole folder rather than part of another path.
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then
    Exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  if not RegWriteExpandStringValue(EnvironmentRoot, EnvironmentKey, 'Path', Paths) then
    Log('Could not remove ' + Dir + ' from PATH');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if WizardIsTaskSelected('path') then
      AddToPath(ExpandConstant('{app}'))
    else
      RemoveFromPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;

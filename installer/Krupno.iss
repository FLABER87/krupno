; Krupno installer. It carries only the app itself (self-contained publish with .NET inside); the models are
; downloaded during installation from the models-v2 release and verified by SHA-256. The wizard speaks the languages of
; the app window: Inno Setup's own messages files, four unofficial ones in languages\ (taken from the is-6_7_1 tag of
; jrsoftware/issrc, as Inno Setup doesn't install them) and our text in messages.iss.
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
; Windows ML needs Windows 10 1903 (build 18362) or later.
MinVersion=10.0.18362
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

; Without a language matching Windows, Setup asks which one to use.
ShowLanguageDialog=auto

; The same languages as the app window (src/Krupno.App/Infrastructure/UiLanguage.cs); the names are the culture names
; without the hyphen. Setup picks the language whose ID matches Windows, or else the first one with the same primary
; language, so Hong Kong and Macau get Traditional Chinese entries of their own (zhhk, zhmo, IDs in [LangOptions]);
; otherwise they would get Simplified Chinese, the first Chinese one.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "id"; MessagesFile: "languages\Indonesian.isl"
Name: "vi"; MessagesFile: "languages\Vietnamese.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "zhhans"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "zhhant"; MessagesFile: "languages\ChineseTraditional.isl"
Name: "zhhk"; MessagesFile: "languages\ChineseTraditional.isl"
Name: "zhmo"; MessagesFile: "languages\ChineseTraditional.isl"

[LangOptions]
zhhk.LanguageName=繁體中文（香港）
zhhk.LanguageID=$0C04
zhmo.LanguageName=繁體中文（澳門）
zhmo.LanguageID=$1404

#include "messages.iss"

[Types]
Name: "full"; Description: "{cm:TypeFull}"
Name: "compact"; Description: "{cm:TypeCompact}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

; Models are listed in the same order and with the same names as in the app window (ViewModels/ModelItem.cs).
[Components]
Name: "app"; Description: "{cm:ComponentApp}"; Types: full compact custom; Flags: fixed
Name: "models"; Description: "{cm:ComponentModels}"; Types: full compact
Name: "models\realplksr"; Description: "{cm:ModelRealPlksr}"; Types: full compact
Name: "models\dat2fp16"; Description: "{cm:ModelDat2Fp16}"; Types: full
Name: "models\ultrasharp"; Description: "{cm:ModelUltraSharp}"; Types: full
Name: "models\dat2"; Description: "{cm:ModelDat2}"; Types: full
Name: "models\esrganfp16"; Description: "{cm:ModelEsrganFp16}"; Types: full
Name: "models\esrgan"; Description: "{cm:ModelEsrgan}"; Types: full
Name: "models\general"; Description: "{cm:ModelGeneral}"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "path"; Description: "{cm:TaskPath}"; GroupDescription: "{cm:GroupCommandLine}"
Name: "contextmenu"; Description: "{cm:TaskContextMenu}"; GroupDescription: "{cm:GroupExplorer}"
Name: "updatecheck"; Description: "{cm:TaskUpdateCheck}"; GroupDescription: "{cm:GroupUpdates}"

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

; With a command string Explorer starts one Krupno.exe per selected item. Each one after the first hands its file to
; the window already open (src/Krupno.App/Infrastructure/SingleInstance.cs), so a selection lands in one queue.
; "Document" is Explorer's default: the item is offered for a selection of up to 15 items, which keeps a large
; selection from starting hundreds of processes at once ("Player" would offer it for any number).
; If the checkbox is cleared on reinstall, the item is removed (deletekey with Tasks: not contextmenu).
[Registry]
Root: HKA; Subkey: "{#ImageVerb}"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; Flags: deletekey; Tasks: not contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueData: "{cm:ContextMenu}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Krupno.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Document"; Tasks: contextmenu
Root: HKA; Subkey: "{#ImageVerb}\command"; ValueType: string; ValueData: """{app}\Krupno.exe"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueData: "{cm:ContextMenu}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Krupno.exe,0"; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Document"; Tasks: contextmenu
Root: HKA; Subkey: "{#FolderVerb}\command"; ValueType: string; ValueData: """{app}\Krupno.exe"" ""%1"""; Tasks: contextmenu
; The update check (src/Krupno.App/Infrastructure/UpdateCheck.cs) as chosen here: the app's default until the user changes
; "Check for updates" in the window. With its time: a check turned off in an installation stays off even if the user had
; turned it on in the window before, and the user can turn it on there again afterwards.
Root: HKA; Subkey: "Software\Krupno"; ValueType: dword; ValueName: "CheckForUpdates"; ValueData: 1; Flags: uninsdeletekey; Tasks: updatecheck
Root: HKA; Subkey: "Software\Krupno"; ValueType: dword; ValueName: "CheckForUpdates"; ValueData: 0; Flags: uninsdeletekey; Tasks: not updatecheck
Root: HKA; Subkey: "Software\Krupno"; ValueType: string; ValueName: "CheckForUpdatesSetAt"; ValueData: "{code:InstallStamp}"; Flags: uninsdeletekey

[UninstallDelete]
; The accelerators' compiled models: up to 2 GB, and the app compiles them again when needed.
Type: filesandordirs; Name: "{localappdata}\Krupno\cache"

[Run]
Filename: "{app}\Krupno.exe"; Description: "{cm:LaunchProgram,Krupno}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  UserEnvironmentKey = 'Environment';
  SystemEnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

  // Each language of the wizard: the app's culture name (UiLanguage.cs) and the Windows language IDs for which the app
  // finds that translation by itself, separated by "/": a primary language ID for a neutral culture (any German is "de"),
  // full ones for Brazilian Portuguese (.NET doesn't take pt-BR for Portugal's pt-PT) and for each Chinese script, which
  // share the primary ID: Simplified for China and Singapore, Traditional for Taiwan, Hong Kong and Macau.
  LanguageTable = '|en=en,$09|uk=uk,$22|ru=ru,$19|pl=pl,$15|de=de,$07|fr=fr,$0C|it=it,$10|es=es,$0A|ptbr=pt-BR,$0416|' +
    'tr=tr,$1F|id=id,$21|vi=vi,$2A|ja=ja,$11|ko=ko,$12|zhhans=zh-Hans,$0804/$1004|zhhant=zh-Hant,$0404/$0C04/$1404|' +
    'zhhk=zh-Hant,$0404/$0C04/$1404|zhmo=zh-Hant,$0404/$0C04/$1404|';

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

// The app shows the Windows language when it has a translation for it. When the wizard runs in a language the app wouldn't
// pick by itself (one chosen in the language dialog, as Windows' own isn't translated, or Brazilian Portuguese on a
// Portuguese Windows), the app is told to use it too: on the first install only, as settings.json doesn't exist yet then,
// so a later choice under "Settings" in the app stays the user's.
procedure SaveLanguageForApp;
var
  Entry, Culture, Ids, Path: String;
  P, Id: Integer;
begin
  P := Pos('|' + ActiveLanguage + '=', LanguageTable);
  if P = 0 then
    Exit;
  Entry := Copy(LanguageTable, P + Length(ActiveLanguage) + 2, MaxInt);
  Entry := Copy(Entry, 1, Pos('|', Entry) - 1);
  Culture := Copy(Entry, 1, Pos(',', Entry) - 1);
  Ids := Copy(Entry, Pos(',', Entry) + 1, MaxInt) + '/';
  while Ids <> '' do
  begin
    Id := StrToInt(Copy(Ids, 1, Pos('/', Ids) - 1));
    Delete(Ids, 1, Pos('/', Ids));
    if ((Id > $3FF) and (GetUILanguage = Id)) or ((Id <= $3FF) and ((GetUILanguage and $3FF) = Id)) then
      Exit;
  end;
  // When installing for all users, this is the settings of the account that runs Setup.
  Path := ExpandConstant('{localappdata}\Krupno\settings.json');
  if FileExists(Path) then
    Exit;
  if not ForceDirectories(ExtractFileDir(Path)) or
     not SaveStringToFile(Path, '{ "Language": "' + Culture + '" }', False) then
    Log('Could not write ' + Path)
  else
    Log('The app will start in ' + Culture + ', the language chosen for Setup');
end;

// When this installation made its update check choice (the [Registry] entries): the app tells a check turned off in this
// installation from one turned off in an earlier one, after which the user may have turned it on in the window again.
function InstallStamp(Param: String): String;
begin
  Result := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('path') then
      AddToPath(ExpandConstant('{app}'))
    else
      RemoveFromPath(ExpandConstant('{app}'));
    SaveLanguageForApp;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;

; Accessibility Mod Manager - Inno Setup Script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)

#define MyAppName "Accessibility Mod Manager"
#ifndef MyAppVersion
  #define MyAppVersion "1.15.0"
#endif
#define MyAppPublisher "Amethyst"
#define MyAppExeName "AccessibilityModManager.App.exe"
#define MyAppURL "https://github.com/RealAmethyst/AccessibilityModManager"
#define DotNetVersion "10.0"
#define DotNetDownloadUrl "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"

[Setup]
AppId={{A3C5E8F1-7B2D-4A6E-9F0C-1D8E3B5A7C2F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=AccessibilityModManager-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
; Source-available license — users must accept before installing.
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Application directory - framework-dependent build
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; No 'skipifsilent': the in-app updater runs this installer with /SILENT and relies on this entry
; to relaunch the app after the upgrade. postinstall still shows the "Launch" checkbox on the
; Finished page for a normal interactive install.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall

[Code]
// Check for the .NET WPF runtime by enumerating its standard install directory directly
// — no Exec, no cmd, no findstr. The earlier shell-out pattern (`cmd /c dotnet
// --list-runtimes | findstr ...`) tripped Defender's ML classifier, which flags installers
// that do command-output piping + keyword scanning as credential-stealer-shaped.
function HasRuntimeUnder(RuntimeRoot: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(RuntimeRoot) then exit;

  if FindFirst(RuntimeRoot + '\{#DotNetVersion}.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Mirrors how the published win-x64 apphost actually finds its runtime, because agreeing with
// the app is the only thing that matters here: DOTNET_ROOT_X64 wins, then DOTNET_ROOT, then the
// default x64 location. When an override IS set it decides the answer outright — the app will
// look there and nowhere else, so finding a runtime somewhere else would be a false pass.
//
// Deliberately NOT accepted: a 32-bit runtime (this app is x64 and cannot use it), and a bare
// %LOCALAPPDATA% copy that no environment variable points at (the apphost won't discover it).
// Checking only Program Files was the original bug — it failed people whose runtime lives
// somewhere legitimate — but being over-generous is its own failure: setup approves, then the
// app won't start (audit finding 41).
function IsDotNetInstalled(): Boolean;
var
  Root: String;
begin
  Root := ExpandConstant('{%DOTNET_ROOT_X64}');
  if Root = '' then
    Root := ExpandConstant('{%DOTNET_ROOT}');

  if Root <> '' then
  begin
    Result := HasRuntimeUnder(RemoveBackslashUnlessRoot(Root) + '\shared\Microsoft.WindowsDesktop.App');
    exit;
  end;

  // No override, so the apphost consults the REGISTERED x64 install location before falling back
  // to a default path. Skipping this rejected anyone who installed the runtime somewhere other
  // than Program Files through the normal installer.
  if RegQueryStringValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) and (Root <> '') then
  begin
    Result := HasRuntimeUnder(RemoveBackslashUnlessRoot(Root) + '\shared\Microsoft.WindowsDesktop.App');
    if Result then exit;
  end;

  Result := HasRuntimeUnder(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'));
  if Result then exit;

  // Arm64 Windows runs this x64 build through emulation, and there the x64 runtime lives in an
  // x64 subfolder rather than the native root. ArchitecturesInstallIn64BitMode is x64compatible,
  // which includes Arm64, so setup can legitimately land there.
  Result := HasRuntimeUnder(ExpandConstant('{commonpf64}\dotnet\x64\shared\Microsoft.WindowsDesktop.App'));
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNetInstalled() then
  begin
    if MsgBox('.NET {#DotNetVersion} Desktop Runtime is required but was not found.' + #13#10 + #13#10 +
             'Would you like to open the .NET download page?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', '{#DotNetDownloadUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataPath: String;
begin
  // After the program files have been removed, optionally wipe user data.
  if CurUninstallStep = usPostUninstall then
  begin
    AppDataPath := ExpandConstant('{localappdata}\AccessibilityModManager');
    if DirExists(AppDataPath) then
    begin
      // The receipts are the ONLY record of which files each mod put in a game folder and which
      // backup belongs to which change. Removing them doesn't uninstall those mods — it makes
      // them unremovable, because nothing is left that knows what to take out. The backups
      // themselves live in each GAME folder and are not touched here; they simply become
      // orphaned. Say exactly that: a data-wipe prompt that overstates OR understates what it
      // does is how someone ends up with a modded game and no way back (audit finding 41).
      if MsgBox('Also remove all settings, plugin states, install receipts, and logs?' + #13#10 + #13#10 +
               'Important: if you still have mods installed in any game, uninstall them FIRST.' + #13#10 +
               'This data includes the record of what each mod changed and where its backups are.' + #13#10 +
               'Without it, the manager can no longer remove those mods or restore your original' + #13#10 +
               'files. The backup folders stay in your game folders, but nothing can use them.' + #13#10 + #13#10 +
               'Choose No to keep this data so a future reinstall picks up where you left off.' + #13#10 +
               'Choose Yes to remove the application''s own data.',
               mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(AppDataPath, True, True, True);
      end;
    end;
  end;
end;

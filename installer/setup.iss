; Accessibility Mod Manager - Inno Setup Script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)

#define MyAppName "Accessibility Mod Manager"
#ifndef MyAppVersion
  #define MyAppVersion "1.9.2"
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
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Check for the .NET WPF runtime by enumerating its standard install directory directly
// — no Exec, no cmd, no findstr. The earlier shell-out pattern (`cmd /c dotnet
// --list-runtimes | findstr ...`) tripped Defender's ML classifier, which flags installers
// that do command-output piping + keyword scanning as credential-stealer-shaped.
function IsDotNetInstalled(): Boolean;
var
  RuntimeRoot: String;
  FindRec: TFindRec;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
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
      if MsgBox('Also remove all settings, plugin states, install receipts, and logs?' + #13#10 + #13#10 +
               'Choose No to keep this data so a future reinstall picks up where you left off.' + #13#10 +
               'Choose Yes to fully remove every trace of the application.',
               mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(AppDataPath, True, True, True);
      end;
    end;
  end;
end;

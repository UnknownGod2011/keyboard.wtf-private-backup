#ifndef MyAppName
  #define MyAppName "keyboard.wtf"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.4.0"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "keyboard.wtf"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "keyboard.wtf.exe"
#endif
#ifndef MyAppId
  #define MyAppId "{{A2290D45-0672-4FC5-8CFB-AEB92B1EF945}"
#endif
#ifndef MyDefaultDirName
  #define MyDefaultDirName "{localappdata}\Programs\keyboard.wtf"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "keyboard-wtf-setup"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\dist\release\keyboard-wtf-win-x64"
#endif
#ifndef MyRunValueName
  #define MyRunValueName "keyboard.wtf"
#endif
#ifndef MyDefaultGroupName
  #define MyDefaultGroupName "keyboard.wtf"
#endif
#ifndef MyShortcutName
  #define MyShortcutName "keyboard.wtf"
#endif
#ifndef MyUserDataDir
  #define MyUserDataDir "keyboard.wtf"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={#MyDefaultDirName}
DefaultGroupName={#MyDefaultGroupName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist\installer
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=keyboard.wtf Windows installer
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Registry]
#ifndef MySkipStartup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyRunValueName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue
#endif

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch keyboard.wtf and finish setup"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function HasCommandLineParameter(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and
     HasCommandLineParameter('/REMOVEUSERDATA') then
  begin
    DelTree(ExpandConstant('{localappdata}\{#MyUserDataDir}'), True, True, True);
  end;
end;

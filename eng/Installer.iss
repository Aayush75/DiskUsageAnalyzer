#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
[Setup]
AppId={{7792FB74-6CE5-49CF-9BDC-E8515345C27F}
AppName=DiskUsageAnalyzer
AppVersion={#AppVersion}
AppPublisher=DiskUsageAnalyzer contributors
DefaultDirName={localappdata}\Programs\DiskUsageAnalyzer
DefaultGroupName=DiskUsageAnalyzer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=DiskUsageAnalyzer-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\DiskUsageAnalyzer.App.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{userprograms}\DiskUsageAnalyzer"; Filename: "{app}\DiskUsageAnalyzer.App.exe"
[Run]
Filename: "{app}\DiskUsageAnalyzer.App.exe"; Description: "Open DiskUsageAnalyzer"; Flags: nowait postinstall skipifsilent unchecked
[Code]
function InitializeUninstall(): Boolean;
begin
  Result := MsgBox('Uninstall DiskUsageAnalyzer? Only installed program files will be removed. Scan history, recovery folders, and your files will be kept.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

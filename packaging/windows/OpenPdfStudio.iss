#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\publish\win-x64"
#endif
#ifndef RepoRoot
  #define RepoRoot "..\.."
#endif

#define MyAppName "OpenPdfStudio"
#define MyAppPublisher "Younis Iqbal"
#define MyAppURL "https://openpdfstudio.github.io/"
#define MyAppExeName "OpenPdfStudio.exe"

[Setup]
AppId={{8F3A1C2E-6B9D-4E71-A5C8-2D9F4B7E1A06}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupIconFile={#RepoRoot}\Assets\app-logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#RepoRoot}
OutputBaseFilename=OpenPdfStudio-win-x64-setup
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "OpenPdfStudio.pdf"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\OpenPdfStudio.pdf"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\OpenPdfStudio.pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\OpenPdfStudio.pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Open {#MyAppName}"; Flags: nowait postinstall skipifsilent

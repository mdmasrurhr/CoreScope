; CoreScope installer (Inno Setup 6). Built by tools\package.ps1, which passes AppVersion, SourceDir and OutputDir.
; Per-user install: no administrator prompt, and winget can install it silently.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\CoreScope-" + AppVersion + "-win-x64\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{926E8A2A-77BD-4940-81A5-601653D4957E}
AppName=CoreScope
AppVersion={#AppVersion}
AppVerName=CoreScope {#AppVersion}
AppPublisher=Rakin
AppComments=Hardware information, live sensors and PC health
DefaultDirName={localappdata}\Programs\CoreScope
DefaultGroupName=CoreScope
DisableProgramGroupPage=yes
DisableDirPage=auto
DirExistsWarning=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=CoreScope-{#AppVersion}-Setup
SetupIconFile=..\..\src\CoreScope\Assets\CoreScope.ico
UninstallDisplayIcon={app}\CoreScope.exe
UninstallDisplayName=CoreScope
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
; CoreScope holds this mutex while running (see App.xaml.cs). Setup then asks the user to exit it first, which also
; covers a copy running as administrator that Setup itself is not allowed to close.
AppMutex=Local\CoreScope.Mutex
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CoreScope

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CoreScope"; Filename: "{app}\CoreScope.exe"; Comment: "Hardware information, live sensors and PC health"
Name: "{autodesktop}\CoreScope"; Filename: "{app}\CoreScope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CoreScope.exe"; Description: "Open CoreScope"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove "Start with Windows" entries the app may have created. The logon task may need admin rights;
; if that fails it is skipped quietly.
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v CoreScope /f"; Flags: runhidden; RunOnceId: "RunKey"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN CoreScope /F"; Flags: runhidden; RunOnceId: "LogonTask"

[UninstallDelete]
; Log and self-test files written next to the exe. Settings in %LocalAppData%\CoreScope are kept.
Type: files; Name: "{app}\corescope.log"
Type: files; Name: "{app}\selftest.json"
Type: files; Name: "{app}\sensors.txt"

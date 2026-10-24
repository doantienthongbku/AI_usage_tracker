; Per-user installer (no admin prompt). Built by build.ps1 with Inno Setup 6.
#ifndef Version
  #define Version "1.1.0"
#endif

[Setup]
AppId={{8B2D6E3A-5C41-4F7B-9E0D-2A6C1F3B7D90}
AppName=AI Usage Tracker
AppVersion={#Version}
DefaultDirName={localappdata}\Programs\AI Usage Tracker
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=AIUsageTracker-Setup-{#Version}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=AI Usage Tracker
UninstallDisplayIcon={app}\AIUsageTracker.exe
CloseApplications=force

[Tasks]
Name: startup; Description: "Start AI Usage Tracker when I sign in to Windows"

[Files]
Source: "..\publish\AIUsageTracker.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AI Usage Tracker"; Filename: "{app}\AIUsageTracker.exe"

[Registry]
; The same value the app's "Open at Login" switch writes. The second entry removes it on uninstall,
; also when it was the app that set it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AIUsageTracker"; ValueData: """{app}\AIUsageTracker.exe"""; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "AIUsageTracker"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\AIUsageTracker.exe"; Description: "Start AI Usage Tracker now"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im AIUsageTracker.exe /f"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AIUsageTracker"

; ExLlamaSharp — Inno Setup 6 script
; Build: packaging\Build-Installer.ps1 (calls ISCC) or:
;   & "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe" packaging\ExLlamaSharp.iss

#define MyAppName "ExLlamaSharp"
#define MyAppVersion "1.2.0-beta"
#define MyAppPublisher "ExLlamaSharp"
#define MyAppURL "http://127.0.0.1:14563"
; Stage folder produced by Build-Installer.ps1 (relative to this .iss)
#define StageDir "..\publish\installer"

[Setup]
AppId={{8F3E2A91-6C4B-4D7E-9A12-E5B8C0D4F617}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion=1.2.0
VersionInfoProductVersion=1.2.0
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\ExLlamaSharp
DefaultGroupName=ExLlamaSharp
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=ExLlamaSharp-Setup-win-x64
SetupIconFile=assets\exllamasharp.ico
UninstallDisplayIcon={app}\exllamasharp.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
; Real work is done by Install.ps1 (copy, venv, bundled GPU wheels, service)
AllowNoIcons=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0
; Wheels can exceed 2 GB combined; Inno 6.3+ supports large setups.
DiskSpanning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; App payload (compressed)
Source: "{#StageDir}\payload\*"; DestDir: "{tmp}\ExLlamaSharpSetup\payload"; \
  Excludes: "offline-wheels\*,redist\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
; GPU wheels are already compressed — store as-is (faster compile, no extra shrink)
Source: "{#StageDir}\payload\offline-wheels\*"; DestDir: "{tmp}\ExLlamaSharpSetup\payload\offline-wheels"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall nocompression skipifsourcedoesntexist
Source: "{#StageDir}\payload\redist\*"; DestDir: "{tmp}\ExLlamaSharpSetup\payload\redist"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall nocompression skipifsourcedoesntexist
; Installer scripts
Source: "{#StageDir}\Install.ps1"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall
Source: "{#StageDir}\Install.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Uninstall.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Install-Clean.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\README.txt"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\exllamasharp.ico"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Setup-Exl3Python.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\scripts\*"; DestDir: "{tmp}\ExLlamaSharpSetup\scripts"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\ExLlamaSharp"; Filename: "{cmd}"; Parameters: "/C start {#MyAppURL}"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\ExLlamaSharp Tray"; Filename: "{app}\ExLlamaSharp.Tray.exe"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\Uninstall ExLlamaSharp"; Filename: "{uninstallexe}"; IconFilename: "{app}\exllamasharp.ico"
Name: "{autodesktop}\ExLlamaSharp"; Filename: "{cmd}"; Parameters: "/C start {#MyAppURL}"; IconFilename: "{app}\exllamasharp.ico"; Tasks: desktopicon

[Run]
; Main installer (Admin already granted by PrivilegesRequired=admin)
; Note: Do NOT pass -InstallDir here, causes truncation with spaces. Install.ps1 uses default.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\ExLlamaSharpSetup\Install.ps1"" -Unattended"; \
  StatusMsg: "Installing ExLlamaSharp (service, bundled ExLlamaV3 CUDA, PyTorch download)..."; \
  Flags: waituntilterminated

Filename: "{#MyAppURL}"; Description: "Open ExLlamaSharp Admin UI"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process ExLlamaSharp* -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue; Stop-Service ExLlamaSharp -Force -EA SilentlyContinue; Start-Sleep 2; sc.exe delete ExLlamaSharp; Remove-NetFirewallRule -DisplayName 'ExLlamaSharp Server' -EA SilentlyContinue"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "StopExLlamaSharp"

; Payload is already embedded in Setup.exe — do not probe StageDir on the target PC.

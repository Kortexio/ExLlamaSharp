; ExLlamaSharp — Inno Setup 6 script
; Build: packaging\Build-Installer.ps1 (calls ISCC) or:
;   & "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe" packaging\ExLlamaSharp.iss

#define MyAppName "ExLlamaSharp"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "ExLlamaSharp"
#define MyAppURL "http://127.0.0.1:14563"
; Stage folder produced by Build-Installer.ps1 (relative to this .iss)
#define StageDir "..\publish\installer"

[Setup]
AppId={{8F3E2A91-6C4B-4D7E-9A12-E5B8C0D4F617}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
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
; Real work is done by Install.ps1 (copy, venv, PyTorch, service)
AllowNoIcons=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "pytorch"; Description: "Download and install PyTorch + CUDA (~2-3 GB, 5-10 min)"; GroupDescription: "GPU runtime:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Extract full stage (payload + Install.ps1) to temp; Install.ps1 copies into {app}
Source: "{#StageDir}\*"; DestDir: "{tmp}\ExLlamaSharpSetup"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall

[Icons]
Name: "{group}\ExLlamaSharp"; Filename: "{cmd}"; Parameters: "/C start {#MyAppURL}"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\ExLlamaSharp Tray"; Filename: "{app}\ExLlamaSharp.Tray.exe"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\Uninstall ExLlamaSharp"; Filename: "{uninstallexe}"; IconFilename: "{app}\exllamasharp.ico"
Name: "{autodesktop}\ExLlamaSharp"; Filename: "{cmd}"; Parameters: "/C start {#MyAppURL}"; IconFilename: "{app}\exllamasharp.ico"; Tasks: desktopicon

[Run]
; Main installer (Admin already granted by PrivilegesRequired=admin)
; Note: Do NOT pass -InstallDir here, causes truncation with spaces. Install.ps1 uses default.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\ExLlamaSharpSetup\Install.ps1"" -Unattended {code:PyTorchArgs}"; \
  StatusMsg: "Installing ExLlamaSharp (service, firewall, optional PyTorch download)..."; \
  Flags: waituntilterminated

Filename: "{#MyAppURL}"; Description: "Open ExLlamaSharp Admin UI"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process ExLlamaSharp* -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue; Stop-Service ExLlamaSharp -Force -EA SilentlyContinue; Start-Sleep 2; sc.exe delete ExLlamaSharp; Remove-NetFirewallRule -DisplayName 'ExLlamaSharp Server' -EA SilentlyContinue"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "StopExLlamaSharp"

[Code]
function PyTorchArgs(Param: String): String;
begin
  if not WizardIsTaskSelected('pytorch') then
    Result := '-SkipPyTorch'
  else
    Result := '';
end;

// Do NOT check StageDir on the target PC — payload is already embedded in Setup.exe.
// (A previous InitializeSetup check caused silent install exit code 1.)

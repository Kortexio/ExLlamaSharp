; ExLlamaSharp — Inno Setup 6 script
; Build: packaging\Build-Installer.ps1 (calls ISCC) or:
;   & "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe" packaging\ExLlamaSharp.iss

#define MyAppName "ExLlamaSharp"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "ExLlamaSharp"
#define MyAppURL "http://127.0.0.1:14563"
; Stage folder produced by Build-Installer.ps1 (relative to this .iss)
#define StageDir "..\publish\installer"

[Setup]
AppId={{8F3E2A91-6C4B-4D7E-9A12-E5B8C0D4F617}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion=1.3.0
VersionInfoProductVersion=1.3.0
AppMutex=Global\ExLlamaSharp.Server
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
Name: "desktopicon"; Description: "Create desktop shortcut (starts Tray + server)"; GroupDescription: "Additional icons:"; Flags: checked

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
Source: "{#StageDir}\Uninstall.ps1"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Install.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Uninstall.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Install-Clean.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\README.txt"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\exllamasharp.ico"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\Setup-Exl3Python.bat"; DestDir: "{tmp}\ExLlamaSharpSetup"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist
Source: "{#StageDir}\scripts\*"; DestDir: "{tmp}\ExLlamaSharpSetup\scripts"; \
  Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall skipifsourcedoesntexist

[Icons]
; Primary: Tray starts the server (service or EXE fallback) and lives in the notification area
Name: "{group}\ExLlamaSharp"; Filename: "{app}\ExLlamaSharp.Tray.exe"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\ExLlamaSharp Tray"; Filename: "{app}\ExLlamaSharp.Tray.exe"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\Open Admin UI"; Filename: "{cmd}"; Parameters: "/C start {#MyAppURL}"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"
Name: "{group}\Uninstall ExLlamaSharp"; Filename: "{uninstallexe}"; IconFilename: "{app}\exllamasharp.ico"
Name: "{autodesktop}\ExLlamaSharp"; Filename: "{app}\ExLlamaSharp.Tray.exe"; IconFilename: "{app}\exllamasharp.ico"; WorkingDir: "{app}"; Tasks: desktopicon
; Autostart is the ExLlamaSharpTrayLogon scheduled task created by Install.ps1 (desktop mode).

; Install.ps1 runs from [Code] so a non-zero exit surfaces as an error dialog (plain [Run] ignores exit codes).
[Run]
Filename: "{#MyAppURL}"; Description: "Open ExLlamaSharp Admin UI"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall.ps1"" -InstallDir ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "UninstallExLlamaSharp"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Ps, Args: String;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Installing ExLlamaSharp (service, ExLlamaV3 CUDA, PyTorch download)...';
    WizardForm.StatusLabel.Update;
    Ps := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    Args := ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{tmp}\ExLlamaSharpSetup\Install.ps1" -Unattended -InstallDir "{app}" -HostMode desktop');
    if not Exec(Ps, Args, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Could not start Install.ps1 (PowerShell).', mbError, MB_OK);
      Abort;
    end
    else if ResultCode <> 0 then
    begin
      MsgBox('ExLlamaSharp installation script failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
        'Details: %TEMP%\ExLlamaSharp-Install.log' + #13#10 + #13#10 +
        'Uninstall ExLlamaSharp, then re-run Setup as Administrator.', mbError, MB_OK);
      Abort;
    end;
  end;
end;

; Payload is already embedded in Setup.exe — do not probe StageDir on the target PC.

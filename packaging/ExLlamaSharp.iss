; ExLlamaSharp — Inno Setup 6 script
; Build: packaging\Build-Installer.ps1 (calls ISCC) or:
;   & "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe" packaging\ExLlamaSharp.iss

#define MyAppName "ExLlamaSharp"
#define MyAppVersion "1.3.2.1"
#define MyAppPublisher "ExLlamaSharp"
#define MyAppURL "http://127.0.0.1:14563"
; Stage folder produced by Build-Installer.ps1 (relative to this .iss)
#define StageDir "..\publish\installer"

[Setup]
AppId={{8F3E2A91-6C4B-4D7E-9A12-E5B8C0D4F617}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion=1.3.2.1
VersionInfoProductVersion=1.3.2.1
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
Name: "desktopicon"; Description: "Create desktop shortcut (starts Tray + server)"; GroupDescription: "Additional icons:"

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
var
  HostPage: TWizardPage;
  RadioDesktop: TNewRadioButton;
  RadioHeadless: TNewRadioButton;
  AccountEdit: TNewEdit;
  PasswordEdit: TNewEdit;
  AccountLabel: TNewStaticText;
  PasswordLabel: TNewStaticText;

procedure HostModeChanged(Sender: TObject);
begin
  AccountEdit.Enabled := RadioHeadless.Checked;
  PasswordEdit.Enabled := RadioHeadless.Checked;
end;

procedure InitializeWizard;
begin
  HostPage := CreateCustomPage(wpSelectDir, 'Host mode',
    'Choose how ExLlamaSharp starts after install.');

  RadioDesktop := TNewRadioButton.Create(HostPage);
  RadioDesktop.Parent := HostPage.Surface;
  RadioDesktop.Caption := 'Desktop (recommended) — Tray starts the Server in your user session';
  RadioDesktop.Checked := True;
  RadioDesktop.Top := ScaleY(8);
  RadioDesktop.Width := HostPage.SurfaceWidth;
  RadioDesktop.OnClick := @HostModeChanged;

  RadioHeadless := TNewRadioButton.Create(HostPage);
  RadioHeadless.Parent := HostPage.Surface;
  RadioHeadless.Caption := 'Headless — Windows service with a GPU-capable account (not LocalSystem)';
  RadioHeadless.Top := RadioDesktop.Top + ScaleY(28);
  RadioHeadless.Width := HostPage.SurfaceWidth;
  RadioHeadless.OnClick := @HostModeChanged;

  AccountLabel := TNewStaticText.Create(HostPage);
  AccountLabel.Parent := HostPage.Surface;
  AccountLabel.Caption := 'Service account (DOMAIN\user or .\localuser):';
  AccountLabel.Top := RadioHeadless.Top + ScaleY(32);
  AccountLabel.Width := HostPage.SurfaceWidth;

  AccountEdit := TNewEdit.Create(HostPage);
  AccountEdit.Parent := HostPage.Surface;
  AccountEdit.Top := AccountLabel.Top + ScaleY(18);
  AccountEdit.Width := HostPage.SurfaceWidth;
  AccountEdit.Enabled := False;

  PasswordLabel := TNewStaticText.Create(HostPage);
  PasswordLabel.Parent := HostPage.Surface;
  PasswordLabel.Caption := 'Password (written to a temp file, not the command line):';
  PasswordLabel.Top := AccountEdit.Top + ScaleY(32);
  PasswordLabel.Width := HostPage.SurfaceWidth;

  PasswordEdit := TNewEdit.Create(HostPage);
  PasswordEdit.Parent := HostPage.Surface;
  PasswordEdit.Top := PasswordLabel.Top + ScaleY(18);
  PasswordEdit.Width := HostPage.SurfaceWidth;
  PasswordEdit.PasswordChar := '*';
  PasswordEdit.Enabled := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = HostPage.ID then
  begin
    if RadioHeadless.Checked then
    begin
      if Trim(AccountEdit.Text) = '' then
      begin
        MsgBox('Headless mode requires a Windows account that can use the GPU (not LocalSystem).', mbError, MB_OK);
        Result := False;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Ps, Args, Mode, PwdFile: String;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := 'Installing ExLlamaSharp (service, ExLlamaV3 CUDA, PyTorch download)...';
    WizardForm.StatusLabel.Update;
    Ps := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    if RadioHeadless.Checked then
    begin
      Mode := 'headless';
      PwdFile := ExpandConstant('{tmp}\exls-svc.pwd');
      SaveStringToFile(PwdFile, PasswordEdit.Text, False);
      Args := ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{tmp}\ExLlamaSharpSetup\Install.ps1" -Unattended -InstallDir "{app}" -HostMode headless -ServiceAccount "') +
        AccountEdit.Text + '" -ServicePasswordFile "' + PwdFile + '"';
    end
    else
    begin
      Mode := 'desktop';
      Args := ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{tmp}\ExLlamaSharpSetup\Install.ps1" -Unattended -InstallDir "{app}" -HostMode desktop');
    end;
    Log('Install.ps1 host-mode=' + Mode);
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

// Payload is already embedded in Setup.exe — do not probe StageDir on the target PC.

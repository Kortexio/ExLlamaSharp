# ExLlamaSharp - Guia de Implantação

**Versão:** 2.0  
**Audiência:** Administradores de TI, DevOps

---

## Pré-requisitos

### Hardware
- CPU x64, 8 GB RAM (16 GB+ recomendado)
- NVIDIA GPU 6 GB+ VRAM para inferência
- 50 GB+ disco (app + modelos)

### Software
- Windows 10 20H1+ ou Windows 11
- Python 3.10+ no PATH
- Driver NVIDIA com CUDA 12.8+
- Porta TCP 14563 livre
- Administrador local para instalação

### Dependências (instalador)
- Visual C++ Redistributable 2022 x64 (embutido no Setup)
- .NET runtime embutido (self-contained)
- PyTorch CUDA 12.8 no venv (download ~2–3 GB durante Install.ps1)
- ExLlamaV3 `.pyd` + deps + Python/VC (embutidos no Setup)

---

## Distribuição

| Pacote | Arquivo | Notas |
|--------|---------|--------|
| **Recomendado** | `ExLlamaSharp-Setup-win-x64.exe` | App + ExLlamaV3 `.pyd` + deps + Python/VC; PyTorch descarrega na instalação |
| Slim | `Build-Installer.ps1 -SkipBundleWheels` | Só a app; GPU fica para depois |

Build:

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1
# slim: -SkipBundleWheels
```

---

## Instalação

### Interativa

1. Extrair o ZIP
2. Executar `Install.ps1` ou `Install.bat` como Admin
3. Aguardar PyTorch
4. Abrir http://localhost:14563

### Não interativa / remota

```powershell
# No host (Admin)
powershell -NoProfile -ExecutionPolicy Bypass -File Install.ps1 -Unattended

# Remoto (exemplo)
$servers = @("GPU-01", "GPU-02")
$zip = "\\fileserver\Software\ExLlamaSharp\ExLlamaSharp-Setup-win-x64.zip"
foreach ($s in $servers) {
    Copy-Item $zip "\\$s\C$\Temp\exls.zip" -Force
    Invoke-Command -ComputerName $s -ScriptBlock {
        Expand-Archive C:\Temp\exls.zip C:\Temp\ExLlamaSharpSetup -Force
        Set-Location C:\Temp\ExLlamaSharpSetup
        powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Unattended
    }
}
```

### Opções

```powershell
Install.ps1 -SkipPyTorch
Install.ps1 -InstallDir "D:\Apps\ExLlamaSharp"
Install.ps1 -Unattended
```

---

## Pós-instalação

| Item | Local |
|------|--------|
| App | `C:\Program Files\ExLlamaSharp\` |
| venv / PyTorch | `C:\Program Files\ExLlamaSharp\venv\` |
| Dados / modelos / logs | `%ProgramData%\ExLlamaSharp\` |
| Serviço | `ExLlamaSharp` (Automatic) |
| UI | http://localhost:14563 |

```powershell
Get-Service ExLlamaSharp
Invoke-WebRequest http://localhost:14563/health
```

### Reparo GPU

```powershell
& "C:\Program Files\ExLlamaSharp\Setup-Exl3Python.bat"
```

---

## Desinstalação

```powershell
# No pacote ou em Program Files\ExLlamaSharp\scripts
.\Uninstall.bat
# ou
powershell -File Uninstall.ps1 -RemoveData   # também apaga %ProgramData%
```

---

## Troubleshooting

| Sintoma | Ação |
|---------|------|
| Porta 14563 em uso | Parar serviço / processo antigo; `Cleanup-BrokenInstall.ps1` |
| PyTorch falhou | `Setup-Exl3Python.bat` |
| exllamav3 Python não compila (VS 2025) | Ver [vs2025-cuda-issue.md](vs2025-cuda-issue.md) — backend nativo OK |
| Serviço não sobe | Logs em `%ProgramData%\ExLlamaSharp\logs\` |

Mais: [INSTALL.md](INSTALL.md) · [packaging/README.md](../packaging/README.md)

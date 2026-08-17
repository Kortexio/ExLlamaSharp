# Packaging — ExLlamaSharp

## Instalador recomendado (EXE)

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1
# Resultado: publish\ExLlamaSharp-Setup-win-x64.exe
```

Duplo clique no EXE (UAC Admin). Opcao no wizard para incluir PyTorch.

## ZIP (alternativa)

```powershell
# Build (máquina de desenvolvimento)
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1

# Resultado
publish\ExLlamaSharp-Setup-win-x64.zip
```

No host GPU:

1. Extraia o ZIP
2. Clique direito em **Install.ps1** → Executar com PowerShell (Admin)  
   ou **Install.bat** → Executar como administrador
3. Abra http://localhost:14563

O instalador configura automaticamente: arquivos, VC++ Redist, venv Python, PyTorch CUDA, serviço Windows, firewall, atalhos e Tray.

### Opções

```powershell
Install.ps1 -SkipPyTorch
Install.ps1 -InstallDir "D:\Apps\ExLlamaSharp"
Install.ps1 -Unattended
```

### Bundle offline de wheels (ZIP grande)

```powershell
.\Build-Installer.ps1 -BundlePytorch
```

## Scripts neste diretório

| Arquivo | Uso |
|---------|-----|
| `Build-Installer.ps1` | Gera o ZIP de distribuição |
| `Install-ExLlamaSharp.ps1` | Instalador único (copiado como `Install.ps1` no ZIP) |
| `Install.bat` | Launcher Admin → `Install.ps1` |
| `Uninstall.ps1` / `Uninstall.bat` | Desinstalação |
| `Setup-Exl3Python.ps1` / `.bat` | Reparo / reinstalação do PyTorch no venv |
| `Download-OfflineWheels.ps1` | Baixa wheels para `-BundlePytorch` |
| `Download-DemoModel.ps1` | Modelo demo EXL3 |
| `Check-Requirements.ps1` | Pré-checagem |
| `Cleanup-BrokenInstall.ps1` | Limpa serviço/pastas quebrados |
| `build-native-cuda.ps1` / `build-native-stub.ps1` | DLL nativa |

Documentação: [docs/INSTALL.md](../docs/INSTALL.md)


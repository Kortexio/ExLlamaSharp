# Packaging — ExLlamaSharp

## Instalador recomendado (EXE)

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1
# Resultado: publish\ExLlamaSharp-Setup-win-x64.exe
# Inclui wheel ExLlamaV3 com .pyd, deps, Python 3.12 e VC++. PyTorch descarrega na instalação.
```

Duplo clique no EXE (UAC Admin). O runtime GPU vai dentro do Setup — não há download extra do PyPI.

## Build só da app (sem wheels)

```powershell
powershell -ExecutionPolicy Bypass -File packaging\Build-Installer.ps1 -SkipBundleWheels
```

## Scripts neste diretório

| Arquivo | Uso |
|---------|-----|
| `Build-Installer.ps1` | Gera o Setup.exe (bundle GPU por omissão) |
| `Install-ExLlamaSharp.ps1` | Instalador único (copiado como `Install.ps1`) |
| `Install.bat` | Launcher Admin → `Install.ps1` |
| `Uninstall.ps1` / `Uninstall.bat` | Desinstalação |
| `Setup-Exl3Python.ps1` / `.bat` | Reparo do venv (usa `offline-wheels` se existirem) |
| `Repair-Exl3Ext.ps1` | Reinstala só o `.pyd` CUDA |
| `Download-OfflineWheels.ps1` | Baixa wheels + Python/VC para o bundle |
| `Download-DemoModel.ps1` | Modelo demo EXL3 |
| `Check-Requirements.ps1` | Pré-checagem |
| `Cleanup-BrokenInstall.ps1` | Limpa serviço/pastas quebrados |
| `build-native-cuda.ps1` / `build-native-stub.ps1` | DLL nativa |

Documentação: [docs/INSTALL.md](../docs/INSTALL.md)

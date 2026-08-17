# ExLlamaSharp - Guia Rápido de Instalação

**Para usuários finais**

---

## Instalação rápida

### 1. Download
Baixe `ExLlamaSharp-Setup-win-x64.zip` (ou do share interno da TI).

### 2. Instalar
1. Extraia o ZIP
2. Clique direito em **Install.ps1** → Executar com PowerShell (Admin)  
   ou **Install.bat** → Executar como administrador
3. Aguarde o download do PyTorch (~2–3 GB, 5–10 min)
4. Abra **http://localhost:14563**

### 3. Verificar
- Ícone na bandeja do sistema
- Login padrão: `admin` / `changeme` (**altere na primeira vez**)

---

## Tudo OK?

```powershell
Get-Service -Name "ExLlamaSharp"
Invoke-WebRequest http://localhost:14563/health
```

---

## Problemas?

### Serviço não inicia
1. Porta 14563 livre?
2. Logs: `C:\ProgramData\ExLlamaSharp\logs\`
3. `Cleanup-BrokenInstall.ps1` como Admin, depois reinstale

### PyTorch / GPU
```powershell
cd "C:\Program Files\ExLlamaSharp"
.\Setup-Exl3Python.bat
```

### Desinstalar
Execute `Uninstall.bat` como Administrador.

Mais detalhes: [INSTALL.md](INSTALL.md)

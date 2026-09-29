# Compila WtSidebarSetup.exe (instalador grafico) com o install.ps1 e o icone embutidos.
# So precisa ser refeito quando o install.ps1 mudar: o setup sempre baixa a ultima release.
param([string]$Out = (Join-Path $PSScriptRoot 'WtSidebarSetup.exe'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    /out:"$Out" /win32icon:"$root\WtSidebar.ico" `
    /resource:"$root\WtSidebar.ico",WtSidebar.ico /resource:"$root\install.ps1",install.ps1 `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$PSScriptRoot\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "csc falhou ($LASTEXITCODE)" }
Write-Host "OK: $Out"

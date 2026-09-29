# Compila WtSidebar.exe com o compilador C# que ja vem no Windows (.NET Framework 4).
param([string]$Out = (Join-Path $PSScriptRoot 'WtSidebar.exe'))
$ErrorActionPreference = 'Stop'
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:"$PSScriptRoot\WtSidebar.ico" /resource:"$PSScriptRoot\WtSidebar.ico",WtSidebar.ico `
    /out:"$Out" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
    /r:"$wpf\UIAutomationClient.dll" /r:"$wpf\UIAutomationTypes.dll" /r:"$wpf\WindowsBase.dll" `
    "$PSScriptRoot\*.cs"
if ($LASTEXITCODE -ne 0) { throw "csc falhou ($LASTEXITCODE)" }
Write-Host "OK: $Out"

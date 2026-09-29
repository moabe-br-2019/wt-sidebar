# Instalador do WT Sidebar. Baixa o codigo da ultima release do GitHub, compila com o csc que ja vem
# no Windows, copia para %LOCALAPPDATA%\Programs\WtSidebar, cria atalhos no menu Iniciar e na
# inicializacao do Windows e abre o app. Rodar de novo reinstala por cima.
#
#   irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1 | iex
#
# Com opcoes:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1))) -NoStartup
#
#   -Ref v1.2.0   instala essa tag ou branch em vez da ultima release
#   -NoStartup    sem atalho na inicializacao do Windows
#   -NoLaunch     nao abre o app no fim
#   -Uninstall    remove app e atalhos (mantem as pastas recentes)
#   -Update       usado pelo menu "Atualizar" do app: roda escondido, grava log e avisa se falhar
#
# Rodado de dentro de um clone (.\install.ps1, sem -Ref), instala o codigo local.
param([string]$Ref, [switch]$NoStartup, [switch]$NoLaunch, [switch]$Uninstall, [switch]$Update)

function Install-WtSidebar([string]$Ref, [bool]$NoStartup, [bool]$NoLaunch, [bool]$Uninstall, [bool]$Update, [string]$ScriptDir) {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo       = 'moabe-br-2019/wt-sidebar'
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\WtSidebar'
    $exe        = Join-Path $installDir 'WtSidebar.exe'
    $dataDir    = Join-Path $env:LOCALAPPDATA 'WtSidebar'
    $startLnk   = Join-Path ([Environment]::GetFolderPath('Programs')) 'WT Sidebar.lnk'
    $bootLnk    = Join-Path ([Environment]::GetFolderPath('Startup')) 'WtSidebar.lnk'

    function Stop-Sidebar {
        $procs = @(Get-Process WtSidebar -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) { return }
        # No -Update o proprio app fecha logo depois de chamar o script; so forca se ele demorar.
        $procs | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        Get-Process WtSidebar -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
    }

    function New-Shortcut([string]$path, [string]$target) {
        $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
        $lnk.TargetPath = $target
        $lnk.WorkingDirectory = Split-Path $target
        $lnk.Description = 'Abas verticais para o Windows Terminal'
        $lnk.Save()
    }

    # API do GitHub. Sem login primeiro; enquanto o repositorio for privado, usa o token do gh.
    function Invoke-GitHub([string]$path, [string]$outFile) {
        $url = "https://api.github.com/repos/$repo/$path"
        $headers = @{ 'User-Agent' = 'WtSidebar-installer'; 'Accept' = 'application/vnd.github+json' }
        foreach ($attempt in 1, 2) {
            try {
                if ($outFile) { Invoke-WebRequest $url -Headers $headers -OutFile $outFile -UseBasicParsing; return }
                return Invoke-RestMethod $url -Headers $headers
            } catch {
                $status = 0
                if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
                $gh = Get-Command gh -ErrorAction SilentlyContinue
                if ($attempt -eq 2 -or $status -notin 401, 404 -or -not $gh) { throw }
                $token = & gh auth token 2>$null
                if (-not $token) { throw }
                $headers['Authorization'] = "Bearer $token"
            }
        }
    }

    if ($Uninstall) {
        Stop-Sidebar
        foreach ($p in $startLnk, $bootLnk, $installDir) {
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
        Write-Host 'WT Sidebar removido.'
        return
    }

    if ($Update) {
        New-Item -ItemType Directory -Force $dataDir | Out-Null
        Start-Transcript (Join-Path $dataDir 'update.log') | Out-Null
    }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('WtSidebar-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force $work | Out-Null

        if (-not $Ref -and $ScriptDir -and (Test-Path (Join-Path $ScriptDir 'WtSidebar.cs'))) {
            # Clone local: versao pelo git describe (ex.: v1.2.0-3-gabc1234).
            $src = $ScriptDir
            $version = git -C $src describe --tags --always 2>$null
            if (-not $version) { $version = 'dev' }
            Write-Host "Instalando do clone $src ($version)"
        } else {
            if (-not $Ref) {
                try { $Ref = (Invoke-GitHub 'releases/latest').tag_name } catch { }
                if (-not $Ref) { throw "Nenhuma release encontrada em github.com/$repo (sem release publicada ou sem acesso)." }
            }
            Write-Host "Baixando $repo@$Ref"
            $zip = Join-Path $work 'src.zip'
            Invoke-GitHub "zipball/$Ref" $zip
            Expand-Archive $zip (Join-Path $work 'src')
            $src = (Get-ChildItem (Join-Path $work 'src') -Directory | Select-Object -First 1).FullName
            $version = $Ref
        }

        # Compila antes de parar o app: se falhar, a versao instalada continua intacta.
        $built = Join-Path $work 'WtSidebar.exe'
        & (Join-Path $src 'build.ps1') -Out $built

        Stop-Sidebar
        New-Item -ItemType Directory -Force $installDir | Out-Null
        Copy-Item $built $exe -Force
        # O app usa esta copia do instalador para se atualizar, e o version.txt para comparar com a release.
        Copy-Item (Join-Path $src 'install.ps1') (Join-Path $installDir 'install.ps1') -Force
        Set-Content (Join-Path $installDir 'version.txt') $version -Encoding ASCII

        New-Shortcut $startLnk $exe
        if ($NoStartup) { if (Test-Path $bootLnk) { Remove-Item $bootLnk -Force } }
        else { New-Shortcut $bootLnk $exe }

        Write-Host "WT Sidebar $version instalado em $installDir"
        if (-not $NoLaunch) { Start-Process $exe }
    }
    catch {
        if (-not $Update) { throw }
        Write-Host "ERRO: $_"
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show("A atualiza$([char]0xE7)$([char]0xE3)o falhou:`n`n$_`n`nLog: $dataDir\update.log",
            'WT Sidebar', 'OK', 'Error') | Out-Null
        # Garante que o app volta mesmo com erro.
        if ((Test-Path $exe) -and -not (Get-Process WtSidebar -ErrorAction SilentlyContinue)) { Start-Process $exe }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        if ($Update) { Stop-Transcript | Out-Null }
    }
}

Install-WtSidebar $Ref $NoStartup $NoLaunch $Uninstall $Update $PSScriptRoot

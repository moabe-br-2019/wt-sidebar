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
#   -NoKeys       nao adiciona ao settings.json do terminal os atalhos que o menu de aba usa
#   -NoLaunch     nao abre o app no fim
#   -Uninstall    remove app e atalhos (mantem as pastas recentes)
#   -Update       usado pelo menu "Atualizar" do app: roda escondido, grava log e avisa se falhar
#
# Rodado de dentro de um clone (.\install.ps1, sem -Ref), instala o codigo local.
param([string]$Ref, [switch]$NoStartup, [switch]$NoKeys, [switch]$NoLaunch, [switch]$Uninstall, [switch]$Update)

function Install-WtSidebar([string]$Ref, [bool]$NoStartup, [bool]$NoKeys, [bool]$NoLaunch, [bool]$Uninstall, [bool]$Update, [string]$ScriptDir) {
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

    # settings.json aceita comentarios e virgulas sobrando; o ConvertFrom-Json do PowerShell 5 nao.
    function Remove-Jsonc([string]$s) {
        # Duas passadas: tira os comentarios e depois as virgulas antes de } ou ] (que podem ter ficado
        # separadas do fecha so por um comentario).
        foreach ($pass in 'comments', 'commas') {
            $sb = New-Object Text.StringBuilder $s.Length
            $inString = $false
            for ($i = 0; $i -lt $s.Length; $i++) {
                $c = $s[$i]
                if ($inString) {
                    [void]$sb.Append($c)
                    if ($c -eq '\' -and $i + 1 -lt $s.Length) { [void]$sb.Append($s[++$i]) }
                    elseif ($c -eq '"') { $inString = $false }
                }
                elseif ($c -eq '"') { $inString = $true; [void]$sb.Append($c) }
                elseif ($pass -eq 'comments' -and $c -eq '/' -and $i + 1 -lt $s.Length -and $s[$i + 1] -eq '/') {
                    while ($i -lt $s.Length -and $s[$i] -ne "`n") { $i++ }
                    [void]$sb.Append("`n")
                }
                elseif ($pass -eq 'comments' -and $c -eq '/' -and $i + 1 -lt $s.Length -and $s[$i + 1] -eq '*') {
                    $i += 2
                    while ($i + 1 -lt $s.Length -and -not ($s[$i] -eq '*' -and $s[$i + 1] -eq '/')) { $i++ }
                    $i++
                }
                elseif ($pass -eq 'commas' -and $c -eq ',') {
                    $j = $i + 1
                    while ($j -lt $s.Length -and [char]::IsWhiteSpace($s[$j])) { $j++ }
                    if ($j -lt $s.Length -and ($s[$j] -eq '}' -or $s[$j] -eq ']')) { continue }
                    [void]$sb.Append($c)
                }
                else { [void]$sb.Append($c) }
            }
            $s = $sb.ToString()
        }
        $s
    }

    # Comando de acao comparavel: "find" ou "action=moveTab;direction=forward", sem depender da ordem.
    function Get-CommandId($command) {
        if ($command -is [string]) { return $command }
        $pairs = if ($command -is [hashtable]) { $command.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } }
                 else { $command.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" } }
        $id = ($pairs | Sort-Object) -join ';'
        if ($id -match '^action=([^;]+)$') { $matches[1] } else { $id }
    }

    # "Shift+Ctrl+C" e "ctrl+shift+c" sao o mesmo atalho.
    function Get-KeyId([string]$keys) {
        (($keys.ToLower() -split '\+' | ForEach-Object { $_.Trim() }) | Sort-Object) -join '+'
    }

    # O menu de aba do sidebar manda estes atalhos, que o Windows Terminal nao tem por padrao.
    # Adiciona so os que faltam, com ids WtSidebar.*, e pula tecla ja usada por outra acao.
    # Com $remove, tira as acoes WtSidebar.* (desinstalacao).
    function Set-TerminalKeys([bool]$remove) {
        $local = $env:LOCALAPPDATA
        $path = @(
            "$local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
            "$local\Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json",
            "$local\Microsoft\Windows Terminal\settings.json"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $path) { Write-Host 'Windows Terminal: settings.json nao encontrado, atalhos nao configurados.'; return }

        $wanted = @(
            @{ id = 'WtSidebar.openTabColorPicker'; keys = 'ctrl+alt+shift+c'; command = 'openTabColorPicker' },
            @{ id = 'WtSidebar.openTabRenamer';     keys = 'ctrl+alt+shift+r'; command = 'openTabRenamer' },
            @{ id = 'WtSidebar.exportBuffer';       keys = 'ctrl+alt+shift+e'; command = @{ action = 'exportBuffer' } },
            @{ id = 'WtSidebar.moveTabNewWindow';   keys = 'ctrl+alt+shift+n'; command = @{ action = 'moveTab'; window = 'new' } },
            @{ id = 'WtSidebar.moveTabBackward';    keys = 'ctrl+alt+shift+left'; command = @{ action = 'moveTab'; direction = 'backward' } },
            @{ id = 'WtSidebar.moveTabForward';     keys = 'ctrl+alt+shift+right'; command = @{ action = 'moveTab'; direction = 'forward' } },
            @{ id = 'WtSidebar.closeOtherTabs';     keys = 'ctrl+alt+shift+o'; command = @{ action = 'closeOtherTabs' } },
            @{ id = 'WtSidebar.closeTabsAfter';     keys = 'ctrl+alt+shift+w'; command = @{ action = 'closeTabsAfter' } }
        )

        try { $json = Remove-Jsonc ([IO.File]::ReadAllText($path)) | ConvertFrom-Json }
        catch { Write-Host "Windows Terminal: nao consegui ler $path, atalhos nao configurados."; return }
        $actions = @(if ($json.actions) { $json.actions })
        $bindings = @(if ($json.keybindings) { $json.keybindings })

        if ($remove) {
            $newActions = @($actions | Where-Object { "$($_.id)" -notlike 'WtSidebar.*' })
            $newBindings = @($bindings | Where-Object { "$($_.id)" -notlike 'WtSidebar.*' })
            if ($newActions.Count -eq $actions.Count -and $newBindings.Count -eq $bindings.Count) { return }
            $actions = $newActions; $bindings = $newBindings
            $added = @()
        } else {
            # Teclas ja usadas: em "keybindings" (formato novo) e em "keys" dentro de "actions" (formato antigo).
            $commandById = @{}
            foreach ($a in $actions) { if ($a.id) { $commandById[$a.id] = Get-CommandId $a.command } }
            $used = @{}
            foreach ($b in $bindings) {
                foreach ($k in @($b.keys)) { if ($k) { $used[(Get-KeyId $k)] = if ($commandById.ContainsKey("$($b.id)")) { $commandById[$b.id] } else { "$($b.id)" } } }
            }
            foreach ($a in $actions) {
                foreach ($k in @($a.keys)) { if ($k) { $used[(Get-KeyId $k)] = Get-CommandId $a.command } }
            }
            $added = @()
            foreach ($w in $wanted) {
                $keyId = Get-KeyId $w.keys
                if ($used.ContainsKey($keyId)) {
                    # Mesma acao ja configurada pelo usuario: nada a fazer. Outra acao: nao sobrescreve.
                    if ($used[$keyId] -ne (Get-CommandId $w.command)) {
                        Write-Host "Windows Terminal: $($w.keys) ja esta em uso ($($used[$keyId])); pulei $(Get-CommandId $w.command)."
                    }
                    continue
                }
                if (-not ($actions | Where-Object { $_.id -eq $w.id })) {
                    $actions += [pscustomobject]@{ command = $w.command; id = $w.id }
                }
                $bindings += [pscustomobject]@{ id = $w.id; keys = $w.keys }
                $added += $w.keys
            }
            if ($added.Count -eq 0) { return }
        }

        foreach ($name in 'actions', 'keybindings') {
            $value = if ($name -eq 'actions') { $actions } else { $bindings }
            if ($json.PSObject.Properties[$name]) { $json.$name = $value }
            else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
        }
        Copy-Item $path "$path.wtsidebar.bak" -Force
        [IO.File]::WriteAllText($path, ($json | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
        if ($remove) { Write-Host 'Windows Terminal: atalhos do WT Sidebar removidos.' }
        else { Write-Host "Windows Terminal: atalhos adicionados ($($added -join ', ')). Backup: $path.wtsidebar.bak" }
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
        Set-TerminalKeys $true
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

        if (-not $NoKeys) { Set-TerminalKeys $false }
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

Install-WtSidebar $Ref $NoStartup $NoKeys $NoLaunch $Uninstall $Update $PSScriptRoot

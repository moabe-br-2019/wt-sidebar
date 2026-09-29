# Instalador do WT Sidebar. Baixa o codigo da ultima release do GitHub, compila com o csc que ja vem
# no Windows, copia para %LOCALAPPDATA%\Programs\WtSidebar, cria atalho no menu Iniciar, liga o inicio
# com o Windows (HKCU\...\Run), configura os atalhos do terminal e abre o app. Rodar de novo reinstala por cima.
#
#   irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1 | iex
#
# Com opcoes:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1))) -NoStartup
#
#   -Ref v1.2.0   instala essa tag ou branch em vez da ultima release
#   -NoStartup    nao inicia com o Windows
#   -NoKeys       nao mexe no settings.json do terminal (atalhos do menu de aba e abrir como aba)
#   -NoLaunch     nao abre o app no fim
#   -Uninstall    remove app e atalhos (mantem as pastas recentes)
#   -Lang en      mensagens em ingles (ou pt); por padrao segue o idioma do Windows
#   -Update       usado pelo menu "Atualizar" do app: roda escondido, grava log e avisa se falhar
#   -KeysOnly     so configura o terminal (botao nas configuracoes do app)
#   -Gui          com -Uninstall, avisa numa janela no fim (desinstalar por Aplicativos instalados)
#
# Rodado de dentro de um clone (.\install.ps1, sem -Ref), instala o codigo local.
param([string]$Ref, [string]$Lang, [switch]$NoStartup, [switch]$NoKeys, [switch]$NoLaunch,
      [switch]$Uninstall, [switch]$Update, [switch]$KeysOnly, [switch]$Gui)

function Install-WtSidebar {
    param([string]$Ref, [string]$Lang, [switch]$NoStartup, [switch]$NoKeys, [switch]$NoLaunch,
          [switch]$Uninstall, [switch]$Update, [switch]$KeysOnly, [switch]$Gui, [string]$ScriptDir)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo       = 'moabe-br-2019/wt-sidebar'
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\WtSidebar'
    $exe        = Join-Path $installDir 'WtSidebar.exe'
    $dataDir    = Join-Path $env:LOCALAPPDATA 'WtSidebar'
    $startLnk   = Join-Path ([Environment]::GetFolderPath('Programs')) 'WT Sidebar.lnk'
    $legacyLnk  = Join-Path ([Environment]::GetFolderPath('Startup')) 'WtSidebar.lnk' # versoes antigas
    $runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $appsKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WtSidebar' # Aplicativos instalados

    if ($Lang -ne 'pt' -and $Lang -ne 'en') { $Lang = if ((Get-UICulture).TwoLetterISOLanguageName -eq 'pt') { 'pt' } else { 'en' } }
    # Mensagem no idioma escolhido. O arquivo fica so em ASCII (o irm | iex quebra com BOM), entao os
    # acentos do portugues vem como \u00e3 e o texto passa por Unescape antes do -f.
    function M([string]$pt, [string]$en) {
        $text = if ($Lang -eq 'pt') { [regex]::Unescape($pt) } else { $en }
        if ($args.Count) { $text -f $args } else { $text }
    }

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

    # Configuracao do Windows Terminal que o sidebar precisa:
    # - atalhos que o menu de aba manda e que o terminal nao tem por padrao. Adiciona so os que faltam,
    #   com ids WtSidebar.*, e pula tecla ja usada por outra acao;
    # - windowingBehavior useAnyExisting: abrir o terminal (menu Iniciar, "Abrir no Terminal" do
    #   Explorer, wt.exe) cria uma aba na janela existente, porque o sidebar acompanha uma janela so.
    #   O valor anterior fica em terminal-windowing.txt para a desinstalacao restaurar.
    # Com $remove (desinstalacao), tira as acoes WtSidebar.* e restaura o windowingBehavior.
    function Set-TerminalSettings([bool]$remove) {
        $local = $env:LOCALAPPDATA
        $path = @(
            "$local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
            "$local\Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json",
            "$local\Microsoft\Windows Terminal\settings.json"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $path) { Write-Host (M 'Windows Terminal: settings.json n\u00e3o encontrado, terminal n\u00e3o configurado.' 'Windows Terminal: settings.json not found, terminal not configured.'); return }

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
        $windowingFile = Join-Path $dataDir 'terminal-windowing.txt'

        try { $json = Remove-Jsonc ([IO.File]::ReadAllText($path)) | ConvertFrom-Json }
        catch { Write-Host (M 'Windows Terminal: n\u00e3o consegui ler {0}, terminal n\u00e3o configurado.' 'Windows Terminal: could not read {0}, terminal not configured.' $path); return }
        $actions = @(if ($json.actions) { $json.actions })
        $bindings = @(if ($json.keybindings) { $json.keybindings })
        $windowing = if ($json.PSObject.Properties['windowingBehavior']) { "$($json.windowingBehavior)" } else { '' }
        $messages = @()

        if ($remove) {
            $newActions = @($actions | Where-Object { "$($_.id)" -notlike 'WtSidebar.*' })
            $newBindings = @($bindings | Where-Object { "$($_.id)" -notlike 'WtSidebar.*' })
            if ($newActions.Count -ne $actions.Count -or $newBindings.Count -ne $bindings.Count) {
                $actions = $newActions; $bindings = $newBindings
                $messages += M 'Windows Terminal: atalhos do WT Sidebar removidos.' 'Windows Terminal: WT Sidebar shortcuts removed.'
            }
            # So restaura se o valor ainda e o que o instalador colocou.
            if ((Test-Path $windowingFile) -and $windowing -eq 'useAnyExisting') {
                $before = ([IO.File]::ReadAllText($windowingFile)).Trim()
                if ($before) { $json.windowingBehavior = $before } else { $json.PSObject.Properties.Remove('windowingBehavior') }
                $messages += M 'Windows Terminal: comportamento de janelas restaurado.' 'Windows Terminal: window behavior restored.'
            }
            if (Test-Path $windowingFile) { Remove-Item $windowingFile -Force }
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
                        $messages += M 'Windows Terminal: {0} j\u00e1 est\u00e1 em uso ({1}); {2} n\u00e3o configurado.' 'Windows Terminal: {0} is already in use ({1}); {2} not configured.' $w.keys $used[$keyId] (Get-CommandId $w.command)
                    }
                    continue
                }
                if (-not ($actions | Where-Object { $_.id -eq $w.id })) {
                    $actions += [pscustomobject]@{ command = $w.command; id = $w.id }
                }
                $bindings += [pscustomobject]@{ id = $w.id; keys = $w.keys }
                $added += $w.keys
            }
            if ($added.Count) {
                $messages += M 'Windows Terminal: atalhos adicionados ({0}).' 'Windows Terminal: shortcuts added ({0}).' ($added -join ', ')
            }

            # useExisting (janela mais recente da area de trabalho atual) tambem serve; so troca o padrao useNew.
            if ($windowing -ne 'useAnyExisting' -and $windowing -ne 'useExisting') {
                New-Item -ItemType Directory -Force $dataDir | Out-Null
                [IO.File]::WriteAllText($windowingFile, $windowing)
                if ($json.PSObject.Properties['windowingBehavior']) { $json.windowingBehavior = 'useAnyExisting' }
                else { $json | Add-Member -NotePropertyName windowingBehavior -NotePropertyValue 'useAnyExisting' }
                $messages += M 'Windows Terminal: abrir o terminal agora cria uma aba na janela existente, em vez de uma janela nova.' 'Windows Terminal: opening the terminal now adds a tab to the existing window instead of a new window.'
            }
            if (-not $added.Count -and $messages.Count -eq 0) {
                Write-Host (M 'Windows Terminal: j\u00e1 est\u00e1 configurado.' 'Windows Terminal: already configured.')
                return
            }
        }
        if ($messages.Count -eq 0) { return }
        $changed = $remove -or $added.Count -or ($json.windowingBehavior -ne $windowing)
        if ($changed) {
            # Sem "$x = if ...": o pipeline desembrulharia um array de 1 item em objeto solto.
            $lists = @{ actions = [object[]]$actions; keybindings = [object[]]$bindings }
            foreach ($name in 'actions', 'keybindings') {
                $json.PSObject.Properties.Remove($name)
                if ($lists[$name].Count) { $json | Add-Member -NotePropertyName $name -NotePropertyValue $lists[$name] }
            }
            Copy-Item $path "$path.wtsidebar.bak" -Force
            [IO.File]::WriteAllText($path, ($json | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
            $messages += M 'Backup: {0}' 'Backup: {0}' "$path.wtsidebar.bak"
        }
        $messages | ForEach-Object { Write-Host $_ }
    }

    function New-Shortcut([string]$path, [string]$target) {
        $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
        $lnk.TargetPath = $target
        $lnk.WorkingDirectory = Split-Path $target
        $lnk.Description = M 'Abas verticais para o Windows Terminal' 'Vertical tabs for Windows Terminal'
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

    if ($KeysOnly) {
        # Chamado pelo app, que mostra esta saida numa caixa de mensagem.
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Set-TerminalSettings $false
        return
    }

    if ($Uninstall) {
        Stop-Sidebar
        Set-TerminalSettings $true
        Remove-ItemProperty $runKey -Name WtSidebar -ErrorAction SilentlyContinue
        foreach ($p in $startLnk, $legacyLnk, $installDir, $appsKey) {
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
        Write-Host (M 'WT Sidebar removido.' 'WT Sidebar removed.')
        if ($Gui) {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show((M 'WT Sidebar removido.' 'WT Sidebar removed.'), 'WT Sidebar', 'OK', 'Information') | Out-Null
        }
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
            Write-Host (M 'Instalando do clone {0} ({1})' 'Installing from clone {0} ({1})' $src $version)
        } else {
            if (-not $Ref) {
                try { $Ref = (Invoke-GitHub 'releases/latest').tag_name } catch { }
                if (-not $Ref) { throw (M 'Nenhuma release encontrada em github.com/{0} (sem release publicada ou sem acesso).' 'No release found at github.com/{0} (none published or no access).' $repo) }
            }
            Write-Host (M 'Baixando {0}@{1}' 'Downloading {0}@{1}' $repo $Ref)
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
        # Entrada em Aplicativos instalados, para desinstalar pelo Windows.
        New-Item $appsKey -Force | Out-Null
        $uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$installDir\install.ps1`" -Uninstall -Gui -Lang $Lang"
        foreach ($v in @(@('DisplayName', 'WT Sidebar'), @('DisplayVersion', "$version"), @('Publisher', 'Moabe'),
                         @('DisplayIcon', $exe), @('InstallLocation', $installDir), @('UninstallString', $uninstallCmd),
                         @('URLInfoAbout', "https://github.com/$repo"))) {
            Set-ItemProperty $appsKey -Name $v[0] -Value $v[1]
        }
        Set-ItemProperty $appsKey -Name NoModify -Value 1 -Type DWord
        Set-ItemProperty $appsKey -Name NoRepair -Value 1 -Type DWord
        # Iniciar com o Windows: no update fica como o usuario deixou nas configuracoes do app.
        if (Test-Path $legacyLnk) {
            Remove-Item $legacyLnk -Force
            if ($Update) { Set-ItemProperty $runKey -Name WtSidebar -Value "`"$exe`"" }
        }
        if (-not $Update) {
            if ($NoStartup) { Remove-ItemProperty $runKey -Name WtSidebar -ErrorAction SilentlyContinue }
            else { Set-ItemProperty $runKey -Name WtSidebar -Value "`"$exe`"" }
        }

        if (-not $NoKeys -and -not $Update) { Set-TerminalSettings $false }
        Write-Host (M 'WT Sidebar {0} instalado em {1}' 'WT Sidebar {0} installed to {1}' $version $installDir)
        if (-not $NoLaunch) { Start-Process $exe }
    }
    catch {
        if (-not $Update) { throw }
        Write-Host "ERROR: $_"
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show((M 'A atualiza\u00e7\u00e3o falhou:\n\n{0}\n\nLog: {1}' "The update failed:`n`n{0}`n`nLog: {1}" $_ "$dataDir\update.log"),
            'WT Sidebar', 'OK', 'Error') | Out-Null
        # Garante que o app volta mesmo com erro.
        if ((Test-Path $exe) -and -not (Get-Process WtSidebar -ErrorAction SilentlyContinue)) { Start-Process $exe }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        if ($Update) { Stop-Transcript | Out-Null }
    }
}

Install-WtSidebar @PSBoundParameters -ScriptDir $PSScriptRoot

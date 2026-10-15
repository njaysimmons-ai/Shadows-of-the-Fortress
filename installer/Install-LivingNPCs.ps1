# Living NPCs installer for Erenshor.
# Finds the game, installs BepInEx if needed, installs the mod, and optionally sets up AI chat (Ollama).
# Run it by double-clicking "Install Living NPCs.bat" in the same folder.

param(
    [string]$GameDir = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ErenshorAppId = "2382520"
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
$OllamaUrl = "https://ollama.com/download/OllamaSetup.exe"
$PluginGuid = "njaysimmons.erenshor.livingnpcs"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$TextFiles = @("dialogue.txt", "sims.txt", "life.txt", "ai.txt")

function Say($text, $color = "Gray") { Write-Host $text -ForegroundColor $color }
function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ask($question) {
    while ($true) {
        $answer = Read-Host "$question [Y/N]"
        if ($answer -match '^(y|yes)$') { return $true }
        if ($answer -match '^(n|no)$') { return $false }
    }
}
function Fail($text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

# ---------------------------------------------------------------- finding the game

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath) { $roots += ($item.SteamPath -replace '/', '\') }
            if ($item.InstallPath) { $roots += $item.InstallPath }
        } catch { }
    }
    $roots += "C:\Program Files (x86)\Steam"
    $libraries = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        $libraries += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($match in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($match.Groups[1].Value -replace '\\\\', '\')
            }
        }
    }
    return $libraries | Select-Object -Unique
}

function Find-Erenshor {
    foreach ($library in Get-SteamLibraries) {
        $manifest = Join-Path $library "steamapps\appmanifest_$ErenshorAppId.acf"
        $folder = "Erenshor"
        if (Test-Path $manifest) {
            $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $folder = $m.Groups[1].Value }
        }
        $candidate = Join-Path $library "steamapps\common\$folder"
        if (Test-Path (Join-Path $candidate "Erenshor.exe")) { return $candidate }
    }
    return $null
}

function Pick-Folder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select your Erenshor folder (the one with Erenshor.exe in it)"
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    return $null
}

# ---------------------------------------------------------------- AI helpers

function Find-Ollama {
    $cmd = Get-Command ollama -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
    if (Test-Path $default) { return $default }
    return $null
}

function Get-VideoMemoryGB {
    # nvidia-smi reports real VRAM; Windows' own figure caps at 4 GB, so it's only a fallback.
    try {
        $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
        if ($smi) {
            $mb = & $smi.Source --query-gpu=memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1
            if ($mb) { return [math]::Round([double]$mb / 1024) }
        }
    } catch { }
    try {
        $bytes = (Get-CimInstance Win32_VideoController | Measure-Object -Property AdapterRAM -Maximum).Maximum
        if ($bytes) { return [math]::Round($bytes / 1GB) }
    } catch { }
    return 0
}

function Choose-Model($vramGB) {
    if ($vramGB -ge 16) { return @{ Name = "qwen2.5:14b"; Size = "9 GB"; Why = "your graphics card has $vramGB GB of video memory, enough for a large, smart model" } }
    if ($vramGB -ge 10) { return @{ Name = "llama3.1:8b"; Size = "5 GB"; Why = "your graphics card has $vramGB GB of video memory" } }
    return @{ Name = "llama3.2:3b"; Size = "2 GB"; Why = "it runs well on most PCs" }
}

function Wait-ForOllama($exe) {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:11434/api/tags" -UseBasicParsing -TimeoutSec 2 | Out-Null
            return $true
        } catch { }
        if ($i -eq 2 -and $exe) { Start-Process -FilePath $exe -ArgumentList "serve" -WindowStyle Hidden }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Set-ConfigValue($configFile, $section, $key, $value) {
    $lines = @()
    if (Test-Path $configFile) { $lines = @(Get-Content $configFile) }
    $out = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    $done = $false
    foreach ($line in $lines) {
        if ($line -match '^\[(.+)\]\s*$') {
            if ($inSection -and -not $done) { $out.Add("$key = $value"); $out.Add(""); $done = $true }
            $inSection = ($Matches[1] -eq $section)
        } elseif ($inSection -and $line -match ("^" + [regex]::Escape($key) + "\s*=")) {
            $out.Add("$key = $value")
            $done = $true
            continue
        }
        $out.Add($line)
    }
    if (-not $done) {
        if (-not $inSection) { $out.Add(""); $out.Add("[$section]") }
        $out.Add("$key = $value")
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $configFile) | Out-Null
    Set-Content -Path $configFile -Value $out -Encoding UTF8
}

# ================================================================ start

Clear-Host
Say "Living NPCs for Erenshor" "Yellow"
Say "Sims with personalities, a living world, emotes, and AI chat." "DarkGray"

Step "Finding Erenshor"
if (-not $GameDir) { $GameDir = Find-Erenshor }
if (-not $GameDir) {
    Say "Couldn't find Erenshor automatically. A folder picker will open; choose the folder that contains Erenshor.exe."
    $GameDir = Pick-Folder
}
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir "Erenshor.exe"))) {
    Fail "That folder doesn't contain Erenshor.exe. Run the installer again and pick the Erenshor folder."
}
Say "Found: $GameDir" "Green"

$PluginDir = Join-Path $GameDir "BepInEx\plugins\LivingNPCs"
$ConfigDir = Join-Path $GameDir "BepInEx\config"
$ModDataDir = Join-Path $ConfigDir "LivingNPCs"
$ConfigFile = Join-Path $ConfigDir "$PluginGuid.cfg"
$BepInExMarker = Join-Path $GameDir "BepInEx\installed-by-livingnpcs.txt"

if (Get-Process -Name "Erenshor" -ErrorAction SilentlyContinue) {
    Say "Erenshor is running. Please close it first." "Yellow"
    while (Get-Process -Name "Erenshor" -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
}

# ================================================================ uninstall

if ($Uninstall) {
    Step "Uninstalling"
    if (Test-Path $PluginDir) { Remove-Item $PluginDir -Recurse -Force; Say "Removed the mod." "Green" }
    else { Say "The mod wasn't installed." }
    if ((Test-Path $ModDataDir) -and (Ask "Also delete its settings, dialogue edits and what sims remember about you?")) {
        Remove-Item $ModDataDir -Recurse -Force
        if (Test-Path $ConfigFile) { Remove-Item $ConfigFile -Force }
        Say "Removed settings and memories." "Green"
    }
    if ((Test-Path $BepInExMarker) -and (Ask "This installer also installed BepInEx (the mod loader). Remove it too? Choose N if you use other mods.")) {
        foreach ($item in @("BepInEx", "doorstop_config.ini", "winhttp.dll", ".doorstop_version", "changelog.txt")) {
            $path = Join-Path $GameDir $item
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
        Say "Removed BepInEx." "Green"
    }
    Say ""
    Say "Done. Ollama, if installed, stays on your PC; remove it from Windows Settings > Apps if you like." "Yellow"
    Read-Host "Press Enter to close"
    exit 0
}

# ================================================================ BepInEx

Step "Mod loader (BepInEx)"
if (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.dll")) {
    Say "Already installed." "Green"
} else {
    Say "Downloading BepInEx 5..."
    $zip = Join-Path $env:TEMP "BepInEx_win_x64.zip"
    try {
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $GameDir -Force
        Remove-Item $zip -Force
    } catch {
        Fail "Couldn't download BepInEx: $($_.Exception.Message)"
    }
    Set-Content -Path $BepInExMarker -Value "BepInEx was installed by the Living NPCs installer."
    Say "Installed." "Green"
}

# ================================================================ the mod

Step "Living NPCs"
$dll = Join-Path $Here "LivingNPCs.dll"
if (-not (Test-Path $dll)) { Fail "LivingNPCs.dll is missing. Keep it in the same folder as this installer." }
$upgrading = Test-Path (Join-Path $PluginDir "LivingNPCs.dll")
New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
Copy-Item $dll $PluginDir -Force
Say ($(if ($upgrading) { "Updated." } else { "Installed." })) "Green"

# The mod writes its text files only when they're missing, so an update would keep old ones.
$existing = @($TextFiles | Where-Object { Test-Path (Join-Path $ModDataDir $_) })
if ($upgrading -and $existing.Count -gt 0) {
    Say "Your dialogue files are from an earlier version and won't include the newest lines."
    if (Ask "Replace them with the new versions? (Your current files are kept as .bak copies.)") {
        foreach ($file in $existing) {
            $path = Join-Path $ModDataDir $file
            Move-Item $path "$path.bak" -Force
        }
        Say "Done. The new files will be written the next time the game starts." "Green"
    }
}

# ================================================================ AI chat

Step "AI chat (optional)"
Say "With AI chat you can talk to sims and NPCs in your own words. It runs on your own PC through"
Say "a free program called Ollama: private, and no internet needed once it's set up."
$vram = Get-VideoMemoryGB
$model = Choose-Model $vram
$ollama = Find-Ollama

if (Ask "Set up AI chat?") {
    if (-not $ollama) {
        Say "Installing Ollama..."
        $winget = Get-Command winget -ErrorAction SilentlyContinue
        $installed = $false
        if ($winget) {
            try {
                & $winget.Source install -e --id Ollama.Ollama --accept-source-agreements --accept-package-agreements
                $installed = $true
            } catch { }
        }
        if (-not $installed -or -not (Find-Ollama)) {
            $setup = Join-Path $env:TEMP "OllamaSetup.exe"
            try {
                Invoke-WebRequest -Uri $OllamaUrl -OutFile $setup -UseBasicParsing
                Say "Follow the Ollama installer window, then come back here."
                Start-Process -FilePath $setup -Wait
                Remove-Item $setup -Force -ErrorAction SilentlyContinue
            } catch {
                Say "Couldn't download Ollama: $($_.Exception.Message)" "Red"
            }
        }
        $ollama = Find-Ollama
    } else {
        Say "Ollama is already installed." "Green"
    }

    if ($ollama) {
        Say ""
        Say "Recommended model: $($model.Name) ($($model.Size)), because $($model.Why)." "White"
        $choice = Read-Host "Press Enter to use it, or type another model name (e.g. llama3.1:8b, mistral-nemo, qwen2.5:7b)"
        if ($choice.Trim()) { $model.Name = $choice.Trim() }

        Set-ConfigValue $ConfigFile "AI Chat" "Enabled" "true"
        Set-ConfigValue $ConfigFile "AI Chat" "Model" $model.Name

        if (Wait-ForOllama $ollama) {
            Say "Downloading $($model.Name) now so it's ready when you play. This can take a while..."
            & $ollama pull $model.Name
            if ($LASTEXITCODE -eq 0) { Say "AI model ready." "Green" }
            else { Say "The download didn't finish; the mod will try again when the game starts." "Yellow" }
        } else {
            Say "Ollama didn't start yet; the mod will download the model the first time you play." "Yellow"
        }
    } else {
        Say "AI chat isn't set up. You can install Ollama later from ollama.com and type /ai on in game." "Yellow"
        Set-ConfigValue $ConfigFile "AI Chat" "Enabled" "false"
    }
} else {
    Set-ConfigValue $ConfigFile "AI Chat" "Enabled" "false"
    Say "Skipped. You can turn it on later by installing Ollama and typing /ai on in game."
}

# ================================================================ done

Step "All set!"
Say "In game:" "White"
Say "  /emotes      list emotes (/wave, /bow, /cheer, /dance, ...)"
Say "  /ai          AI chat status; target a sim or NPC and just type to talk"
Say "  Ctrl+F8      reload the dialogue files after editing them"
Say "Settings and dialogue files: $ConfigDir"
Say ""
if (Ask "Launch Erenshor now?") { Start-Process "steam://rungameid/$ErenshorAppId" }

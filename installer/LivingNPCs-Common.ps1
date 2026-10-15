# Shared helpers for the Living NPCs installer and the AI checker. Dot-sourced; does nothing on its own.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ErenshorAppId = "2382520"
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
$OllamaUrl = "https://ollama.com/download/OllamaSetup.exe"
$PluginGuid = "njaysimmons.erenshor.livingnpcs"
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
    if ($env:LOCALAPPDATA) {
        $default = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
        if (Test-Path $default) { return $default }
    }
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

function Get-ConfigValue($configFile, $section, $key) {
    if (-not (Test-Path $configFile)) { return $null }
    $inSection = $false
    foreach ($line in Get-Content $configFile) {
        if ($line -match '^\[(.+)\]\s*$') { $inSection = ($Matches[1] -eq $section); continue }
        if ($inSection -and $line -match ("^" + [regex]::Escape($key) + "\s*=\s*(.*)$")) { return $Matches[1].Trim() }
    }
    return $null
}

function Get-ModPaths($gameDir) {
    $config = Join-Path $gameDir "BepInEx\config"
    return @{
        Plugin = Join-Path $gameDir "BepInEx\plugins\LivingNPCs"
        Config = $config
        ModData = Join-Path $config "LivingNPCs"
        ConfigFile = Join-Path $config "$PluginGuid.cfg"
        Log = Join-Path $gameDir "BepInEx\LogOutput.log"
        BepInExMarker = Join-Path $gameDir "BepInEx\installed-by-livingnpcs.txt"
    }
}

function Resolve-GameDir($gameDir) {
    if (-not $gameDir) { $gameDir = Find-Erenshor }
    if (-not $gameDir) {
        Say "Couldn't find Erenshor automatically. A folder picker will open; choose the folder that contains Erenshor.exe."
        $gameDir = Pick-Folder
    }
    if (-not $gameDir -or -not (Test-Path (Join-Path $gameDir "Erenshor.exe"))) {
        Fail "That folder doesn't contain Erenshor.exe. Run this again and pick the Erenshor folder."
    }
    return $gameDir
}

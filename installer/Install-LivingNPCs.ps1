# Living NPCs installer for Erenshor.
# Finds the game, installs BepInEx if needed, installs the mod, and optionally sets up AI chat (Ollama).
# Run it by double-clicking "Install Living NPCs.bat" in the same folder.

param(
    [string]$GameDir = "",
    [switch]$Uninstall
)

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Here "LivingNPCs-Common.ps1")

# ================================================================ start

Clear-Host
Say "Living NPCs for Erenshor" "Yellow"
Say "Sims with personalities, a living world, emotes, and AI chat." "DarkGray"

Step "Finding Erenshor"
$GameDir = Resolve-GameDir $GameDir
Say "Found: $GameDir" "Green"

$paths = Get-ModPaths $GameDir
$PluginDir = $paths.Plugin
$ConfigDir = $paths.Config
$ModDataDir = $paths.ModData
$ConfigFile = $paths.ConfigFile
$BepInExMarker = $paths.BepInExMarker

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

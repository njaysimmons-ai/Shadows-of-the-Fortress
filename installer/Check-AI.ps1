# Living NPCs AI checker: finds out why AI chat won't connect, fixes what it can, and writes a report.
# Run it by double-clicking "Check AI.bat". Close Erenshor first if you want it to download a model.

param([string]$GameDir = "")

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $Here "LivingNPCs-Common.ps1")
$ErrorActionPreference = "Continue"

$Report = New-Object System.Collections.Generic.List[string]
function Note($text, $color = "Gray") { Say $text $color; $Report.Add($text) }
function Good($text) { Note ("[OK]   " + $text) "Green" }
function Bad($text) { Note ("[FAIL] " + $text) "Red" }
function Info($text) { Note ("       " + $text) "Gray" }

$OllamaUrl = "http://127.0.0.1:11434"
function Api($path, $body = $null, $timeout = 10) {
    if ($body) {
        return Invoke-RestMethod -Uri "$OllamaUrl$path" -Method Post -Body ($body | ConvertTo-Json -Depth 6) -ContentType "application/json" -TimeoutSec $timeout
    }
    return Invoke-RestMethod -Uri "$OllamaUrl$path" -TimeoutSec $timeout
}

Clear-Host
Say "Living NPCs: AI chat checker" "Yellow"
Note ("Date: " + (Get-Date) + "   Windows: " + [Environment]::OSVersion.VersionString)

# ---------------------------------------------------------------- 1. the game and the mod's settings
Step "1. Erenshor and the mod"
$GameDir = Resolve-GameDir $GameDir
$paths = Get-ModPaths $GameDir
Good "Erenshor: $GameDir"
if (Test-Path (Join-Path $paths.Plugin "LivingNPCs.dll")) { Good "Living NPCs is installed" } else { Bad "Living NPCs isn't installed. Run 'Install Living NPCs.bat'." }

$enabled = Get-ConfigValue $paths.ConfigFile "AI Chat" "Enabled"
$model = Get-ConfigValue $paths.ConfigFile "AI Chat" "Model"
$url = Get-ConfigValue $paths.ConfigFile "AI Chat" "OllamaUrl"
if (-not $model) { $model = "llama3.2:3b" }
if ($url) { $OllamaUrl = $url.TrimEnd('/') }
Info "Settings: AI enabled = $enabled, model = $model, Ollama address = $OllamaUrl"
if ($enabled -eq "false") {
    Bad "AI chat is switched off in the settings."
    if (Ask "Turn it on?") { Set-ConfigValue $paths.ConfigFile "AI Chat" "Enabled" "true"; Good "Turned on." }
}

# ---------------------------------------------------------------- 2. Ollama installed
Step "2. Ollama installed"
$exe = Find-Ollama
if ($exe) {
    Good "Ollama found: $exe"
    try { Info ("Version: " + (& $exe --version 2>&1 | Select-Object -First 1)) } catch { }
} else {
    Bad "Ollama isn't installed (looked on the PATH and in $env:LOCALAPPDATA\Programs\Ollama)."
    Info "Install it from https://ollama.com/download, or run 'Install Living NPCs.bat' again and say Yes to AI chat."
    if (Ask "Open the Ollama download page now?") { Start-Process "https://ollama.com/download" }
}

# ---------------------------------------------------------------- 3. Ollama running
Step "3. Ollama running"
$running = $false
try { $v = Api "/api/version" $null 5; $running = $true; Good ("Ollama is running (version " + $v.version + ")") }
catch { Bad ("Ollama isn't answering at " + $OllamaUrl + ": " + $_.Exception.Message) }
if (-not $running -and $exe) {
    Info "Starting Ollama..."
    $app = Join-Path (Split-Path $exe) "ollama app.exe"
    if (Test-Path $app) { Start-Process -FilePath $app } else { Start-Process -FilePath $exe -ArgumentList "serve" -WindowStyle Hidden }
    for ($i = 0; $i -lt 30 -and -not $running; $i++) {
        Start-Sleep -Seconds 1
        try { Api "/api/version" $null 3 | Out-Null; $running = $true } catch { }
    }
    if ($running) { Good "Ollama started." } else { Bad "Ollama didn't start within 30 seconds." }
}
$listeners = $null
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listeners = Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue
}
if ($listeners) { Info ("Listening on port 11434: " + (($listeners | ForEach-Object { $_.LocalAddress }) -join ", ")) }
elseif (-not $running) { Info "Nothing is listening on port 11434." }
if ($env:OLLAMA_HOST) { Info "OLLAMA_HOST is set to '$env:OLLAMA_HOST' (the mod expects $OllamaUrl)" }

# ---------------------------------------------------------------- 4. the model
Step "4. The AI model ($model)"
if ($running) {
    $names = @()
    try { $names = @((Api "/api/tags").models | ForEach-Object { $_.name }) } catch { Bad ("Couldn't list models: " + $_.Exception.Message) }
    Info ("Downloaded models: " + $(if ($names.Count) { $names -join ", " } else { "none" }))
    $wanted = $(if ($model.Contains(":")) { $model } else { "$model`:latest" })
    if ($names -contains $wanted -or $names -contains $model) {
        Good "$model is downloaded."
    } else {
        Bad "$model isn't downloaded yet."
        if ($exe -and (Ask "Download it now? You'll see the progress here (it's several GB).")) {
            & $exe pull $model
            if ($LASTEXITCODE -eq 0) { Good "Downloaded $model." } else { Bad "The download didn't finish." }
        }
    }
}

# ---------------------------------------------------------------- 5. a real reply
Step "5. Test reply"
if ($running) {
    Info "Asking $model a question. The first time can take up to a minute while it loads into video memory..."
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $reply = Api "/api/chat" @{
            model = $model; stream = $false; keep_alive = "30m"
            messages = @(@{ role = "user"; content = "Say hello like an adventurer in a fantasy game, in one short sentence." })
        } 180
        Good ("Reply in " + [math]::Round($timer.Elapsed.TotalSeconds, 1) + "s: " + $reply.message.content)
    } catch {
        Bad ("No reply after " + [math]::Round($timer.Elapsed.TotalSeconds, 1) + "s: " + $_.Exception.Message)
        if ($_.ErrorDetails.Message) { Info $_.ErrorDetails.Message }
    }
}
try {
    $gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty Name
    Info ("Graphics card: $gpu, about " + (Get-VideoMemoryGB) + " GB")
} catch { }

# ---------------------------------------------------------------- 6. what the mod saw
Step "6. What the mod logged last time you played"
if (Test-Path $paths.Log) {
    $lines = Get-Content $paths.Log | Where-Object { $_ -match "Living NPCs|LivingNPCs|Ollama|AI chat|AI request|AI model" } | Select-Object -Last 25
    if ($lines) { foreach ($l in $lines) { Info $l } } else { Info "No Living NPCs lines in the log. Did the mod load? Check that BepInEx is installed." }
} else {
    Info "No BepInEx log yet at $($paths.Log). Start the game once with the mod installed."
}

# ---------------------------------------------------------------- report
$desktop = [Environment]::GetFolderPath("Desktop")
$file = Join-Path $desktop "LivingNPCs-AI-report.txt"
$Report | Set-Content -Path $file -Encoding UTF8
Step "Done"
Say "A copy of this report is on your Desktop: LivingNPCs-AI-report.txt" "Yellow"
Say "If AI chat still doesn't work in game, send that file (or a screenshot of this window)."
Read-Host "Press Enter to close"

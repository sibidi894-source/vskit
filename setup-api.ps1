# setup-api.ps1
# Writes api.conf next to qwen-terminal.exe (url / key / model).
#
#   powershell -ExecutionPolicy Bypass -File .\setup-api.ps1
#   .\setup-api.ps1 -Url https://your-provider.com/v1 -Key sk-... -Model qwen3.8-flash
#
# Key is stored as plain text in api.conf. Keep the file out of shared folders.

param(
    [string]$Url = "",
    [string]$Key = "",
    [string]$Model = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$conf = Join-Path $here "api.conf"
$exe  = Join-Path $here "qwen-terminal.exe"

if (-not (Test-Path $exe)) {
    Write-Host "warning: qwen-terminal.exe not found next to this script" -ForegroundColor Yellow
}

# keep existing values so you can change one thing at a time
$existing = @{}
if (Test-Path $conf) {
    foreach ($line in Get-Content $conf) {
        if ($line -match '^\s*#' -or -not $line.Contains('=')) { continue }
        $k, $v = $line -split '=', 2
        $existing[$k.Trim().ToLower()] = $v.Trim()
    }
}

if (-not $Url)   { $Url   = if ($existing['url'])   { $existing['url'] }   else { "" } }
if (-not $Key)   { $Key   = if ($existing['key'])   { $existing['key'] }   else { "" } }
if (-not $Model) { $Model = if ($existing['model']) { $existing['model'] } else { "gpt-4o-mini" } }
$system = if ($existing['system']) { $existing['system'] } else { "" }

if (-not $Url) {
    $Url = Read-Host "api base url (ends with /v1), Enter for https://api.openai.com/v1"
    if (-not $Url) { $Url = "https://api.openai.com/v1" }
}

if (-not $Key) {
    Write-Host "Paste your api key (input is hidden)." -ForegroundColor Cyan
    $secure = Read-Host "api key" -AsSecureString
    $Key = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

$lines = @(
    "# vskit terminal config"
    "url=$Url"
    "key=$Key"
    "model=$Model"
)
if ($system) { $lines += "system=$system" }

Set-Content -Path $conf -Value $lines -Encoding UTF8

Write-Host ""
Write-Host "wrote $conf" -ForegroundColor Green
Write-Host "  url     : $Url"
Write-Host "  model   : $Model"
Write-Host ("  key     : " + $(if ($Key.Length -gt 8) { $Key.Substring(0,4) + "..." + $Key.Substring($Key.Length-4) } elseif ($Key) { "****" } else { "(none)" }))
Write-Host ""
Write-Host "start qwen-terminal.exe - everything you type goes to the model." -ForegroundColor Cyan
Write-Host "inside: /models lists models, /model <name> switches, /help for all commands." -ForegroundColor Cyan

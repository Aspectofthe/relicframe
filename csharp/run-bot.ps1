[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$ForgetCredentials
)

$ErrorActionPreference = 'Stop'
# Codex and some PowerShell 7 hosts prepend their own module runtime to
# PSModulePath. Windows PowerShell can then discover the wrong Security module
# and fail before it can decrypt the saved DPAPI credential. Keep this launcher's
# module search limited to Windows PowerShell's own locations.
$env:PSModulePath = [string]::Join(';', @(
    (Join-Path $env:USERPROFILE 'Documents\WindowsPowerShell\Modules'),
    (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
    (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules')
))
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
$repository = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repository '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$runtimeDirectory = Join-Path $repository 'csharp\runtime'
$credentialPath = Join-Path $runtimeDirectory 'launch-credentials.xml'

if ($ForgetCredentials -and (Test-Path -LiteralPath $credentialPath)) {
    Remove-Item -LiteralPath $credentialPath -Force
}

if (([string]::IsNullOrWhiteSpace($env:RELICFRAME_CSHARP_TOKEN) -or [string]::IsNullOrWhiteSpace($env:RELICFRAME_TEST_GUILD_ID)) -and
    (Test-Path -LiteralPath $credentialPath)) {
    try {
        $savedCredential = Import-Clixml -LiteralPath $credentialPath
        if ($savedCredential -is [System.Management.Automation.PSCredential]) {
            if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_CSHARP_TOKEN)) {
                $env:RELICFRAME_CSHARP_TOKEN = $savedCredential.GetNetworkCredential().Password
            }
            if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_TEST_GUILD_ID)) {
                $env:RELICFRAME_TEST_GUILD_ID = $savedCredential.UserName
            }
        }
    }
    catch {
        Write-Warning 'Saved launch credentials could not be decrypted by this Windows account. Enter them again to replace the local credential file.'
    }
}

if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_CSHARP_TOKEN)) {
    $secureToken = Read-Host 'Paste the Discord bot token (input is hidden)' -AsSecureString
    try {
        $env:RELICFRAME_CSHARP_TOKEN = [System.Net.NetworkCredential]::new('', $secureToken).Password
    }
    finally {
        $secureToken.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_CSHARP_TOKEN)) {
        throw 'A Discord bot token is required. Nothing was saved.'
    }
}
if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_TEST_GUILD_ID)) {
    $env:RELICFRAME_TEST_GUILD_ID = (Read-Host 'Enter the Discord server ID').Trim()
    if ($env:RELICFRAME_TEST_GUILD_ID -notmatch '^\d{17,20}$') {
        throw 'The Discord server ID must contain 17-20 digits. Nothing was saved.'
    }
}

if ($env:RELICFRAME_TEST_GUILD_ID -notmatch '^\d{17,20}$') {
    throw 'RELICFRAME_TEST_GUILD_ID must contain 17-20 digits.'
}

New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$tokenForStorage = ConvertTo-SecureString $env:RELICFRAME_CSHARP_TOKEN -AsPlainText -Force
try {
    [System.Management.Automation.PSCredential]::new($env:RELICFRAME_TEST_GUILD_ID, $tokenForStorage) |
        Export-Clixml -LiteralPath $credentialPath -Force
}
finally {
    $tokenForStorage.Dispose()
}

if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_DATA_DIR)) {
    $env:RELICFRAME_DATA_DIR = Join-Path $repository 'relicframe\data'
}
if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_RUNTIME_DIR)) {
    $env:RELICFRAME_RUNTIME_DIR = $runtimeDirectory
}
if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_EE_LOG)) {
    $env:RELICFRAME_EE_LOG = 'true'
}
if ([string]::IsNullOrWhiteSpace($env:RELICFRAME_TRADE_OCR)) {
    $env:RELICFRAME_TRADE_OCR = 'true'
}

Set-Location -LiteralPath $repository
if (-not $NoBuild) {
    & $dotnet build 'csharp\RelicFrame.Bot' -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $dotnet 'csharp\RelicFrame.Bot\bin\Release\net10.0\RelicFrame.Bot.dll' --test-bot
exit $LASTEXITCODE

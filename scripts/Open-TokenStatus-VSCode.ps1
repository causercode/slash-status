$ErrorActionPreference = "Stop"

$workspace = Split-Path -Parent $PSScriptRoot
$sdkInstall = 'C:\Users\Daniel\.dotnet-tokenstatus-sdk'
$codePath = Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\Code.exe'

if (-not (Test-Path -LiteralPath (Join-Path $sdkInstall 'dotnet.exe'))) {
    throw "The TokenStatus .NET SDK was not found at $sdkInstall."
}

if (-not (Test-Path -LiteralPath $codePath)) {
    $codeCommand = Get-Command code.cmd -ErrorAction SilentlyContinue
    if ($null -eq $codeCommand) {
        throw "VS Code could not be found."
    }

    $codePath = $codeCommand.Source
}

$env:DOTNET_ROOT = $sdkInstall
$env:Path = "$sdkInstall;$env:Path"
Start-Process -FilePath $codePath -ArgumentList @('--new-window', $workspace) -WorkingDirectory $workspace

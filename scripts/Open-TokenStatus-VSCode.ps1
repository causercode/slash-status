$ErrorActionPreference = "Stop"

$workspace = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
$codePath = Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\Code.exe'

if ($null -eq $dotnetCommand) {
    throw "The .NET SDK could not be found on PATH."
}

if (-not (Test-Path -LiteralPath $codePath)) {
    $codeCommand = Get-Command code.cmd -ErrorAction SilentlyContinue
    if ($null -eq $codeCommand) {
        throw "VS Code could not be found."
    }

    $codePath = $codeCommand.Source
}

$sdkInstall = Split-Path -Parent $dotnetCommand.Source
$env:DOTNET_ROOT = $sdkInstall
$env:Path = "$sdkInstall;$env:Path"
Start-Process -FilePath $codePath -ArgumentList @('--new-window', $workspace) -WorkingDirectory $workspace

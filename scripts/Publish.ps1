param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$sdkInstall = 'C:\Users\Daniel\.dotnet-tokenstatus-sdk'
$env:DOTNET_ROOT = $sdkInstall
$env:Path = "$sdkInstall;$env:Path"
$publishDirectory = Join-Path (Get-Location) 'publish'
$artifactDirectory = Join-Path (Get-Location) 'artifacts'
$zipPath = Join-Path $artifactDirectory "TokenStatus-win-x64-$Version.zip"

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

dotnet publish .\src\TokenStatus.App\TokenStatus.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -o $publishDirectory

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath
Write-Output $zipPath

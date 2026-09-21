[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$DotNetPath,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DotNetArguments
)

$ErrorActionPreference = "Stop"
if ($DotNetArguments.Count -eq 0) {
    throw "Supply a dotnet command, for example: .\scripts\Invoke-DotNet.ps1 build .\TokenStatus.sln"
}

$resolver = Join-Path $PSScriptRoot "Resolve-DotNet.ps1"
$dotnet = & $resolver -DotNetPath $DotNetPath
& $dotnet @DotNetArguments
exit $LASTEXITCODE

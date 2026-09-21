[CmdletBinding()]
param(
    [string]$DotNetPath
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$globalJsonPath = Join-Path $repoRoot "global.json"
$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$requiredVersion = [System.Version]::Parse([string]$globalJson.sdk.version)
$rollForward = [string]$globalJson.sdk.rollForward

function Test-CompatibleSdkVersion {
    param([Parameter(Mandatory = $true)][System.Version]$ActualVersion)

    if ($rollForward -eq "latestPatch") {
        return $ActualVersion.Major -eq $requiredVersion.Major -and
            $ActualVersion.Minor -eq $requiredVersion.Minor -and
            [math]::Floor($ActualVersion.Build / 100) -eq [math]::Floor($requiredVersion.Build / 100) -and
            $ActualVersion.Build -ge $requiredVersion.Build
    }

    return $ActualVersion -eq $requiredVersion
}

function Resolve-CandidatePath {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    if (Test-Path -LiteralPath $Candidate -PathType Container) {
        $Candidate = Join-Path $Candidate "dotnet.exe"
    }

    if (!(Test-Path -LiteralPath $Candidate -PathType Leaf)) {
        return $null
    }

    return (Resolve-Path -LiteralPath $Candidate).Path
}

$candidates = [System.Collections.Generic.List[string]]::new()
if (![string]::IsNullOrWhiteSpace($DotNetPath)) {
    $candidates.Add($DotNetPath)
}
else {
    if (![string]::IsNullOrWhiteSpace($env:TOKENSTATUS_DOTNET)) {
        $candidates.Add($env:TOKENSTATUS_DOTNET)
    }
    if (![string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe"))
    }
    $candidates.Add((Join-Path $env:USERPROFILE ".dotnet-tokenstatus-sdk\dotnet.exe"))
    foreach ($command in @(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
        $candidates.Add($command.Source)
    }
}

$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$rejected = [System.Collections.Generic.List[string]]::new()
foreach ($candidate in $candidates) {
    $resolved = Resolve-CandidatePath $candidate
    if ($null -eq $resolved -or !$seen.Add($resolved)) {
        continue
    }

    Push-Location -LiteralPath $repoRoot
    try {
        $versionText = (& $resolved --version 2>$null | Select-Object -Last 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($versionText)) {
            $rejected.Add("$resolved (no compatible SDK available)")
            continue
        }

        $actualVersion = [System.Version]::Parse($versionText.Trim())
        if (Test-CompatibleSdkVersion $actualVersion) {
            Write-Output $resolved
            return
        }

        $rejected.Add("$resolved (SDK $actualVersion)")
    }
    catch {
        $rejected.Add("$resolved (could not query SDK version)")
    }
    finally {
        Pop-Location
    }
}

$details = if ($rejected.Count -gt 0) {
    " Candidates checked: " + ($rejected -join "; ") + "."
}
else {
    ""
}
throw "A .NET SDK compatible with global.json ($requiredVersion, $rollForward) was not found.$details Install the .NET 10 SDK or pass -DotNetPath."

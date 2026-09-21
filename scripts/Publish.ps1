[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$DotNetPath,
    [string]$ReleaseCommit,
    [string]$ReleaseTag,
    [switch]$AllowDeveloperOverride,
    [switch]$RequireSigning,
    [string]$CertificateThumbprint,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location -LiteralPath $repoRoot

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-CheckedTextCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $FilePath @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$output"
    }

    return $output.Trim()
}

function Resolve-DotNetPath {
    param([string]$RequestedPath)

    if ([string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (& (Join-Path $PSScriptRoot "Resolve-DotNet.ps1"))
    }

    $candidate = $RequestedPath
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $candidate = Join-Path $candidate "dotnet.exe"
    }

    if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The selected .NET executable was not found: $RequestedPath"
    }

    return (Resolve-Path -LiteralPath $candidate).Path
}

function Test-SdkVersion {
    param(
        [Parameter(Mandatory = $true)][string]$DotNet,
        [Parameter(Mandatory = $true)][string]$GlobalJsonPath
    )

    $globalJson = Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json
    $required = [string]$globalJson.sdk.version
    $rollForward = [string]$globalJson.sdk.rollForward
    $actual = Invoke-CheckedTextCommand $DotNet @("--version")

    $requiredVersion = [System.Version]::Parse($required)
    $actualVersion = [System.Version]::Parse($actual)
    $valid = $false
    if ($rollForward -eq "latestPatch") {
        $valid = $actualVersion.Major -eq $requiredVersion.Major -and
            $actualVersion.Minor -eq $requiredVersion.Minor -and
            [math]::Floor($actualVersion.Build / 100) -eq [math]::Floor($requiredVersion.Build / 100) -and
            $actualVersion.Build -ge $requiredVersion.Build
    }
    else {
        $valid = $actualVersion -eq $requiredVersion
    }

    if (!$valid) {
        throw "The selected .NET SDK $actual does not satisfy global.json version $required ($rollForward)."
    }

    return $actual
}

function Get-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    return Invoke-CheckedTextCommand "git" $Arguments
}

function Test-SafeRepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (!$fullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $fullPath"
    }

    return $fullPath
}

function Remove-SafeDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = Test-SafeRepositoryPath $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Sign-AndVerifyExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [Parameter(Mandatory = $true)][string]$TimestampUrl
    )

    if (![OperatingSystem]::IsWindows()) {
        throw "Authenticode signing is only supported on Windows."
    }

    $normalizedThumbprint = $Thumbprint.Replace(" ", "").ToUpperInvariant()
    $certificate = Get-ChildItem -Path "Cert:\CurrentUser\My\$normalizedThumbprint" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw "The signing certificate with thumbprint $normalizedThumbprint was not found in the current-user certificate store."
    }

    $signed = Set-AuthenticodeSignature -FilePath $ExecutablePath -Certificate $certificate -TimestampServer $TimestampUrl
    if ($signed.Status -ne "Valid") {
        throw "Authenticode signing failed with status $($signed.Status): $($signed.StatusMessage)"
    }

    $verification = Get-AuthenticodeSignature -FilePath $ExecutablePath
    if ($verification.Status -ne "Valid") {
        throw "The published executable signature is not valid: $($verification.Status)"
    }

    return $verification.Status.ToString()
}

$versionMatch = [regex]::Match($Version, '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?<suffix>-[0-9A-Za-z.-]+)?$')
if (!$versionMatch.Success) {
    throw "Version must be a semantic major.minor.patch value, optionally with a prerelease suffix."
}
if ($RequireSigning -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "Official releases require -CertificateThumbprint and trusted signing material."
}

$isCi = $env:GITHUB_ACTIONS -eq "true" -or $env:CI -eq "true"
if ($AllowDeveloperOverride -and ($isCi -or $RequireSigning -or ![string]::IsNullOrWhiteSpace($CertificateThumbprint))) {
    throw "-AllowDeveloperOverride is only available for local unsigned development publishes."
}

$worktreeChanges = git status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the Git worktree."
}
if (($worktreeChanges | Out-String).Trim().Length -gt 0 -and !$AllowDeveloperOverride) {
    throw "The worktree contains tracked or untracked changes. Release publishing requires a clean source tree."
}

$head = Get-GitText @("rev-parse", "HEAD")
$requestedCommit = $null
if (![string]::IsNullOrWhiteSpace($ReleaseCommit)) {
    $requestedCommit = Get-GitText @("rev-parse", "$ReleaseCommit^{commit}")
}
if (![string]::IsNullOrWhiteSpace($ReleaseTag)) {
    if ($ReleaseTag.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase) -and
        $ReleaseTag.Substring(1) -ne $Version) {
        throw "Release tag $ReleaseTag does not match version $Version."
    }

    $tagCommit = Get-GitText @("rev-parse", "$ReleaseTag^{commit}")
    if ($tagCommit -ne $head) {
        throw "Release tag $ReleaseTag points to $tagCommit, but HEAD is $head."
    }
    $requestedCommit = $tagCommit
}
if ([string]::IsNullOrWhiteSpace($requestedCommit) -and !$AllowDeveloperOverride) {
    throw "Specify -ReleaseCommit or -ReleaseTag. Use -AllowDeveloperOverride only for local unsigned development publishing."
}
if (![string]::IsNullOrWhiteSpace($requestedCommit) -and $requestedCommit -ne $head) {
    throw "HEAD $head does not match the requested release commit $requestedCommit."
}

$dotnet = Resolve-DotNetPath $DotNetPath
$sdkVersion = Test-SdkVersion $dotnet (Join-Path $repoRoot "global.json")
$solution = Join-Path $repoRoot "TokenStatus.sln"

Invoke-CheckedCommand $dotnet @("restore", $solution)
Invoke-CheckedCommand $dotnet @("build", $solution, "--configuration", "Release", "--no-restore")
Invoke-CheckedCommand $dotnet @("test", $solution, "--configuration", "Release", "--no-build")
Invoke-CheckedCommand $dotnet @("format", $solution, "--verify-no-changes")

$auditOutput = Invoke-CheckedTextCommand $dotnet @("list", $solution, "package", "--vulnerable", "--include-transitive")
if ($auditOutput -match "has the following vulnerable packages" -or
    $auditOutput -match "(?im)^\s*\S+\s+\S+\s+(Critical|High|Moderate|Low)\s+") {
    throw "The NuGet vulnerability audit reported vulnerable packages."
}

$artifactDirectory = Join-Path $repoRoot "artifacts"
$stagingDirectory = Join-Path $artifactDirectory "staging\TokenStatus-win-x64-$Version"
$unsignedSuffix = if ($RequireSigning -or ![string]::IsNullOrWhiteSpace($CertificateThumbprint)) { "" } else { "-UNSIGNED" }
$zipName = "TokenStatus-win-x64-$Version$unsignedSuffix.zip"
$manifestName = "TokenStatus-win-x64-$Version$unsignedSuffix.manifest.json"
$zipPath = Join-Path $artifactDirectory $zipName
$manifestPath = Join-Path $artifactDirectory $manifestName

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Remove-SafeDirectory $stagingDirectory
foreach ($oldArtifact in @($zipPath, $manifestPath)) {
    if (Test-Path -LiteralPath $oldArtifact) {
        Remove-Item -LiteralPath (Test-SafeRepositoryPath $oldArtifact) -Force
    }
}
New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

$assemblyVersion = "$($versionMatch.Groups['major'].Value).$($versionMatch.Groups['minor'].Value).$($versionMatch.Groups['patch'].Value).0"
$informationalVersion = "$Version+commit.$head"
$publishArguments = @(
    "publish",
    (Join-Path $repoRoot "src\TokenStatus.App\TokenStatus.App.csproj"),
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$informationalVersion",
    "-p:SourceRevisionId=$head",
    "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-o", $stagingDirectory
)
Invoke-CheckedCommand $dotnet $publishArguments

$publishedFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -File)
$publishedExecutable = Join-Path $stagingDirectory "TokenStatus.exe"
if ($publishedFiles.Count -ne 1 -or !(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "The publish directory contains unexpected files; expected only TokenStatus.exe."
}

$signatureStatus = "Unsigned"
$shouldSign = $RequireSigning -or ![string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($shouldSign) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw "Official releases require -CertificateThumbprint and trusted signing material."
    }
    $signatureStatus = Sign-AndVerifyExecutable $publishedExecutable $CertificateThumbprint $TimestampServer
}

$executableHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
$buildTimeUtc = (Get-Date).ToUniversalTime().ToString("O")
$releaseKind = if ($signatureStatus -eq "Valid") { "OFFICIAL" } else { "UNSIGNED" }
$internalManifest = [ordered]@{
    schemaVersion = 1
    releaseKind = $releaseKind
    version = $Version
    commit = $head
    rid = "win-x64"
    sdkVersion = $sdkVersion
    buildTimeUtc = $buildTimeUtc
    signatureStatus = $signatureStatus
    executable = [ordered]@{
        fileName = "TokenStatus.exe"
        sha256 = $executableHash
        bytes = (Get-Item -LiteralPath $publishedExecutable).Length
    }
}
$internalManifestPath = Join-Path $stagingDirectory "release-manifest.json"
$internalManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $internalManifestPath -Encoding UTF8

Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$externalManifest = [ordered]@{
    schemaVersion = $internalManifest.schemaVersion
    releaseKind = $internalManifest.releaseKind
    version = $internalManifest.version
    commit = $internalManifest.commit
    rid = $internalManifest.rid
    sdkVersion = $internalManifest.sdkVersion
    buildTimeUtc = $internalManifest.buildTimeUtc
    signatureStatus = $internalManifest.signatureStatus
    executable = $internalManifest.executable
    zip = [ordered]@{
        fileName = $zipName
        sha256 = $zipHash
        bytes = (Get-Item -LiteralPath $zipPath).Length
    }
}
$externalManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$verifiedExecutableHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
$verifiedZipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($verifiedExecutableHash -ne $executableHash -or $verifiedZipHash -ne $zipHash) {
    throw "The release manifest hash verification failed."
}

Write-Output $zipPath
Write-Output $manifestPath

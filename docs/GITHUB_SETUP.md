# Maintaining /status on GitHub

The product is named **/status** and its source repository is [`causercode/slash-status`](https://github.com/causercode/slash-status). The repository name spells out "slash" because GitHub repository names cannot contain `/`.

Clone it with:

```powershell
git clone https://github.com/causercode/slash-status.git
Set-Location .\slash-status
```

## Repository settings

The repository description is:

> A privacy-conscious Windows tray app for Codex and OpenCode Go usage, quota alerts, and keep-awake controls.

Repository topics:

```text
windows tray-app codex opencode quota dotnet winforms
```

Keep Issues and private vulnerability reporting enabled. Protect `main` and require the `verify` job before merging pull requests. Secret scanning, push protection, Dependabot alerts, and Dependabot security updates should also remain enabled.

## Optional Authenticode signing

The release workflow works without a certificate and clearly labels those files `UNSIGNED`. To publish Authenticode-signed Windows executables, add all three required repository Actions secrets:

- `TOKENSTATUS_SIGNING_CERTIFICATE_BASE64`
- `TOKENSTATUS_SIGNING_CERTIFICATE_PASSWORD`
- `TOKENSTATUS_SIGNING_CERTIFICATE_THUMBPRINT`
- `TOKENSTATUS_TIMESTAMP_SERVER` (optional; defaults to DigiCert)

The certificate value must be the base64 encoding of the PFX file. Keep the PFX and password outside the repository.

To prepare the certificate value locally without printing it:

```powershell
$bytes = [IO.File]::ReadAllBytes('C:\secure\path\codesigning.pfx')
$base64 = [Convert]::ToBase64String($bytes)
$base64 | Set-Clipboard
```

## Create a release

Make sure `main` is clean and its GitHub Actions verification is green, then create an annotated semantic-version tag:

```powershell
$version = '1.0.0'
git switch main
git pull --ff-only
git tag -a "v$version" -m "/status $version"
git push origin "v$version"
```

The workflow verifies the solution, builds `win-x64` and `win-arm64` archives, uploads their manifests, and creates the GitHub Release automatically. It signs both executables only when the complete certificate configuration is present.

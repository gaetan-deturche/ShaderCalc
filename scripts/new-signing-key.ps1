# Makes the update signing key (once): the private key goes straight into the repository's Actions secret
# SHADERCALC_SIGNING_KEY (through gh, never on disk), the public key into app/src-tauri/update-public-key.txt, which
# the app builds in. Commit that file afterwards. A new key makes every older build reject the new releases.
param(
    [string]$Repository = 'gaetan-deturche/ShaderCalc',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$publicKeyFile = Join-Path $PSScriptRoot '..\app\src-tauri\update-public-key.txt'
if ((Test-Path $publicKeyFile) -and (Get-Content $publicKeyFile -Raw).Trim() -and -not $Force) {
    throw "A public key already exists in $publicKeyFile; builds with it would reject releases signed with a new key. Use -Force to replace it."
}
$privateKey = node (Join-Path $PSScriptRoot 'sign-release.mjs') keygen $publicKeyFile
if ($LASTEXITCODE -ne 0) { throw 'key generation failed' }
($privateKey -join "`n") | gh secret set SHADERCALC_SIGNING_KEY --repo $Repository
if ($LASTEXITCODE -ne 0) { throw 'gh secret set failed' }
"Secret SHADERCALC_SIGNING_KEY set on $Repository."
"Public key written to $publicKeyFile - commit it."

# Sets the app version (tauri.conf.json, the app crate, package.json and the lock files) before a release:
#   scripts\bump-version.ps1 0.2.0, commit, then `git tag v0.2.0` and push the tag to run the release workflow.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z, got '$Version'" }
$root = Join-Path $PSScriptRoot '..'
$utf8 = [System.Text.UTF8Encoding]::new($false)
function Set-Version([string]$Path, [string]$Pattern, [string]$Replacement) {
    $file = Join-Path $root $Path
    $text = [System.IO.File]::ReadAllText($file, $utf8)
    $updated = [regex]::new($Pattern, 'Multiline').Replace($text, $Replacement, 1)
    if ($updated -eq $text -and $text -notmatch [regex]::Escape($Version)) { throw "No version found in $Path" }
    [System.IO.File]::WriteAllText($file, $updated, $utf8)
}
Set-Version 'app/src-tauri/tauri.conf.json' '"version": "[^"]*"' "`"version`": `"$Version`""
Set-Version 'app/src-tauri/Cargo.toml' '^version = "[^"]*"' "version = `"$Version`""
Set-Version 'app/package.json' '"version": "[^"]*"' "`"version`": `"$Version`""
Push-Location (Join-Path $root 'app')
try { npm install --package-lock-only --silent } finally { Pop-Location }
# Rewrites the app's entry in Cargo.lock (cargo reports on stderr, which Windows PowerShell must not treat as an error)
$ErrorActionPreference = 'Continue'
cargo update --workspace --offline --manifest-path (Join-Path $root 'Cargo.toml')
"Version set to $Version. Commit, then: git tag v$Version; git push origin v$Version"

# Downloads the DirectX Shader Compiler release the reference is tested with into third_party/dxc
# (dxcompiler.dll, dxil.dll and their license). The app embeds them; tests find them through .cargo/config.toml.
param(
    [string]$Tag = 'v1.9.2602',
    [string]$Asset = 'dxc_2026_02_20.zip'
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\third_party\dxc'
New-Item -ItemType Directory -Force $target | Out-Null
$zip = Join-Path ([System.IO.Path]::GetTempPath()) $Asset
$url = "https://github.com/microsoft/DirectXShaderCompiler/releases/download/$Tag/$Asset"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
$extracted = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetFileNameWithoutExtension($Asset))
Expand-Archive -Path $zip -DestinationPath $extracted -Force
Copy-Item (Join-Path $extracted 'bin\x64\dxcompiler.dll') $target -Force
Copy-Item (Join-Path $extracted 'bin\x64\dxil.dll') $target -Force
Get-ChildItem $extracted -Filter 'LICENSE*' | Copy-Item -Destination $target -Force
Remove-Item $zip, $extracted -Recurse -Force
Get-ChildItem $target | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }

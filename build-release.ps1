# Builds release\WallTerm-<version>\ (plus a .zip of it) with x64 and arm64 exes and the install scripts.
#   powershell -ExecutionPolicy Bypass -File build-release.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

[xml]$proj = Get-Content (Join-Path $root 'WallTerm.csproj')
$version = @($proj.Project.PropertyGroup.Version)[0]
$name = "WallTerm-$version"
$out = Join-Path $root "release\$name"
$app = Join-Path $out 'app'
$tmp = Join-Path $root 'obj\release-publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

foreach ($arch in 'x64', 'arm64') {
    $pub = Join-Path $tmp $arch
    dotnet publish (Join-Path $root 'WallTerm.csproj') -c Release -r "win-$arch" --self-contained false `
        -p:PublishSingleFile=true -o $pub --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }
    Copy-Item (Join-Path $pub 'WallTerm.exe') (Join-Path $app "WallTerm-$arch.exe")
}

Copy-Item (Join-Path $root 'installer\setup.ps1') $app
Copy-Item (Join-Path $root 'installer\*.cmd') $out
Copy-Item (Join-Path $root 'installer\README.txt') $out
Copy-Item (Join-Path $root 'README.md') (Join-Path $out 'MANUAL.md')

$zip = Join-Path $root "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip

Write-Host "`nRelease: $out"
Write-Host "Zip:     $zip"

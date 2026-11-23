param(
  [string]$Version = "1.0.0",
  [string]$ChangelogPath = "",
  [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'LcdFusion\LcdFusion.csproj'

# 1) publish (framework-dependent net48, x64)
$pub = Join-Path $env:TEMP 'lcdpublish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
$pubArgs = @('publish', $proj, '-c', 'Release', '-o', $pub, '--nologo', "/p:Version=$Version", "/p:InformationalVersion=$Version")
if ($NoRestore) { $pubArgs += '--no-restore' }
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2) stage the portable payload (MIT binaries + docs + licenses, no pdb, no GPL external renderer binary)
$stageParent = Join-Path $env:TEMP 'lcdpack'
$stage = Join-Path $stageParent "LcdFusion-$Version-portable"
if (Test-Path $stageParent) { Remove-Item $stageParent -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'licenses') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'renderer') | Out-Null

Get-ChildItem $pub -File |
  Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'wpe-renderer.exe' } |
  ForEach-Object { Copy-Item $_.FullName $stage }

Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'LcdFusion\THIRD_PARTY_NOTICES.md') $stage
Copy-Item (Join-Path $root 'LcdFusion\licenses\*') (Join-Path $stage 'licenses')
if (Test-Path (Join-Path $root 'docs\wpe-renderer-ipc.md')) {
  Copy-Item (Join-Path $root 'docs\wpe-renderer-ipc.md') (Join-Path $stage 'renderer\wpe-renderer-ipc.md')
}
if ($ChangelogPath) {
  $resolvedChangelog = Resolve-Path $ChangelogPath -ErrorAction Stop
  Copy-Item $resolvedChangelog (Join-Path $stage 'CHANGELOG.md')
}

# Optional external Wallpaper Engine renderer distribution note (keeps MIT and GPL-2.0 strictly separated)
$rendererNote = @"
Optional External Wallpaper Engine Renderer (Separate Distribution)
===================================================================

LCD Fusion (MIT license, x64) does NOT bundle any GPL-2.0 renderer binary
or any copyrighted Wallpaper Engine assets.

To enable Wallpaper Engine Scene backgrounds:
1. Obtain or build the standalone GPL-2.0 x64 renderer (LcdFusion-WpeRenderer)
   separately from its own repository/release package.
2. Place `wpe-renderer.exe` in this `renderer\` folder:
     <LcdFusionDir>\renderer\wpe-renderer.exe
   (or select its path manually inside LCD Fusion's Wallpaper dialog).
3. Ensure Wallpaper Engine is installed via Steam on your PC; LCD Fusion reads
   your local Steam Workshop (431960) and `wallpaper_engine\assets` at runtime.

See `wpe-renderer-ipc.md` in this folder for the public IPC v1 protocol.
"@
Set-Content -Path (Join-Path $stage 'renderer\README.txt') -Value $rendererNote -Encoding UTF8

# 3) zip
$zip = Join-Path $root "LcdFusion-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageParent, $zip)
Write-Output $zip

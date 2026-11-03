# Checks a self-contained win-x64 build (or -Publish) folder: bundled runtime, WinUI resources, x64 with no 32-bit native file. Exit 1 on any FAIL.
# [AC-x64-naudio-1] [EDGE-x64-naudio-21] [EDGE-x64-naudio-23] [EDGE-x64-naudio-25] [EDGE-x64-naudio-28]
param([string]$Dir, [int]$ExpectMajor = 10, [switch]$Publish)

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Dir) { $Dir = Join-Path $repo "bin\Release\net$ExpectMajor.0-windows10.0.19041.0\win-x64" }
$app = 'Pickles Playlist Editor'
$failed = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Output "PASS $name" } else { Write-Output "FAIL $name"; $script:failed++ }
}
function Major([string]$file) {
    $p = Join-Path $Dir $file
    if (-not (Test-Path $p)) { return -1 }
    return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($p).FileMajorPart
}

Write-Output "Checking $Dir (.NET $ExpectMajor$(if ($Publish) { ', publish layout' }))"
$exe = Join-Path $Dir "$app.exe"
Check 'exe' (Test-Path $exe)
foreach ($pri in "$app.pri", 'Microsoft.UI.Xaml.Controls.pri') { Check $pri (Test-Path (Join-Path $Dir $pri)) }
if (-not $Publish) {
    foreach ($xaml in Get-ChildItem (Join-Path $repo 'ui') -Filter *.xaml) {
        Check "ui\$($xaml.BaseName).xbf" (Test-Path (Join-Path $Dir "ui\$($xaml.BaseName).xbf"))
    }
}

$rcPath = Join-Path $Dir "$app.runtimeconfig.json"
$rc = if (Test-Path $rcPath) { (Get-Content $rcPath -Raw | ConvertFrom-Json).runtimeOptions } else { $null }
Check "runtimeconfig tfm net$ExpectMajor.0" ($rc -and $rc.tfm -eq "net$ExpectMajor.0")
Check 'runtimeconfig self-contained (no framework/frameworks)' ($rc -and -not $rc.PSObject.Properties['framework'] -and -not $rc.PSObject.Properties['frameworks'])
foreach ($fw in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
    $v = if ($rc -and $rc.includedFrameworks) { ($rc.includedFrameworks | Where-Object name -eq $fw).version } else { $null }
    Check "includedFrameworks $fw $ExpectMajor.x" ($v -and ([int]($v -split '\.')[0]) -eq $ExpectMajor)
}
foreach ($dll in 'hostfxr.dll', 'coreclr.dll', 'System.Private.CoreLib.dll') { Check "$dll $ExpectMajor.x" ((Major $dll) -eq $ExpectMajor) }

$depsPath = Join-Path $Dir "$app.deps.json"
$target = if (Test-Path $depsPath) { (Get-Content $depsPath -Raw | ConvertFrom-Json).runtimeTarget.name } else { $null }
Check "deps.json .NETCoreApp v$ExpectMajor.0/win-x64" ($target -eq ".NETCoreApp,Version=v$ExpectMajor.0/win-x64")

# RestartAgent.exe is needed by AppInstance.Restart (language switch).
foreach ($dll in 'Microsoft.ui.xaml.dll', 'Microsoft.ui.xaml.resources.19h1.dll', 'Microsoft.WindowsAppRuntime.dll', 'RestartAgent.exe', 'System.Security.Cryptography.ProtectedData.dll') {
    Check $dll (Test-Path (Join-Path $Dir $dll))
}

# Windows App SDK: the runtime is the csproj's major.minor; WinUI (own 3.x versioning) is the resolved package's file.
$sdk = [regex]::Match((Get-Content (Join-Path $repo "$app.csproj") -Raw), 'Include="Microsoft\.WindowsAppSDK"\s+Version="([^"]+)"').Groups[1].Value
$mm = ($sdk -split '\.')[0..1] -join '.'
$rt = Join-Path $Dir 'Microsoft.WindowsAppRuntime.dll'
$rtv = if (Test-Path $rt) { $i = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($rt); "$($i.ProductMajorPart).$($i.ProductMinorPart)" } else { $null }
Check "Microsoft.WindowsAppRuntime.dll $mm" ($sdk -and $rtv -eq $mm)
$assets = Get-Content (Join-Path $repo 'obj\project.assets.json') -Raw | ConvertFrom-Json
$winui = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.WindowsAppSDK.WinUI/*' } | Select-Object -First 1
$shipped = if ($winui) { Join-Path ($assets.packageFolders.PSObject.Properties | Select-Object -First 1).Name "$($winui.Value.path)\runtimes-framework\win-x64\native\Microsoft.ui.xaml.dll" } else { $null }
$xaml = Join-Path $Dir 'Microsoft.ui.xaml.dll'
Check "Microsoft.ui.xaml.dll from $($winui.Name)" ($shipped -and (Test-Path $shipped) -and (Test-Path $xaml) -and
    (Get-FileHash $shipped).Hash -eq (Get-FileHash $xaml).Hash)

# PE kind from the headers: managed if the CLI data directory (14) is set, else the machine type.
# Data directories start at optional header +96 for PE32 (magic 0x10b) and +112 for PE32+ (0x20b).
function Get-PeKind([string]$path) {
    try { $b = [System.IO.File]::ReadAllBytes($path) } catch { return 'unreadable' }
    if ($b.Length -lt 0x40 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return 'not-pe' }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $b.Length -or [BitConverter]::ToUInt32($b, $pe) -ne 0x4550) { return 'not-pe' }
    $machine = [BitConverter]::ToUInt16($b, $pe + 4)
    $opt = $pe + 24
    $dirs = switch ([BitConverter]::ToUInt16($b, $opt)) { 0x10b { $opt + 96 } 0x20b { $opt + 112 } default { -1 } }
    if ($dirs -gt 0 -and $dirs + 15 * 8 -le $b.Length -and [BitConverter]::ToUInt32($b, $dirs + 14 * 8) -ne 0) { return 'managed' }
    switch ($machine) { 0x14c { 'x86' } 0x8664 { 'x64' } default { 'other' } }
}

# Controls first, so a broken classifier can't pass the scan by accident.
$sys = $env:WINDIR
Check 'pe-scan control x86 native flagged' ((Get-PeKind "$sys\SysWOW64\whoami.exe") -eq 'x86')
Check 'pe-scan control x64 native not flagged' ((Get-PeKind "$sys\System32\whoami.exe") -eq 'x64')
$il = Join-Path $Dir 'Newtonsoft.Json.dll'
Check 'pe-scan control IL-only PE32 not flagged' ((Get-PeKind $il) -eq 'managed')
Check 'pe-scan control R2R PE32+ not flagged' ((Get-PeKind (Join-Path $Dir 'System.Private.CoreLib.dll')) -eq 'managed')

Check 'exe x64' ((Get-PeKind $exe) -eq 'x64')
$x86 = @(Get-ChildItem $Dir -Recurse -File -Include *.dll, *.exe | Where-Object { (Get-PeKind $_.FullName) -eq 'x86' } |
    ForEach-Object { $_.FullName.Substring($Dir.Length).TrimStart('\') })
foreach ($f in $x86) { Write-Output "  32-bit: $f" }
Check 'no 32-bit native file in the payload' ($x86.Count -eq 0)
Check 'libzplay.dll absent' (-not (Test-Path (Join-Path $Dir 'libzplay.dll')))
foreach ($dll in 'SoundTouch.Net.dll', 'NAudio.WinMM.dll', 'NVorbis.dll', 'THIRD-PARTY-NOTICES.md', 'licenses\LGPL-2.1.txt', 'licenses\NAudio-MIT.txt', 'licenses\NAudio.Vorbis-MIT.txt', 'licenses\NVorbis-MIT.txt') {
    Check $dll (Test-Path (Join-Path $Dir $dll))
}

# The bundled ffmpeg stays the tracked essentials build; the AI score's full build and model are downloads, never payload.
$ff = Join-Path $Dir 'ffmpeg.exe'
Check 'ffmpeg.exe' (Test-Path $ff)
Check 'ffmpeg.exe x64' ((Test-Path $ff) -and (Get-PeKind $ff) -eq 'x64')
Check 'ffmpeg.exe matches tracked essentials' ((Test-Path $ff) -and (Get-FileHash $ff).Hash -eq (Get-FileHash (Join-Path $repo 'ffmpeg.exe')).Hash)
Check 'licenses\FFmpeg-GPL-3.0.txt' (Test-Path (Join-Path $Dir 'licenses\FFmpeg-GPL-3.0.txt'))
Check 'no model files in payload' (@(Get-ChildItem $Dir -Recurse -File -Include *.onnx, *.onnx.data).Count -eq 0)

if ($failed) { Write-Output "$failed check(s) failed"; exit 1 }
Write-Output 'All checks passed'
exit 0

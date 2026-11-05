# Proves an installed x86 Velopack app takes an x64 update on the same channel: installs devtools\ArchProbe as x86 0.0.1,
# packs it as x64 0.0.2 into the same local feed, lets it update itself, then checks it runs as x64 and uninstalls.
# [AC-x64-naudio-8] [EDGE-x64-naudio-22] [EDGE-x64-naudio-23] [EDGE-x64-naudio-29]
# Also packs the real build (bin\...\win-x64) to read its nuspec. Every pack writes under the work dir. Exit 1 on any FAIL.
param([string]$Work = (Join-Path $env:TEMP "PicklesArchProbe-$([guid]::NewGuid().ToString('N'))"))

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path $PSScriptRoot -Parent
$id = 'PicklesArchProbe'
$inst = Join-Path $env:LOCALAPPDATA $id
$report = Join-Path $env:TEMP "$id.txt"
$failed = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Output "PASS $name" } else { Write-Output "FAIL $name"; $script:failed++ }
}
# '""' reaches vpk as an empty value (Windows PowerShell drops a bare ''), so no shortcuts are made.
function Pack([string]$packId, [string]$version, [string]$dir, [string]$exe, [string]$rid, [string]$out) {
    & vpk pack --packId $packId --packVersion $version --packDir $dir --mainExe $exe -r $rid --outputDir $out --shortcuts '""' --exclude '.*\.(pdb|dat)$' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "vpk pack $packId $version failed ($LASTEXITCODE)" }
}
function Probe([string[]]$a) {
    if (Test-Path $report) { Remove-Item $report }
    & (Join-Path $inst 'current\ArchProbe.exe') @a | Out-Null
    if (Test-Path $report) { (Get-Content $report -Raw).Trim() } else { '' }
}

if (Test-Path $inst) { throw "$inst already exists; uninstall the earlier probe first" }
$before = @(Get-ChildItem $repo -Recurse -File -Include 'releases.*.json', '*.nupkg' -ErrorAction SilentlyContinue | ForEach-Object FullName)
New-Item -ItemType Directory -Force $Work | Out-Null
Write-Output "Work dir $Work"
try {
    foreach ($arch in 'x86', 'x64') {
        & dotnet publish (Join-Path $repo 'devtools\ArchProbe\ArchProbe.csproj') -c Release -r "win-$arch" -o (Join-Path $Work $arch) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "publish ArchProbe $arch failed" }
    }
    $rel = Join-Path $Work 'rel'
    Pack $id '0.0.1' (Join-Path $Work 'x86') 'ArchProbe.exe' 'win-x86' $rel
    $setup = Start-Process (Join-Path $rel "$id-win-Setup.exe") -ArgumentList '--silent' -PassThru -Wait
    Check 'x86 setup' ($setup.ExitCode -eq 0)
    Check 'installed copy runs as x86 0.0.1' ((Probe @()) -eq '0.0.1 X86')

    Pack $id '0.0.2' (Join-Path $Work 'x64') 'ArchProbe.exe' 'win-x64' $rel
    Check 'x64 release on the same channel (releases.win.json)' (((Get-Content (Join-Path $rel 'releases.win.json') -Raw) -match "$id-0\.0\.2-full\.nupkg"))
    Probe @('update', $rel) | Out-Null
    Start-Sleep 3
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Process Update -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$inst*" }) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    Check 'after the update (delta in the feed) it runs as x64 0.0.2' ((Probe @()) -eq '0.0.2 X64')

    $realOut = Join-Path $Work 'realpack'
    $real = Join-Path $repo 'bin\Release\net10.0-windows10.0.19041.0\win-x64'
    Pack 'PicklesArchProbeReal' '0.0.1-archprobe' $real 'Pickles Playlist Editor.exe' 'win-x64' $realOut
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Get-ChildItem $realOut -Filter '*-full.nupkg' | Select-Object -First 1).FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        $nuspec = (New-Object IO.StreamReader($entry.Open())).ReadToEnd()
    } finally { $zip.Dispose() }
    Check 'real build nuspec machineArchitecture x64' ($nuspec -match '<machineArchitecture>x64</machineArchitecture>')
    Check 'real build nuspec channel win' ($nuspec -match '<channel>win</channel>')
    $upd = [IO.File]::ReadAllBytes((Join-Path $inst 'Update.exe')); $pe = [BitConverter]::ToInt32($upd, 0x3C)
    Write-Output "INFO Velopack Update.exe machine 0x$('{0:x}' -f [BitConverter]::ToUInt16($upd, $pe + 4)) (added at pack time, outside the app payload)"
    Check 'delta package built for 0.0.2' (Test-Path (Join-Path $rel "$id-0.0.2-delta.nupkg"))
}
finally {
    $u = Join-Path $inst 'Update.exe'
    if (Test-Path $u) { Start-Process $u -ArgumentList 'uninstall', '--silent' -Wait | Out-Null; Start-Sleep 3 }
    $left = @()
    if (Test-Path $inst) { $left += $inst }
    if (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$id") { $left += "HKCU Uninstall\$id" }
    $left += @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')), "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Recurse -Filter "$id*.lnk" -ErrorAction SilentlyContinue | ForEach-Object FullName)
    foreach ($l in $left) { Write-Output "LEFTOVER $l" }
    Check 'probe uninstalled with nothing left behind' ($left.Count -eq 0)
    $after = @(Get-ChildItem $repo -Recurse -File -Include 'releases.*.json', '*.nupkg' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    Check 'no release files written outside the work dir' (@($after | Where-Object { $before -notcontains $_ }).Count -eq 0)
}

if ($failed) { Write-Output "$failed check(s) failed"; exit 1 }
Write-Output 'All checks passed'
exit 0

# Checks a self-contained win-x86 build (or -Publish) folder: bundled runtime, WinUI resources, x86 + LAA. Exit 1 on any FAIL.
param([string]$Dir, [int]$ExpectMajor = 10, [switch]$Publish)

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Dir) { $Dir = Join-Path $repo "bin\Release\net$ExpectMajor.0-windows10.0.19041.0\win-x86" }
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
Check "deps.json .NETCoreApp v$ExpectMajor.0/win-x86" ($target -eq ".NETCoreApp,Version=v$ExpectMajor.0/win-x86")

foreach ($dll in 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'System.Security.Cryptography.ProtectedData.dll') {
    Check $dll (Test-Path (Join-Path $Dir $dll))
}

# PE header: machine at e_lfanew+4, characteristics at e_lfanew+22 (0x20 = large address aware).
$x86 = $false; $laa = $false
if (Test-Path $exe) {
    $b = [System.IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $x86 = [BitConverter]::ToUInt16($b, $pe + 4) -eq 0x14c
    $laa = ($b[$pe + 22] -band 0x20) -ne 0
}
Check 'exe x86' $x86
Check 'exe large-address-aware' $laa

if ($failed) { Write-Output "$failed check(s) failed"; exit 1 }
Write-Output 'All checks passed'
exit 0

# Stages the CI-fetched assets of an aiscan-v<N> release into -OutDir, verifying every pin first.
# Each "gyan" entry: the pinned archive (cache or download), its size and SHA-256, the extracted file's size and
# SHA-256, the ffmpeg build string, --enable-libsoxr, and a real soxr resample. "manual" entries are uploaded later
# by Publish-AiscanModels.ps1. Prints the pin lines. Any failure exits 1 and leaves -OutDir empty.
# -EmitPins (local only, never in CI) allows a blank exe pin so the first pins can be printed.
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Manifest,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$CacheDir,
    [switch]$Offline,
    [switch]$EmitPins
)

$ErrorActionPreference = 'Stop'
if (-not $Manifest) { $Manifest = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'releases.json' } # PS 5.1: no $PSScriptRoot in param defaults
$ProgressPreference = 'SilentlyContinue'

function Fail([string]$msg) {
    Write-Output "FAIL $msg"
    if (Test-Path $OutDir) { Get-ChildItem $OutDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
function Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

if ($Tag -cnotmatch '^aiscan-v\d+\z') { Fail "tag '$Tag' is not aiscan-v<N>" }
$all = Get-Content $Manifest -Raw | ConvertFrom-Json
$entries = $all.PSObject.Properties | Where-Object Name -ceq $Tag | ForEach-Object Value
if (-not $entries) { Fail "tag '$Tag' is not listed in $Manifest" }

foreach ($e in $entries) {
    if ($e.source.kind -ne 'gyan') { continue }
    if (-not $e.source.archiveSha256 -or -not $e.source.archiveBytes) { Fail "$($e.name): archive pin not filled" }
    if (-not $EmitPins -and (-not $e.sha256 -or -not $e.bytes)) { Fail "$($e.name): pins not filled" }
}

New-Item -ItemType Directory -Force $OutDir, $CacheDir | Out-Null
if (@(Get-ChildItem $OutDir -Force).Count) { Fail "$OutDir is not empty" }

$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
$tar = Join-Path $env:WINDIR 'System32\tar.exe'

foreach ($e in $entries) {
    if ($e.source.kind -eq 'manual') { Write-Output "pending $($e.name): upload with Publish-AiscanModels.ps1"; continue }
    if ($e.source.kind -ne 'gyan') { Fail "$($e.name): unknown source kind '$($e.source.kind)'" }
    $s = $e.source
    $archive = Join-Path $CacheDir ([IO.Path]::GetFileName(([Uri]$s.url).AbsolutePath))
    if (-not (Test-Path $archive)) {
        if ($Offline) { Fail "$archive missing (offline)" }
        Write-Output "downloading $($s.url)"
        Invoke-WebRequest -Uri $s.url -OutFile $archive -UseBasicParsing
    }
    if ((Get-Item $archive).Length -ne [long]$s.archiveBytes) { Remove-Item $archive -Force; Fail "$($e.name): archive size mismatch" }
    if ((Sha $archive) -ne $s.archiveSha256) { Remove-Item $archive -Force; Fail "$($e.name): archive SHA-256 mismatch" }

    $work = Join-Path ([IO.Path]::GetTempPath()) ("aiscan-x-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        if ($sevenZip) { & $sevenZip x -y "-o$work" $archive $s.path | Out-Null }
        elseif (Test-Path $tar) { & $tar -xf $archive -C $work $s.path | Out-Null }
        else { Fail 'no extractor (7z or tar.exe)' }
        $exe = Join-Path $work ($s.path -replace '/', '\')
        if (-not (Test-Path $exe)) { Fail "$($s.path) not found in the archive" }

        $bytes = (Get-Item $exe).Length; $sha = Sha $exe
        if (-not $EmitPins -or $e.sha256) {
            if ($bytes -ne [long]$e.bytes) { Fail "$($e.name): size $bytes, pinned $($e.bytes)" }
            if ($sha -ne $e.sha256) { Fail "$($e.name): SHA-256 mismatch" }
        }

        if ($e.name -eq 'ffmpeg.exe') {
            $version = (& $exe -hide_banner -version | Select-Object -First 1)
            if ($version -notmatch 'version') { Fail "ffmpeg -version printed '$version'" }
            if ($version -notlike "*$($s.version)*") { Fail "build '$version' is not $($s.version)" }
            Write-Output "build: $version"
            $conf = (& $exe -hide_banner -buildconf) -join "`n"
            if ($conf -notmatch '--enable-libsoxr') { Fail 'build has no --enable-libsoxr' }
            Write-Output 'buildconf: --enable-libsoxr'

            # Binary output goes to a file: a PowerShell pipeline would turn it into text lines.
            $f32 = Join-Path $work 'soxr.f32'; $err = Join-Path $work 'soxr.err'
            $p = Start-Process -FilePath $exe -NoNewWindow -Wait -PassThru -RedirectStandardError $err -ArgumentList @(
                '-v', 'error', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=44100:duration=1',
                '-af', 'aresample=16000:resampler=soxr:precision=20', '-ac', '1', '-f', 'f32le', '-y', "`"$f32`"")
            $len = if (Test-Path $f32) { (Get-Item $f32).Length } else { -1 }
            $errText = if (Test-Path $err) { (Get-Content $err -Raw) } else { '' }
            if ($p.ExitCode -ne 0 -or $errText -or $len -lt (16000 - 32) * 4 -or $len -gt (16000 + 32) * 4) {
                Fail "soxr resample failed (exit $($p.ExitCode), $len bytes) $errText"
            }
            Write-Output "soxr: $($len / 4) samples at 16 kHz"
        }
        Copy-Item $exe (Join-Path $OutDir $e.name)
        Write-Output "pin json: `"name`": `"$($e.name)`", `"bytes`": $bytes, `"sha256`": `"$sha`""
        Write-Output "pin c#:   new(`"$($e.name)`", `"$Tag`", ${bytes}L, `"$sha`"),"
    }
    finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
foreach ($e in $entries | Where-Object { $_.source.kind -eq 'manual' }) {
    Write-Output "pin c#:   new(`"$($e.name)`", `"$Tag`", $($e.bytes)L, `"$($e.sha256)`"),"
}
Write-Output "staged $Tag into $OutDir"
exit 0

# Uploads the "manual" assets of an aiscan-v<N> release (the SONICS model files) into the draft that
# upload-aiscan.yml created, then publishes it, never as Latest, once every pinned asset is there.
# Run by the developer with a gh login:  devtools\aiscan\Publish-AiscanModels.ps1 -Tag aiscan-v1 -Dir E:\tmp\aiscan-v1
# Local files are checked against their pins before any gh call. Re-running after a partial upload is safe.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Dir,
    [string]$Manifest,
    [string]$Gh = 'gh',
    [string]$Repo = 'solona-m/Pickles-Playlist-Editor'
)

$ErrorActionPreference = 'Stop'
if (-not $Manifest) { $Manifest = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'releases.json' }

function Fail([string]$msg) { Write-Output "FAIL $msg"; exit 1 }
function Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function GhJson([string[]]$a) {
    $out = & $Gh @a
    if ($LASTEXITCODE -ne 0) { Fail "gh $($a -join ' ') failed" }
    ($out -join "`n") | ConvertFrom-Json
}
function DraftRelease() {
    # The tags endpoint does not list drafts; find it among all releases.
    $all = GhJson @('api', "repos/$Repo/releases?per_page=100")
    $r = @($all | Where-Object { $_.tag_name -ceq $Tag }) | Select-Object -First 1
    if (-not $r) { Fail "no release for $Tag; push the tag first so upload-aiscan.yml creates the draft" }
    $r
}

if ($Tag -cnotmatch '^aiscan-v\d+\z') { Fail "tag '$Tag' is not aiscan-v<N>" }
$all = Get-Content $Manifest -Raw | ConvertFrom-Json
$entries = $all.PSObject.Properties | Where-Object Name -ceq $Tag | ForEach-Object Value
if (-not $entries) { Fail "tag '$Tag' is not listed in $Manifest" }
$manual = @($entries | Where-Object { $_.source.kind -eq 'manual' })

foreach ($e in $manual) {
    $f = Join-Path $Dir $e.name
    if (-not (Test-Path $f)) { Fail "$f missing" }
    if ((Get-Item $f).Length -ne [long]$e.bytes) { Fail "$($e.name): size differs from the pin" }
    if ((Sha $f) -ne $e.sha256) { Fail "$($e.name): SHA-256 differs from the pin" }
}

$rel = DraftRelease
if (-not $rel.draft) { Fail "$Tag is already published; published releases are immutable, use a new tag" }

foreach ($e in $manual) {
    $have = @($rel.assets | Where-Object { $_.name -ceq $e.name }) | Select-Object -First 1
    if ($have) {
        if ([long]$have.size -ne [long]$e.bytes) { Fail "$($e.name) is already on the draft with a different size" }
        Write-Output "skip $($e.name): already uploaded"
        continue
    }
    & $Gh release upload $Tag (Join-Path $Dir $e.name) --repo $Repo
    if ($LASTEXITCODE -ne 0) { Fail "upload of $($e.name) failed" }
    Write-Output "uploaded $($e.name)"
}

$rel = DraftRelease
foreach ($e in $entries) {
    $a = @($rel.assets | Where-Object { $_.name -ceq $e.name }) | Select-Object -First 1
    if (-not $a) { Fail "$($e.name) is not on the draft" }
    if ([long]$a.size -ne [long]$e.bytes) { Fail "$($e.name) on the draft is $($a.size) bytes, pinned $($e.bytes)" }
    if ($a.digest -and $a.digest -ne "sha256:$($e.sha256)") { Fail "$($e.name) on the draft has digest $($a.digest)" }
}

& $Gh release edit $Tag --repo $Repo --draft=false --latest=false
if ($LASTEXITCODE -ne 0) { Fail 'publishing the release failed' }
Write-Output "published $Tag (not Latest)"
exit 0

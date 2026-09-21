<#
.SYNOPSIS
	Publishes the single-file executable, signs it, and produces the files to attach to a
	GitHub release.

.DESCRIPTION
	Order matters here and is not negotiable: publish, prune, sign, then package. Modifying a
	file after it has been signed breaks the signature, so the executable is signed only once
	it is byte-for-byte final, and the zip is built around it afterwards.

	Checksums are computed last, from the files that will actually be uploaded.

.PARAMETER SkipSigning
	Produce an unsigned build. Useful for testing the packaging, and the only option if you do
	not have an Artifact Signing account yet. The output is named so nobody mistakes it for a
	release build.

.EXAMPLE
	.\build\Publish-Release.ps1 -Version 2.0.0

.EXAMPLE
	.\build\Publish-Release.ps1 -Version 2.0.0 -SkipSigning
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9\.\-]+)?$')]
	[string] $Version,

	[string] $OutputDirectory,

	[switch] $SkipSigning,

	[string] $CorrelationId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Progress rendering on a 55 MB artifact costs far more than the work itself.
$ProgressPreference = 'SilentlyContinue'

# Resolved here rather than in the param block: $PSScriptRoot is not populated in parameter
# defaults under Windows PowerShell 5.1, which silently yields an empty path.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Resolve-Path (Join-Path $scriptRoot '..')
$project    = Join-Path $repoRoot 'src\IaaSBuilder.Web\IaaSBuilder.Web.csproj'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }

function Write-Step { param([string] $m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "    $m" -ForegroundColor Green }

if (-not (Test-Path $project)) { throw "Project not found: $project" }

$staging = Join-Path $OutputDirectory "publish-$Version"
$release = Join-Path $OutputDirectory "release-$Version"

foreach ($d in @($staging, $release)) {
	if (Test-Path $d) { Remove-Item $d -Recurse -Force }
	New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# ---------------------------------------------------------------------------

Write-Step "Publishing $Version"

& dotnet publish $project `
	-c Release `
	-o $staging `
	-p:Version=$Version `
	-p:InformationalVersion=$Version `
	--nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $staging 'IaaSBuilder.exe'
if (-not (Test-Path $exe)) { throw "Publish succeeded but $exe is missing." }
Write-Ok ("IaaSBuilder.exe  {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))

# ---------------------------------------------------------------------------
# Debug symbols and import libraries are build by-products. Shipping them adds tens of
# megabytes to a release for no benefit to anyone downloading it.
# ---------------------------------------------------------------------------

Write-Step 'Pruning build by-products'
$junk = Get-ChildItem $staging -Recurse -File -Include '*.pdb', '*.lib', '*.exp', '*.ilk'
if ($junk) {
	$freed = ($junk | Measure-Object Length -Sum).Sum / 1MB
	foreach ($j in $junk) { Write-Host "    - $($j.Name)" -ForegroundColor DarkGray }
	$junk | Remove-Item -Force
	Write-Ok ("Removed {0} file(s), {1:N1} MB" -f $junk.Count, $freed)
}
else {
	Write-Ok 'Nothing to prune.'
}

# ---------------------------------------------------------------------------
# Sign before packaging. Never after.
# ---------------------------------------------------------------------------

if ($SkipSigning) {
	Write-Step 'Skipping signing (-SkipSigning)'
	Write-Host '    This build is UNSIGNED. Windows SmartScreen will warn on it.' -ForegroundColor Yellow
	$packageName = "IaaSBuilder-$Version-win-x64-unsigned"
}
else {
	Write-Step 'Signing'
	$signArgs = @{ Path = $exe }
	if ($CorrelationId) { $signArgs['CorrelationId'] = $CorrelationId }
	& (Join-Path $scriptRoot 'Sign-File.ps1') @signArgs
	if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
	$packageName = "IaaSBuilder-$Version-win-x64"
}

# ---------------------------------------------------------------------------

Write-Step 'Packaging'

$zip = Join-Path $release "$packageName.zip"

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Copy-Item $exe (Join-Path $release 'IaaSBuilder.exe')

# Compress-Archive has been seen to return before it has finished, leaving a truncated
# archive behind. Open the result and confirm the executable is in it at full size.
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries | Where-Object { $_.Name -eq 'IaaSBuilder.exe' }
    if (-not $entry) { throw "The archive does not contain IaaSBuilder.exe." }
    if ($entry.Length -ne (Get-Item $exe).Length) {
        throw "IaaSBuilder.exe in the archive is $($entry.Length) bytes; expected $((Get-Item $exe).Length)."
    }
    Write-Ok ("{0} entries, IaaSBuilder.exe intact" -f $archive.Entries.Count)
}
finally {
    $archive.Dispose()
}

Write-Ok ("{0}  {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))

# ---------------------------------------------------------------------------
# Checksums, computed from the files that will actually be uploaded.
# ---------------------------------------------------------------------------

Write-Step 'Checksums'
$lines = foreach ($f in Get-ChildItem $release -File | Sort-Object Name) {
	$hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
	Write-Host "    $hash  $($f.Name)"
	"$hash  $($f.Name)"
}
$lines | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii

Write-Host ''
Write-Ok "Release files are in $release"
if ($SkipSigning) {
	Write-Host '    Remember: this build is unsigned.' -ForegroundColor Yellow
}

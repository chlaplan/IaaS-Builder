<#
.SYNOPSIS
	Authenticode-signs files with Azure Artifact Signing (formerly Trusted Signing).

.DESCRIPTION
	Acquires the signing dlib, writes the metadata file the dlib expects, runs SignTool, and
	then verifies the result.

	Two things about this service drive the whole design:

	1. Its certificates are renewed daily and are valid for 72 hours. A signature without an
	   RFC3161 timestamp is therefore a time bomb: it verifies on the build machine and starts
	   failing for users three days later. This script treats a missing timestamp as a hard
	   failure rather than a warning.

	2. There is no US Government region. The endpoints are all `*.codesigning.azure.net` in
	   commercial Azure, and `Microsoft.CodeSigning` is not even a valid resource namespace in
	   Gov ARM. Since this repository is otherwise used almost entirely against Azure US
	   Government, the script checks which cloud you are signed in to and says so plainly,
	   because the failure you would otherwise get is an unexplained authentication error.

.PARAMETER Path
	Files to sign. Sign the final artifact, last. Do not sign anything that will later be
	bundled, rewritten or compressed - modifying a file after signing breaks the signature.

.EXAMPLE
	.\build\Sign-File.ps1 -Path .\publish\IaaSBuilder.exe

.EXAMPLE
	.\build\Sign-File.ps1 -Path .\publish\IaaSBuilder.exe -WhatIfNoSigningAccount
	Runs every check and prepares every tool, but stops before the signing call. Use this to
	prove the machine is ready before you have an account, or to test the script itself.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory, ValueFromRemainingArguments)]
	[string[]] $Path,

	[string] $Endpoint,
	[string] $AccountName,
	[string] $CertificateProfileName,

	[string] $ConfigPath,

	# Pinned to the version Microsoft's own GitHub action pins. Bump deliberately, not by drift.
	[string] $ClientVersion = '1.0.128',

	[string] $TimestampUrl = 'http://timestamp.acs.microsoft.com',

	[string] $ToolsDirectory = (Join-Path $env:LOCALAPPDATA 'IaaSBuilder\signing-tools'),

	# An opaque string handed to the service so a signature can be traced back to a build.
	[string] $CorrelationId,

	[switch] $WhatIfNoSigningAccount,

	# For callers that do not authenticate through the Azure CLI at all.
	[switch] $SkipCloudCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolved here rather than in the param block: $PSScriptRoot is not populated in parameter
# defaults under Windows PowerShell 5.1, which silently yields an empty path.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ConfigPath) { $ConfigPath = Join-Path $scriptRoot 'signing.config.json' }

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Note { param([string] $Message) Write-Host "    $Message" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# Configuration: explicit parameters, then environment, then the config file.
# ---------------------------------------------------------------------------

$config = @{}
if (Test-Path $ConfigPath) {
	Write-Note "Configuration from $ConfigPath"
	$json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
	foreach ($p in $json.PSObject.Properties) {
		if (-not $p.Name.StartsWith('//')) { $config[$p.Name] = $p.Value }
	}
}

function Resolve-Setting {
	param([string] $Value, [string] $EnvName, [string] $ConfigKey, [string] $Label)

	if ($Value) { return $Value }
	$fromEnv = [Environment]::GetEnvironmentVariable($EnvName)
	if ($fromEnv) { return $fromEnv }
	if ($config.ContainsKey($ConfigKey) -and $config[$ConfigKey]) { return [string]$config[$ConfigKey] }

	throw @"
No $Label configured.

Set it one of three ways:
  1. Pass -$ConfigKey to this script.
  2. Set the environment variable $EnvName.
  3. Copy build\signing.config.sample.json to build\signing.config.json and fill it in.
	 That file is gitignored.
"@
}

$Endpoint               = Resolve-Setting $Endpoint               'ARTIFACT_SIGNING_ENDPOINT' 'endpoint'               'signing endpoint'
$AccountName            = Resolve-Setting $AccountName            'ARTIFACT_SIGNING_ACCOUNT'  'accountName'            'signing account name'
$CertificateProfileName = Resolve-Setting $CertificateProfileName 'ARTIFACT_SIGNING_PROFILE'  'certificateProfileName' 'certificate profile name'

if ($Endpoint -notmatch '^https://') {
	throw "The endpoint must be an absolute https URL. Got '$Endpoint'."
}

# The portal shows the account URI with a trailing slash. Pasting it verbatim is the obvious
# thing to do, so accept it rather than letting the dlib fail on a doubled separator.
$Endpoint = $Endpoint.TrimEnd('/')

# ---------------------------------------------------------------------------
# The files. Resolved and checked up front, because SignTool's own complaint
# about a missing file is easy to miss in its verbose output.
# ---------------------------------------------------------------------------

Write-Step 'Resolving files to sign'
$files = foreach ($p in $Path) {
	$resolved = Resolve-Path -LiteralPath $p -ErrorAction SilentlyContinue
	if (-not $resolved) { throw "File not found: $p" }
	foreach ($r in $resolved) { $r.ProviderPath }
}
$files = @($files)
if ($files.Count -eq 0) { throw 'No files matched.' }
foreach ($f in $files) { Write-Ok ("{0}  ({1:N1} MB)" -f $f, ((Get-Item $f).Length / 1MB)) }

# ---------------------------------------------------------------------------
# SignTool. The dlib does not work with the 20348 SDK, so pick the newest.
# ---------------------------------------------------------------------------

Write-Step 'Locating SignTool'
$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
	Where-Object { $_.FullName -match '\\x64\\' } |
	Sort-Object { [version]($_.FullName -replace '.*\\10\\bin\\([\d\.]+)\\.*', '$1') } -Descending |
	Select-Object -First 1

if (-not $signTool) {
	throw @'
SignTool (x64) not found under C:\Program Files (x86)\Windows Kits\10\bin.

Install the Windows SDK. Artifact Signing needs a recent one - the 20348 SDK is explicitly
not supported by the signing dlib.
'@
}
Write-Ok $signTool.FullName

# ---------------------------------------------------------------------------
# The signing dlib.
#
# Acquired through `dotnet restore` rather than a direct api.nuget.org download on purpose:
# a managed workstation may well have nuget.org disabled in favour of an internal feed proxy,
# in which case a hardcoded nuget.org URL fails at the TLS handshake with nothing useful to
# say. Going through dotnet uses whatever feed the machine is actually configured for.
# ---------------------------------------------------------------------------

Write-Step "Ensuring signing client $ClientVersion"
$packagesRoot = Join-Path $ToolsDirectory 'packages'
$dlib = Join-Path $packagesRoot "microsoft.artifactsigning.client\$ClientVersion\bin\x64\Azure.CodeSigning.Dlib.dll"

if (Test-Path $dlib) {
	Write-Ok "Cached: $dlib"
}
else {
	New-Item -ItemType Directory -Force -Path $ToolsDirectory | Out-Null
	$proj = Join-Path $ToolsDirectory 'acquire.csproj'

	@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<TargetFramework>net8.0</TargetFramework>
	<RestorePackagesPath>packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
	<PackageDownload Include="Microsoft.ArtifactSigning.Client" Version="[$ClientVersion]" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $proj -Encoding utf8

	Write-Note 'Restoring from the configured NuGet feed...'
	$restore = & dotnet restore $proj 2>&1
	if ($LASTEXITCODE -ne 0) {
		$restore | Write-Host
		throw @"
Could not acquire Microsoft.ArtifactSigning.Client $ClientVersion.

If this machine routes NuGet through an internal proxy feed, check the package is mirrored
there:  dotnet nuget list source
"@
	}

	if (-not (Test-Path $dlib)) { throw "Restore succeeded but the dlib is not at $dlib" }
	Write-Ok "Installed: $dlib"
}

# ---------------------------------------------------------------------------
# Which cloud are we signed in to?
#
# This repository is used almost entirely against Azure US Government, so the default `az`
# context here is very likely Gov - where this service does not exist at all. Catching that
# now turns a baffling auth failure into one sentence.
# ---------------------------------------------------------------------------

Write-Step 'Checking the Azure sign-in'

# DefaultAzureCredential tries the environment credential before the Azure CLI, so when
# AZURE_CLIENT_ID and AZURE_TENANT_ID are set the CLI's configured cloud has no bearing on
# anything. Checking it anyway would fail a perfectly good CI run over a leftover local
# setting.
$usingEnvironmentCredential = $env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID

if ($SkipCloudCheck) {
	Write-Note 'Cloud check skipped (-SkipCloudCheck).'
}
elseif ($usingEnvironmentCredential) {
	Write-Note "Using the environment credential (AZURE_CLIENT_ID is set); not checking the CLI cloud."
}
else {
	$az = Get-Command az -ErrorAction SilentlyContinue
	if (-not $az) {
		Write-Note 'Azure CLI not found. The dlib will fall back to another credential.'
	}
	else {
		$cloud = (& az cloud show --query name -o tsv 2>$null)
		if ($LASTEXITCODE -eq 0 -and $cloud) {
			if ($cloud -ne 'AzureCloud') {
				throw @"
The Azure CLI is signed in to '$cloud'.

Azure Artifact Signing exists only in commercial Azure. There are no US Government regions:
every endpoint is *.codesigning.azure.net, and 'Microsoft.CodeSigning' is not a valid resource
namespace in Gov ARM at all.

Switch, sign, then switch back:

	az cloud set --name AzureCloud
	az login
	.\build\Sign-File.ps1 -Path <file>
	az cloud set --name AzureUSGovernment
	az login

If you authenticate some other way, pass -SkipCloudCheck.
"@
			}
			$who = (& az account show --query "user.name" -o tsv 2>$null)
			Write-Ok "AzureCloud as $who"
		}
		else {
			Write-Note 'Azure CLI present but not signed in. Run: az login'
		}
	}
}

# ---------------------------------------------------------------------------
# Metadata file. Written to a temp path, never into the repository.
#
# ExcludeCredentials narrows DefaultAzureCredential to the two that make sense here -
# environment variables (CI) and the Azure CLI (a developer machine). Leaving the rest
# enabled is how you get an interactive browser prompt in the middle of a build, or a stale
# cached token from a completely different tenant.
# ---------------------------------------------------------------------------

Write-Step 'Writing signing metadata'
$metadata = [ordered]@{
	Endpoint               = $Endpoint
	CodeSigningAccountName = $AccountName
	CertificateProfileName = $CertificateProfileName
	ExcludeCredentials     = @(
		'ManagedIdentityCredential'
		'WorkloadIdentityCredential'
		'SharedTokenCacheCredential'
		'VisualStudioCredential'
		'VisualStudioCodeCredential'
		'AzurePowerShellCredential'
		'AzureDeveloperCliCredential'
		'InteractiveBrowserCredential'
	)
}
if ($CorrelationId) { $metadata['CorrelationId'] = $CorrelationId }

$metadataPath = Join-Path ([System.IO.Path]::GetTempPath()) ("artifact-signing-{0}.json" -f [guid]::NewGuid())
$metadata | ConvertTo-Json -Depth 4 | Set-Content -Path $metadataPath -Encoding utf8
Write-Ok "$AccountName / $CertificateProfileName  at  $Endpoint"

if ($WhatIfNoSigningAccount) {
	Write-Step 'Stopping before the signing call (-WhatIfNoSigningAccount)'
	Write-Host ''
	Write-Host 'Everything needed to sign is present. The command that would run:' -ForegroundColor Yellow
	Write-Host ''
	Write-Host "  `"$($signTool.FullName)`" sign /v /debug /fd SHA256 ``"
	Write-Host "    /tr `"$TimestampUrl`" /td SHA256 ``"
	Write-Host "    /dlib `"$dlib`" ``"
	Write-Host "    /dmdf `"$metadataPath`" ``"
	foreach ($f in $files) { Write-Host "    `"$f`"" }
	Write-Host ''
	Remove-Item $metadataPath -Force -ErrorAction SilentlyContinue
	return
}

# ---------------------------------------------------------------------------
# Sign.
# ---------------------------------------------------------------------------

try {
	Write-Step 'Signing'
	$signArgs = @(
		'sign'
		'/v'
		'/debug'
		'/fd', 'SHA256'
		'/tr', $TimestampUrl
		'/td', 'SHA256'
		'/dlib', $dlib
		'/dmdf', $metadataPath
	) + $files

	& $signTool.FullName @signArgs
	$signExit = $LASTEXITCODE

	if ($signExit -ne 0) {
		throw @"
SignTool failed with exit code $signExit.

Common causes, in the order they actually happen:
  - 403 / SignerSign() failure: the endpoint region does not match the region the account and
	certificate profile live in. They must be the same region.
  - Authentication failure: run 'az login' against commercial Azure.
  - Authorization failure: Owner and Contributor do NOT grant signing. The signed-in identity
	needs the 'Artifact Signing Certificate Profile Signer' role on the account or profile:

	  az role assignment create --assignee <upn-or-object-id> ``
		--role "Artifact Signing Certificate Profile Signer" ``
		--scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CodeSigning/codeSigningAccounts/$AccountName"

  - Identity validation expired: certificate renewal stops and so does signing.
"@
	}
}
finally {
	Remove-Item $metadataPath -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Verify. Not a formality.
#
# The timestamp check is the important one. Artifact Signing certificates live 72 hours, so an
# untimestamped signature verifies perfectly on the build machine and then starts failing for
# everyone who downloads it three days later - by which point the release is out and the cause
# is not obvious. Treat it as a build break.
# ---------------------------------------------------------------------------

Write-Step 'Verifying'
$failed = @()

foreach ($f in $files) {
	& $signTool.FullName verify /pa /v $f | Out-Null
	$verifyExit = $LASTEXITCODE

	$sig = Get-AuthenticodeSignature -LiteralPath $f

	$status      = $sig.Status
	$subject     = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(none)' }
	$timestamped = $null -ne $sig.TimeStamperCertificate

	Write-Host "    $([System.IO.Path]::GetFileName($f))"
	Write-Host "      status      : $status"
	Write-Host "      signer      : $subject"
	Write-Host "      timestamped : $timestamped"

	if ($verifyExit -ne 0 -or $status -ne 'Valid') {
		$failed += "$f - signtool verify exit $verifyExit, status $status"
	}
	elseif (-not $timestamped) {
		$failed += "$f - SIGNED BUT NOT TIMESTAMPED. This signature stops validating in 72 hours."
	}
	else {
		Write-Ok 'Valid and timestamped.'
	}
}

if ($failed.Count -gt 0) {
	Write-Host ''
	foreach ($f in $failed) { Write-Host "    $f" -ForegroundColor Red }
	throw 'Verification failed. Do not publish these files.'
}

Write-Host ''
Write-Ok "Signed and verified $($files.Count) file(s)."

[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string] $Tag,
    [Parameter(Mandatory = $true, ParameterSetName = 'Release')]
    [string] $DownloadDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Local')]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Local')]
    [ValidatePattern('\A[0-9a-f]{40}\z')]
    [string] $SourceCommit,
    [Parameter(Mandatory = $true, ParameterSetName = 'Local')]
    [string] $NativeProvenanceFile,
    [Parameter(ParameterSetName = 'Local')]
    [string] $ChecksumFile,
    [Parameter(ParameterSetName = 'Local')]
    [string] $PublishedPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repository = 'sqk0232/RKNNSharp'
$version = $Tag.Substring(1)
$packageNames = @("RKNNSharp.$version.nupkg", "RKNNSharp.$version.snupkg")

function Get-GitHubJson([string] $Endpoint) {
    $json = & gh api --hostname github.com -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28' $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "GitHub read failed: $Endpoint" }
    return ($json | ConvertFrom-Json)
}

function Get-EntryHash([IO.Compression.ZipArchiveEntry] $Entry) {
    $stream = $Entry.Open()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

if ($PSCmdlet.ParameterSetName -eq 'Release') {
    # Local mode below is read-only and never contacts GitHub or NuGet.
    if ($env:GITHUB_ACTIONS -eq 'true') {
        if ($env:GITHUB_REPOSITORY -cne $repository) { throw 'Publishing is restricted to sqk0232/RKNNSharp.' }
        if ($env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
            if ($env:GITHUB_REF -cne 'refs/heads/main') { throw 'Manual publishing must run from main.' }
        } elseif ($env:GITHUB_EVENT_NAME -ne 'release') {
            throw 'Only a published release or manual dispatch can publish.'
        }
    }
    $release = Get-GitHubJson "repos/$repository/releases/tags/$Tag"
    if ($release.draft -or $release.prerelease -or !$release.published_at -or $release.tag_name -cne $Tag) {
        throw 'A published, stable GitHub Release with the exact tag is required.'
    }
    $SourceCommit = (Get-GitHubJson "repos/$repository/commits/$Tag").sha
    if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'The release tag did not resolve to a full commit.' }
    $provenanceFile = Get-GitHubJson "repos/$repository/contents/native/prebuilt/linux-arm64/provenance.json?ref=$SourceCommit"
    if ($provenanceFile.encoding -cne 'base64') { throw 'Expected source native provenance as base64 file contents.' }
    $provenanceJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($provenanceFile.content))
    if ($release.target_commitish -match '\A[0-9a-fA-F]{40}\z' -and $release.target_commitish -cne $SourceCommit) {
        throw 'Release target commit differs from the resolved tag.'
    }
    if ($env:GITHUB_ACTIONS -eq 'true' -and $env:GITHUB_EVENT_NAME -eq 'release') {
        $event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($event.action -cne 'published' -or $event.release.id -ne $release.id -or
            $env:GITHUB_REF -cne "refs/tags/$Tag" -or $env:GITHUB_SHA -cne $SourceCommit) {
            throw 'Release event, current release, tag and source commit must agree.'
        }
    }
    $runs = Get-GitHubJson "repos/$repository/actions/workflows/managed.yml/runs?head_sha=$SourceCommit&event=push&branch=main&per_page=100"
    $run = @($runs.workflow_runs | Where-Object {
        $_.head_sha -ceq $SourceCommit -and $_.head_repository.full_name -ceq $repository -and
        $_.path -ceq '.github/workflows/managed.yml' -and $_.event -ceq 'push' -and
        $_.head_branch -ceq 'main' -and $_.status -eq 'completed' -and $_.conclusion -eq 'success'
    } | Sort-Object id -Descending | Select-Object -First 1)
    if ($run.Count -ne 1) { throw 'No successful managed.yml main push CI for the exact source commit. Retry only after CI completes.' }
    $jobs = Get-GitHubJson "repos/$repository/actions/runs/$($run[0].id)/jobs?per_page=100"
    foreach ($os in @('ubuntu-latest', 'windows-latest')) {
        $job = @($jobs.jobs | Where-Object { $_.name -ceq "net10.0 $os" -and $_.status -eq 'completed' -and $_.conclusion -eq 'success' })
        if ($job.Count -ne 1) { throw "Exact source CI has no successful $os job." }
        $requiredSteps = @('Build', 'Check structured logging', 'Test', 'Validate default package')
        if ($os -eq 'ubuntu-latest') { $requiredSteps += 'Check shell logging guard' }
        foreach ($step in $requiredSteps) {
            if (@($job[0].steps | Where-Object { $_.name -ceq $step -and $_.status -eq 'completed' -and $_.conclusion -eq 'success' }).Count -ne 1) {
                throw "Exact source CI is missing a successful step: $os / $step"
            }
        }
    }
    $PackageDirectory = [IO.Path]::GetFullPath($DownloadDirectory)
    if (Test-Path -LiteralPath $PackageDirectory) { throw 'Download directory must be new; refusing to reuse or overwrite files.' }
    New-Item -ItemType Directory -Path $PackageDirectory | Out-Null
    foreach ($name in ($packageNames + 'SHA256SUMS.txt')) {
        $asset = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or $asset[0].size -le 0 -or
            $asset[0].size -gt 50MB -or $asset[0].digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') {
            throw "Missing, ambiguous or invalid release asset: $name"
        }
        # gh downloads only these named assets from this fixed repository/release.
        & gh release download $Tag --repo $repository --pattern $name --dir $PackageDirectory
        if ($LASTEXITCODE -ne 0) { throw "Release asset download failed: $name" }
        $path = Join-Path $PackageDirectory $name
        if ((Get-Item -LiteralPath $path).Length -ne $asset[0].size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset[0].digest.Substring(7)) {
            throw "Release asset size/digest mismatch: $name"
        }
    }
    # Fetch provenance from the tag's immutable commit, never from a newer main.
    $NativeProvenanceFile = Join-Path $PackageDirectory 'source-native-provenance.json'
    $provenanceJson | Set-Content -LiteralPath $NativeProvenanceFile -Encoding UTF8
    $ChecksumFile = Join-Path $PackageDirectory 'SHA256SUMS.txt'
    Write-Output "Verified stable Release $($release.id), tag $Tag, source $SourceCommit, managed CI $($run[0].id)."
} elseif (!$ChecksumFile) {
    $ChecksumFile = Join-Path $PackageDirectory 'SHA256SUMS.txt'
}

$nativeExpectation = Get-Content -LiteralPath $NativeProvenanceFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($nativeExpectation.asset -cne 'librknnsharp_native.so' -or
    $nativeExpectation.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
    $nativeExpectation.bytes -le 0 -or $nativeExpectation.bytes -gt 50MB) {
    throw 'Invalid native provenance: an exact asset, SHA256 and byte count are required.'
}

$checksums = @{}
foreach ($line in (Get-Content -LiteralPath $ChecksumFile -Encoding UTF8)) {
    if ($line -cnotmatch '^([0-9a-fA-F]{64})  (RKNNSharp\.[0-9]+\.[0-9]+\.[0-9]+\.(?:nupkg|snupkg))$') {
        throw 'Checksum manifest must contain only SHA256 and a bare, expected package filename.'
    }
    $hashValue = $Matches[1].ToLowerInvariant()
    $name = $Matches[2]
    if ($packageNames -cnotcontains $name -or $checksums.ContainsKey($name)) { throw 'Unexpected or duplicate checksum filename.' }
    $checksums[$name] = $hashValue
}
if ($checksums.Count -ne 2) { throw 'Both package and symbol package checksums are required.' }

foreach ($name in $packageNames) {
    $path = Join-Path $PackageDirectory $name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $checksums[$name]) {
        throw "Package SHA256 mismatch: $name"
    }
    $symbols = $name.EndsWith('.snupkg', [StringComparison]::Ordinal)
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        if ($zip.Entries.Count -gt 64 -or ($zip.Entries | Measure-Object -Property Length -Sum).Sum -gt 50MB) {
            throw 'Package entry count or uncompressed size exceeds the expected library payload.'
        }
        $seen = @{}
        foreach ($entry in $zip.Entries) {
            # Do not extract archives; reject traversal, duplicates and unrelated/private payloads.
            $entryName = $entry.FullName
            $common = $entryName -cmatch '\A(?:_rels/\.rels|\[Content_Types\]\.xml|RKNNSharp\.nuspec|package/services/metadata/core-properties/[A-Za-z0-9]+\.psmdcp)\z'
            $payload = if ($symbols) {
                $entryName -cmatch '\Alib/net[0-9]+\.[0-9]+/RKNNSharp\.pdb\z'
            } else {
                $entryName -cmatch '\A(?:LICENSE|NUGET_README\.md|lib/net[0-9]+\.[0-9]+/RKNNSharp\.(?:dll|xml)|runtimes/linux-arm64/native/librknnsharp_native\.so)\z'
            }
            if ((!$common -and !$payload) -or $seen.ContainsKey($entryName) -or $entry.Length -gt 50MB) {
                throw "Unexpected, duplicate or oversized package entry: $entryName"
            }
            $seen[$entryName] = $true
        }
        $nuspec = $zip.GetEntry('RKNNSharp.nuspec')
        if (!$nuspec) { throw 'RKNNSharp.nuspec is required.' }
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $nuspec.Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.XmlResolver = $null
            $xml.Load($reader)
        } finally { $reader.Dispose(); $stream.Dispose() }
        $metadata = $xml.package.metadata
        if ($metadata.id -cne 'RKNNSharp' -or $metadata.version -cne $version -or
            $metadata.repository.type -cne 'git' -or $metadata.repository.url -cne "https://github.com/$repository" -or
            $metadata.repository.commit -cne $SourceCommit) {
            throw 'Package id, version, repository and full source commit must match the release.'
        }
        $payloadPattern = if ($symbols) { '\Alib/net[0-9]+\.[0-9]+/RKNNSharp\.pdb\z' } else { '\Alib/net[0-9]+\.[0-9]+/RKNNSharp\.dll\z' }
        if (@($zip.Entries | Where-Object { $_.FullName -cmatch $payloadPattern -and $_.Length -gt 0 }).Count -lt 1) {
            throw 'The package has no managed payload.'
        }
        if (!$symbols -and ($metadata.readme -cne 'NUGET_README.md' -or
            !$seen.ContainsKey('NUGET_README.md') -or !$seen.ContainsKey('runtimes/linux-arm64/native/librknnsharp_native.so') -or
            $zip.GetEntry('NUGET_README.md').Length -eq 0 -or $zip.GetEntry('runtimes/linux-arm64/native/librknnsharp_native.so').Length -eq 0)) {
            throw 'The consumer README and Linux ARM64 native bridge are required.'
        }
        if (!$symbols) {
            $native = $zip.GetEntry('runtimes/linux-arm64/native/librknnsharp_native.so')
            if ($native.Length -ne $nativeExpectation.bytes -or (Get-EntryHash $native) -cne $nativeExpectation.sha256) {
                throw 'Packaged native bridge does not match the source commit provenance.'
            }
        }
        Write-Output "Verified $name SHA256=$($checksums[$name]), source=$SourceCommit, entries=$($zip.Entries.Count)."
    } finally { $zip.Dispose() }
}

if ($PublishedPackage) {
    $approved = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory $packageNames[0]))
    try {
        $published = [IO.Compression.ZipFile]::OpenRead($PublishedPackage)
        try {
            $seen = @{}
            foreach ($entry in $published.Entries) {
                if ($seen.ContainsKey($entry.FullName)) { throw 'Public package has duplicate entries.' }
                $seen[$entry.FullName] = $true
                if ($entry.FullName -ceq '.signature.p7s') {
                    if ($entry.Length -le 0 -or $entry.Length -gt 1MB) { throw 'Public package signature has an unexpected size.' }
                    continue
                }
                $expected = $approved.GetEntry($entry.FullName)
                if (!$expected -or $entry.Length -ne $expected.Length -or (Get-EntryHash $entry) -cne (Get-EntryHash $expected)) {
                    throw "Public package content differs from approved Release: $($entry.FullName)"
                }
            }
            foreach ($entry in $approved.Entries) {
                if (!$seen.ContainsKey($entry.FullName)) { throw "Public package is missing approved entry: $($entry.FullName)" }
            }
            Write-Output 'Public package entries match the approved Release, including consumer README; only an added repository signature is allowed.'
        } finally { $published.Dispose() }
    } finally { $approved.Dispose() }
}

if ($PSCmdlet.ParameterSetName -eq 'Release' -and $env:GITHUB_OUTPUT) {
    @("version=$version", "source_commit=$SourceCommit", "package_directory=$PackageDirectory") |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding UTF8
}

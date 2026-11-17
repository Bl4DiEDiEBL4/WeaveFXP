[CmdletBinding()]
param(
    [Parameter(Position=0, ValueFromRemainingArguments=$true)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'linux-arm64')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$releaseRoot = Join-Path $root 'Release'
$project = Join-Path $root 'WeaveFxp.Web\WeaveFxp.Web.csproj'
$work = $null

function Assert-OutputPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path escapes Release: $full"
    }
    $current = $full
    while ($current -and $current.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a linked output path: $current"
        }
        $current = Split-Path -Parent $current
    }
}

function Assert-TargetStopped([string]$Target) {
    foreach ($process in @(Get-Process -Name WeaveFXP -ErrorAction SilentlyContinue)) {
        try { $path = $process.Path } catch { $path = $null }
        if (!$path) { throw "Cannot verify the path of running WeaveFXP process $($process.Id)." }
        if ($path.Equals($Target, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The target is running: $Target (PID $($process.Id)). Stop that instance before publishing. Other previews can stay open."
        }
    }
}

function Install-File([string]$Source, [string]$Destination) {
    Assert-OutputPath $Destination
    $incoming = $Destination + '.incoming-' + [guid]::NewGuid().ToString('N')
    try {
        Copy-Item -LiteralPath $Source -Destination $incoming
        if (Test-Path -LiteralPath $Destination) {
            [IO.File]::Replace($incoming, $Destination, [System.Management.Automation.Language.NullString]::Value)
        } else {
            [IO.File]::Move($incoming, $Destination)
        }
    } finally {
        if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Force }
    }
}

function Remove-Generated([string]$Path) {
    Assert-OutputPath $Path
    if (!(Test-Path -LiteralPath $Path)) { return }
    $items = @(Get-Item -LiteralPath $Path -Force)
    if ($items[0].PSIsContainer) { $items += @(Get-ChildItem -LiteralPath $Path -Recurse -Force) }
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Refusing to clean linked generated files: $Path"
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

try {
    if (!(Test-Path -LiteralPath $project)) {
        throw 'The project is missing.'
    }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $rids = @($Runtime | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
    if (!$rids.Count) { throw 'Specify at least one supported runtime.' }
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'The project version is missing.' }
    foreach ($rid in $rids) {
        $name = if ($rid -eq 'win-x64') { 'WeaveFXP.exe' } else { 'WeaveFXP' }
        $target = Join-Path (Join-Path $releaseRoot $rid) $name
        Assert-OutputPath $target
        Assert-TargetStopped $target
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $work = Join-Path $tempRoot ('WeaveFXP-publish-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    Write-Host '=== Restoring ==='
    & $dotnet restore $project --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw "Restore failed (exit $LASTEXITCODE). Existing releases and data were not changed." }
    $built = @()
    foreach ($rid in $rids) {
        Write-Host "=== Publishing $rid ==="
        $stage = Join-Path $work $rid
        $baseOutput = (Join-Path (Join-Path $work 'bin') $rid).Replace('\', '/') + '/'
        $publishArgs = @('publish', $project, '-c', 'Release', '-r', $rid, '--no-restore',
            '--self-contained', 'true', '-m:1', '-nr:false', "-p:BaseOutputPath=$baseOutput",
            '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugType=none', '-o', $stage)
        & $dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Publish for $rid failed (exit $LASTEXITCODE). Existing releases and data were not changed." }
        $name = if ($rid -eq 'win-x64') { 'WeaveFXP.exe' } else { 'WeaveFXP' }
        $binary = Join-Path $stage $name
        if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Published executable is missing: $binary" }
        $zipName = "WeaveFXP-v$version-$rid.zip"
        $zip = Join-Path $work $zipName
        Compress-Archive -LiteralPath $binary -DestinationPath $zip -Force
        $built += [pscustomobject]@{ Rid=$rid; Name=$name; Binary=$binary; Zip=$zip; ZipName=$zipName }
    }
    # All requested builds and packages succeed before changing a current release.
    foreach ($build in $built) {
        $destination = Join-Path $releaseRoot $build.Rid
        $target = Join-Path $destination $build.Name
        Assert-OutputPath $target
        Assert-TargetStopped $target
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Install-File $build.Binary $target
        foreach ($pattern in @('*.pdb', 'appsettings*.json', '*.staticwebassets*.json', 'web.config', 'wwwroot', 'bin', 'THIRD-PARTY-NOTICES.txt')) {
            foreach ($item in @(Get-ChildItem -LiteralPath $destination -Filter $pattern -Force)) {
                Remove-Generated $item.FullName
            }
        }
        $data = Join-Path $destination 'data'
        $legacy = Join-Path $releaseRoot 'data'
        if (!(Test-Path -LiteralPath $data) -and (Test-Path -LiteralPath $legacy)) {
            Assert-OutputPath $data
            Copy-Item -LiteralPath $legacy -Destination $data -Recurse
        }
        $zips = Join-Path $releaseRoot 'zips'
        Assert-OutputPath (Join-Path $zips $build.ZipName)
        New-Item -ItemType Directory -Path $zips -Force | Out-Null
        Install-File $build.Zip (Join-Path $zips $build.ZipName)
        Write-Host "Ready: $target"
    }
    Write-Host '=== Done ==='
    Write-Host "Release packages: $(Join-Path $releaseRoot 'zips')"
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    exit 1
} finally {
    if ($work -and (Test-Path -LiteralPath $work)) {
        $resolved = [IO.Path]::GetFullPath($work)
        if ((Split-Path -Parent $resolved) -ne $tempRoot -or
            !(Split-Path -Leaf $resolved).StartsWith('WeaveFXP-publish-', [StringComparison]::Ordinal)) {
            throw "Unsafe temporary cleanup path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

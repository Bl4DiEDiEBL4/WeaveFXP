param(
    [string]$Bash = 'C:\Program Files\Git\bin\bash.exe',
    [string]$Python = 'python3'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('weave-linux-publish-' + [guid]::NewGuid().ToString('N'))
$passed = 0
function Unix([string]$Path) { '/' + $Path.Substring(0,1).ToLowerInvariant() + $Path.Substring(2).Replace('\','/') }
function Check([bool]$Ok, [string]$Message) {
    if (!$Ok) { throw $Message }; $script:passed++; Write-Host "PASS: $Message"
}
function Snapshot {
    (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'Release') -Recurse -File | Sort-Object FullName |
        ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash })) -join '|'
}
function Publish([string[]]$Runtimes) {
    $scriptPath = Unix (Join-Path $fixture 'publish.sh')
    & $Bash $scriptPath @Runtimes | Out-Host
    return $LASTEXITCODE
}
try {
    foreach ($dir in @('tools','WeaveFxp.Web','Release/linux-x64/data','Release/linux-arm64/data','Release/win-x64/data')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $dir) -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $repo 'publish.sh') -Destination $fixture
    [IO.File]::WriteAllText((Join-Path $fixture 'WeaveFxp.Web/WeaveFxp.Web.csproj'), '<Project><PropertyGroup><Version>9.8.7</Version></PropertyGroup></Project>')
    foreach ($rid in @('linux-x64','linux-arm64','win-x64')) {
        [IO.File]::WriteAllText((Join-Path $fixture "Release/$rid/data/keep.json"), 'preserve me')
    }
    $stub = @'
#!/usr/bin/env bash
set -eu
if [ "$1" = restore ]; then [ "${WEAVE_LINUX_PUBLISH_FAIL:-}" != restore ]; exit; fi
rid=; stage=
while [ $# -gt 0 ]; do
    case "$1" in -r) rid="$2"; shift ;; -o) stage="$2"; shift ;; esac
    shift
done
[ "${WEAVE_LINUX_PUBLISH_FAIL:-}" != "$rid" ] || exit 31
mkdir -p "$stage"
name=WeaveFXP
[ "$rid" != win-x64 ] || name=WeaveFXP.exe
echo "new $rid" > "$stage/$name"
'@
    [IO.File]::WriteAllText((Join-Path $fixture 'tools/dotnet'), $stub.Replace("`r`n","`n") + "`n")
    if ($Python -ne 'python3') {
        $wrapper = "#!/usr/bin/env bash`nexec '" + (Unix $Python) + "' `"`$@`"`n"
        [IO.File]::WriteAllText((Join-Path $fixture 'tools/python3'), $wrapper)
    }
    $oldPath = $env:PATH
    $env:PATH = (Join-Path $fixture 'tools') + ';' + $oldPath
    try {
        $env:WEAVE_LINUX_PUBLISH_FAIL = ''
        Check ((Publish @('linux-x64','linux-arm64','win-x64')) -eq 0) 'Linux script publishes all requested runtimes'
        foreach ($rid in @('linux-x64','linux-arm64','win-x64')) {
            Check ((Get-Content -LiteralPath (Join-Path $fixture "Release/$rid/data/keep.json") -Raw) -eq 'preserve me') "$rid data stays in place"
            $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $fixture "Release/zips/WeaveFXP-v9.8.7-$rid.zip"))
            try { Check ($zip.Entries.Count -eq 1 -and $zip.Entries.FullName -match '^WeaveFXP(?:\.exe)?$') "$rid archive contains only its binary" }
            finally { $zip.Dispose() }
        }
        foreach ($mode in @('restore','linux-arm64')) {
            $before = Snapshot
            $env:WEAVE_LINUX_PUBLISH_FAIL = $mode
            Check ((Publish @('linux-x64','linux-arm64')) -ne 0 -and (Snapshot) -eq $before) "$mode failure keeps existing builds, packages and data"
        }
        $env:WEAVE_LINUX_PUBLISH_FAIL = ''
        $before = Snapshot
        Check ((Publish @('../escape')) -ne 0 -and (Snapshot) -eq $before) 'invalid runtime is rejected before any mutation'
    } finally { $env:PATH = $oldPath; Remove-Item Env:WEAVE_LINUX_PUBLISH_FAIL -ErrorAction SilentlyContinue }
    Write-Host "$passed Linux publication checks passed."
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path -Parent $full) -ne $parent -or !(Split-Path -Leaf $full).StartsWith('weave-linux-publish-')) { throw 'Unsafe fixture cleanup' }
    if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked fixture cleanup' }
    Remove-Item -LiteralPath $full -Recurse -Force
}

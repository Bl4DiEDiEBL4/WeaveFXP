param([string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('weave-publish-tests-' + [guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$passed = 0
$active = $null
function Check([bool]$Ok, [string]$Message) {
    if (!$Ok) { throw $Message }
    $script:passed++
    Write-Host "PASS: $Message"
}
function Fixture([string]$Name) {
    $folder = Join-Path $fixtureRoot ($Name + " with spaces and 'quotes'")
    foreach ($child in @('WeaveFxp.Web', 'tools', 'Release\win-x64\data', 'Release\linux-x64\data', 'Release\data')) {
        New-Item -ItemType Directory -Path (Join-Path $folder $child) -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $Repository 'publish.ps1') -Destination $folder
    Copy-Item -LiteralPath (Join-Path $Repository 'publish.bat') -Destination $folder
    [IO.File]::WriteAllText((Join-Path $folder 'WeaveFxp.Web\WeaveFxp.Web.csproj'), '<Project><PropertyGroup><Version>9.8.7</Version></PropertyGroup></Project>')
    foreach ($file in @('Release\notes.txt','Release\win-x64\WeaveFXP.exe',
        'Release\linux-x64\WeaveFXP','Release\win-x64\data\keep.bin','Release\linux-x64\data\keep.bin','Release\data\seed.bin')) {
        [IO.File]::WriteAllText((Join-Path $folder $file), 'original')
    }
    @'
$toolArgs = @($args)
if ($env:WEAVE_PUBLISH_TEST_FAIL -eq 'restore' -and $toolArgs[0] -eq 'restore') { $global:LASTEXITCODE=23; return }
if ($toolArgs[0] -eq 'publish') {
    $rid=$toolArgs[[Array]::IndexOf($toolArgs,'-r')+1]
    if ($env:WEAVE_PUBLISH_TEST_FAIL -eq $rid) { $global:LASTEXITCODE=31; return }
    $stage=$toolArgs[[Array]::IndexOf($toolArgs,'-o')+1]
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $name=if ($rid -eq 'win-x64') { 'WeaveFXP.exe' } else { 'WeaveFXP' }
    [IO.File]::WriteAllText((Join-Path $stage $name), 'new '+$rid)
}
$global:LASTEXITCODE=0
'@ | Set-Content -LiteralPath (Join-Path $folder 'tools\dotnet.ps1') -Encoding utf8
    return $folder
}
function Invoke-Publisher([string]$Folder, [string[]]$Rids) {
    $env:PATH=(Join-Path $Folder 'tools')+';'+$originalPath
    $oldAction=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $output=(& (Join-Path $Folder 'publish.bat') @Rids 2>&1 | Out-String)
        $code=$LASTEXITCODE
    } finally { $ErrorActionPreference=$oldAction }
    [pscustomobject]@{Code=$code;Output=$output}
}
function Snapshot([string]$Folder) {
    @((Get-ChildItem -LiteralPath (Join-Path $Folder 'Release') -Recurse -File | Sort-Object FullName |
        ForEach-Object { $_.FullName.Substring($Folder.Length)+':'+(Get-FileHash -LiteralPath $_.FullName).Hash })) -join '|'
}
try {
    $success=Fixture 'success'
    $env:WEAVE_PUBLISH_TEST_FAIL=''
    $result=Invoke-Publisher $success @('win-x64','linux-x64','linux-arm64')
    if ($result.Code -ne 0) { throw $result.Output }
    Check ($result.Code -eq 0) 'batch accepts multiple runtimes and an unrelated running preview'
    foreach ($rid in @('win-x64','linux-x64','linux-arm64')) {
        $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $success "Release\zips\WeaveFXP-v9.8.7-$rid.zip"))
        try {
            $names=@($archive.Entries | Select-Object -ExpandProperty FullName)
            $binary=if($rid -eq 'win-x64'){'WeaveFXP.exe'}else{'WeaveFXP'}
            Check ($names.Count -eq 1 -and $names -contains $binary) "$rid archive contains only its binary"
        } finally { $archive.Dispose() }
    }
    Check ((Get-Content -LiteralPath (Join-Path $success 'Release\win-x64\data\keep.bin') -Raw) -eq 'original') 'existing Windows data stays in place'
    Check ((Get-Content -LiteralPath (Join-Path $success 'Release\linux-x64\data\keep.bin') -Raw) -eq 'original') 'existing Linux data stays in place'
    Check (Test-Path -LiteralPath (Join-Path $success 'Release\linux-arm64\data\seed.bin')) 'legacy data seeds only a runtime with no data directory'
    Check ((Get-Content -LiteralPath (Join-Path $success 'Release\notes.txt') -Raw) -eq 'original') 'unrelated Release files remain untouched'
    foreach ($mode in @('restore','linux-x64')) {
        $folder=Fixture ('failure-'+$mode)
        $before=Snapshot $folder
        $env:WEAVE_PUBLISH_TEST_FAIL=$mode
        $result=Invoke-Publisher $folder @('win-x64','linux-x64')
        Check ($result.Code -ne 0 -and (Snapshot $folder) -eq $before) "$mode failure preserves all existing binaries and data"
    }
    $env:WEAVE_PUBLISH_TEST_FAIL=''
    $invalid=Fixture 'invalid'
    $before=Snapshot $invalid
    $result=Invoke-Publisher $invalid @('../escape')
    Check ($result.Code -ne 0 -and (Snapshot $invalid) -eq $before) 'invalid runtime cannot escape Release or change existing files'
    $locked=Fixture 'locked'
    $before=Snapshot $locked
    $lock=[IO.File]::Open((Join-Path $locked 'Release\win-x64\WeaveFXP.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { $result=Invoke-Publisher $locked @('win-x64') } finally { $lock.Dispose() }
    Check ($result.Code -ne 0 -and (Snapshot $locked) -eq $before) 'locked target fails without truncating its binary or moving data'
    $running=Fixture 'running'
    $exe=Join-Path $running 'Release\win-x64\WeaveFXP.exe'
    Copy-Item -LiteralPath (Join-Path $Repository 'Release\preview\WeaveFXP.exe') -Destination $exe -Force
    $data=Join-Path $running 'Release\win-x64\data'
    @{enable_https_json_api=$false;enable_udp_api=$false;tray_icon_enabled=$false;auto_open_web_ui=$false;
        check_for_updates=$false;web_bind_address='127.0.0.1';web_port=18796} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
    '{}' | Set-Content -LiteralPath (Join-Path $data 'sites.json')
    $env:WEAVEFXP_STATE=Join-Path $data 'state.json'
    $env:WEAVEFXP_URL='http://127.0.0.1:18796'
    $active=Start-Process -FilePath $exe -ArgumentList '--no-browser' -WindowStyle Hidden -PassThru
    try {
        $result=Invoke-Publisher $running @('win-x64')
        if ($result.Output -notmatch 'The target is running\s*:') { throw $result.Output }
        Check ($result.Code -ne 0) 'only the exact running destination blocks publishing'
        Check (!$active.HasExited) 'publisher never stops the running destination'
    } finally { Stop-Process -Id $active.Id -ErrorAction SilentlyContinue; $active=$null }
    Write-Host "$passed publish regression checks passed."
} finally {
    $env:PATH=$originalPath
    $env:WEAVE_PUBLISH_TEST_FAIL=''
    if ($active -and !$active.HasExited) { Stop-Process -Id $active.Id }
    if (Test-Path -LiteralPath $fixtureRoot) {
        $full=[IO.Path]::GetFullPath($fixtureRoot)
        $expected=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ((Split-Path -Parent $full) -ne $expected -or !(Split-Path -Leaf $full).StartsWith('weave-publish-tests-')) { throw 'Unsafe test cleanup path' }
        if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked test cleanup path' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

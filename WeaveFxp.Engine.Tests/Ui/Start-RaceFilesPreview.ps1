param([string]$Binary = '', [int]$Port = 18791, [switch]$IncludeFailedRoute)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Binary) { $Binary = Join-Path $root 'Release/unified-files-web/Debug/net8.0/win-x64/WeaveFXP.dll' }
$fixture = Join-Path $root ('Release/unified-files-ui/' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $fixture 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$probe.Start()
$probe.Stop()
@{ enable_https_json_api=$false; enable_udp_api=$false; tray_icon_enabled=$false; auto_open_web_ui=$false;
   web_bind_address='127.0.0.1'; web_port=$Port; debug_logging=$false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
'{}' | Set-Content -LiteralPath (Join-Path $data 'sites.json') -Encoding utf8
$now = [DateTime]::UtcNow
$manifest = @(for ($i=0; $i -lt 620; $i++) {
    @{name=('payload.{0:d4}.rar' -f $i); crc='C71C0011'; sfv='release.sfv'; size=50000000;
      status=$(if ($i -eq 3) {'waiting'} elseif ($i -eq 4) {'missing'} else {'covered'});
      uploaded_by_us=($i -eq 1)}
})
$files = @(for ($i=0; $i -lt 125; $i++) {
    @{name='payload.0000.rar'; from_site='HV'; to_site='KPN'; size=50000000; status='fail';
      started_at=$now.AddSeconds(-125+$i).ToString('o'); error='Old failed attempt'}
})
$files += @(
    @{name='payload.0001.rar'; from_site='HV'; to_site='KPN'; size=50000000; status='done'; seconds=2; bps=25000000; started_at=$now.AddSeconds(-3).ToString('o')},
    @{name='payload.0002.rar'; from_site='HV'; to_site='KPN'; size=50000000; status='dupe'; started_at=$now.AddSeconds(-2).ToString('o')},
    @{name='release.sfv'; from_site='HV'; to_site='KPN'; size=20000; status='done'; started_at=$now.AddSeconds(-4).ToString('o')},
    @{name='notes.nfo'; from_site='HV'; to_site='KPN'; size=1000; status='done'; started_at=$now.AddSeconds(-5).ToString('o')},
    @{name='payload.0006.rar'; from_site='HV'; to_site='KPN'; size=50000000; status='active'; started_at=$now.AddSeconds(-10).ToString('o')},
    @{name='payload.0006.rar'; from_site='HV'; to_site='KPN'; size=50000000; status='done'; seconds=2; started_at=$now.AddSeconds(-6).ToString('o')},
    @{name='PAYLOAD.0000.RAR'; from_site='HV'; to_site='KPN'; size=50000000; status='active'; observed_bytes=10000000;
      bps=5000000; progress_observed_at=$now.ToString('o'); started_at=$now.AddSeconds(-1).ToString('o')}
)
$jobs = @{ 'files-preview'=@{ id='files-preview'; batch_id='files-preview'; type=1; state=0; created_at=$now.ToString('o');
    race_coverage_known=$true; files_total=622; files_covered=619; files_done=3;
    request=@{race=$true; label='Unified.Files.LOCAL-FIXTURE'; from_site='HV'; to_site='KPN';
        source_path='/TEST/Unified.Files.LOCAL-FIXTURE'; dest_path='/TEST/Unified.Files.LOCAL-FIXTURE'};
    sfv_files=$manifest; files=$files; events=@() };
   'files-preview-peer'=@{ id='files-preview-peer'; batch_id='files-preview'; type=1; state=0; created_at=$now.AddSeconds(1).ToString('o');
    request=@{race=$true; from_site='HV'; to_site='DC'; source_path='/TEST/Unified.Files.LOCAL-FIXTURE'; dest_path='/TEST/Unified.Files.LOCAL-FIXTURE'};
    files=@(@{name='payload.0000.rar'; from_site='HV'; to_site='DC'; size=50000000; status='active'; observed_bytes=10000000;
        bps=5000000; progress_observed_at=$now.ToString('o'); started_at=$now.AddSeconds(-1).ToString('o')}); events=@() } }
if ($IncludeFailedRoute) {
    $jobs['failed-preview'] = @{id='failed-preview'; batch_id='files-preview'; type=1; state=3; created_at=$now.AddSeconds(2).ToString('o');
        request=@{race=$true; from_site='HV'; to_site='ARCHIVE'; source_path='/TEST/Unified.Files.LOCAL-FIXTURE'; dest_path='/TEST/Unified.Files.LOCAL-FIXTURE'};
        error='Fictitious failed route'; files=@(); sfv_files=@(); events=@()}
}
$jobs |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $data 'jobs.json') -Encoding utf8
$env:WEAVEFXP_STATE = Join-Path $data 'state.json'
$env:WEAVEFXP_URL = "http://127.0.0.1:$Port"
$hostCommand = if ([IO.Path]::GetExtension($Binary) -eq '.exe') { $Binary } else { 'dotnet' }
$arguments = if ($hostCommand -eq 'dotnet') { @($Binary, '--no-browser') } else { @('--no-browser') }
$process = Start-Process -FilePath $hostCommand -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $fixture 'web.log') -RedirectStandardError (Join-Path $fixture 'error.log')
$result=@{pid=$process.Id; url="http://127.0.0.1:$Port/races"; fixture=$fixture; fictitious_data=$true}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'preview.json') -Encoding utf8
$result | ConvertTo-Json

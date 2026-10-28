param([string]$OutputDirectory=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-sources'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $classes|Out-Null
$files=@((Join-Path $PSScriptRoot 'fixtures\org\json\JSONObject.java'),(Join-Path $androidRoot 'src\com\local\lyricbridge\PlaybackSources.java'),(Join-Path $androidRoot 'src\com\local\lyricbridge\SnapshotFrames.java'),(Join-Path $PSScriptRoot 'PlaybackSourcesHostTest.java'))
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\PlayerSelection.java'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes @files
if($LASTEXITCODE -ne 0){throw 'Playback sources host compilation failed'}
$actual=@(& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.PlaybackSourcesHostTest)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Playback sources host execution failed'}
$result=[ordered]@{completed=$true;success=(@($actual.checks.PSObject.Properties|Where-Object{-not $_.Value}).Count -eq 0);checkedAt=[DateTimeOffset]::Now.ToString('o');scope='Production PlaybackSources and SnapshotFrames on host JVM with flat JSONObject fixture; no player app or phone controlled';checks=$actual.checks}
$json=$result|ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[Text.UTF8Encoding]::new($false))
Write-Output $json
if(-not $result.success){exit 1}

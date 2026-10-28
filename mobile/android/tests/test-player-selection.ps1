param([string]$OutputDirectory=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-player-selection'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $classes|Out-Null
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes (Join-Path $androidRoot 'src\com\local\lyricbridge\PlayerSelection.java') (Join-Path $PSScriptRoot 'PlayerSelectionHostTest.java')
if($LASTEXITCODE -ne 0){throw 'Player selection host compilation failed'}
$actual=@(& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.PlayerSelectionHostTest)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Player selection host execution failed'}
$result=[ordered]@{success=(@($actual.checks.PSObject.Properties|Where-Object{-not $_.Value}).Count -eq 0);checkedAt=[DateTimeOffset]::Now.ToString('o');scope='Pure Java platform and exact system/custom selection rules';checks=$actual.checks}
$json=$result|ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[Text.UTF8Encoding]::new($false))
Write-Output $json
if(-not $result.success){exit 1}

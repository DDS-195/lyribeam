param([string]$OutputDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-integration'))
$ErrorActionPreference='Stop'
$mobile=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testRoot=Join-Path $mobile 'developer\tests'
$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $classes|Out-Null
$files=@(Get-ChildItem -LiteralPath (Join-Path $testRoot 'fixtures') -Recurse -Filter '*.java'|ForEach-Object{$_.FullName})
$files+=Join-Path $mobile 'android\src\com\local\lyricbridge\IntegrationStore.java'
$files+=Join-Path $mobile 'android\src\com\local\lyricbridge\IntegrationProvider.java'
$files+=Join-Path $mobile 'developer\src\com\local\lyricbridge\sdk\LyricBridgeClient.java'
$files+=Join-Path $testRoot 'IntegrationHostTest.java'
$files+=Join-Path $mobile 'android\src\com\local\lyricbridge\PlayerSelection.java'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$java=Get-LyriBeamJavaBin
& (Join-Path $java 'javac.exe') -encoding UTF-8 --release 8 -d $classes @files
if($LASTEXITCODE -ne 0){throw 'Integration host compilation failed'}
$result=@(& (Join-Path $java 'java.exe') -cp $classes com.local.lyricbridge.IntegrationHostTest)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Integration host execution failed'}
$checks=@($result.checks.PSObject.Properties)
$report=[ordered]@{success=@($checks|Where-Object{-not $_.Value}).Count -eq 0;count=$checks.Count;scope='Production Provider/Store/SDK with host Android/Binder fixtures; no demo player or device controlled';checks=$result.checks}
$json=$report|ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[Text.UTF8Encoding]::new($false))
Write-Output $json
if(-not $report.success){exit 1}

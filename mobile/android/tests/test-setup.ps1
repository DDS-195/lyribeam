param([string]$OutputDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-setup'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Path $classes -Force | Out-Null
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes (Join-Path $androidRoot 'src\com\local\lyricbridge\SetupFlow.java') (Join-Path $PSScriptRoot 'SetupFlowHostTest.java')
if($LASTEXITCODE -ne 0){throw 'Setup flow tests did not compile'}
$output=& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.SetupFlowHostTest
if($LASTEXITCODE -ne 0){throw 'Setup flow tests failed'}
$output | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
$output

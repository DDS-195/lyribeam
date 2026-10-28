$ErrorActionPreference='Stop'
$mobile=Split-Path $PSScriptRoot -Parent
. (Join-Path $mobile 'scripts\build-env.ps1')
$java=Get-LyriBeamJavaBin
$androidJar=Get-LyriBeamAndroidJar
$classes=Join-Path $PSScriptRoot 'build\classes'
$dist=Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $classes,$dist|Out-Null
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -Filter '*.java'|ForEach-Object{$_.FullName})
& (Join-Path $java 'javac.exe') -encoding UTF-8 --release 8 -classpath $androidJar -d $classes @sources
if($LASTEXITCODE -ne 0){throw 'SDK compilation failed'}
& (Join-Path $java 'jar.exe') --create --file (Join-Path $dist 'lyricbridge-sdk-1.0.0.jar') -C $classes .
if($LASTEXITCODE -ne 0){throw 'SDK packaging failed'}
Write-Output (Join-Path $dist 'lyricbridge-sdk-1.0.0.jar')

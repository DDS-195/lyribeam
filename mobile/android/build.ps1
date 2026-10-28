$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $taskRoot 'scripts\build-env.ps1')
$javaBin = Get-LyriBeamJavaBin
$tools = Get-LyriBeamBuildTools
$androidJar = Get-LyriBeamAndroidJar
$buildPath = Join-Path $PSScriptRoot 'build'
$packagePath = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force -Path $buildPath,(Join-Path $buildPath 'classes'),(Join-Path $buildPath 'dex'),$packagePath | Out-Null
function Assert-Exit { if ($LASTEXITCODE -ne 0) { throw ('Build tool failed: ' + $LASTEXITCODE) } }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -Filter '*.java' | ForEach-Object { $_.FullName })
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -classpath $androidJar -d (Join-Path $buildPath 'classes') @sources
Assert-Exit
& (Join-Path $javaBin 'jar.exe') --create --file (Join-Path $buildPath 'classes.jar') -C (Join-Path $buildPath 'classes') .
Assert-Exit
& (Join-Path $javaBin 'java.exe') -cp (Join-Path $tools 'lib\d8.jar') com.android.tools.r8.D8 --lib $androidJar --min-api 26 --output (Join-Path $buildPath 'dex') (Join-Path $buildPath 'classes.jar')
Assert-Exit
# Native aapt2 directory enumeration does not support this project's Unicode path.
# Stage resource inputs in an isolated ASCII temp directory; retain the actual project location.
$resourceWorkspace = Join-Path ([IO.Path]::GetTempPath()) ('LyricBridge-resources-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resourceWorkspace | Out-Null
try {
    $resourceJar = Join-Path $resourceWorkspace 'android.jar'
    Copy-Item -LiteralPath $androidJar -Destination $resourceJar
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'res') -Destination (Join-Path $resourceWorkspace 'res') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AndroidManifest.xml') -Destination (Join-Path $resourceWorkspace 'AndroidManifest.xml')
    & (Join-Path $tools 'aapt2.exe') compile --dir (Join-Path $resourceWorkspace 'res') -o (Join-Path $resourceWorkspace 'compiled-res.zip')
    Assert-Exit
    & (Join-Path $tools 'aapt2.exe') link -o (Join-Path $resourceWorkspace 'resources.apk') -I $resourceJar --manifest (Join-Path $resourceWorkspace 'AndroidManifest.xml') --min-sdk-version 26 --target-sdk-version 35 (Join-Path $resourceWorkspace 'compiled-res.zip')
    Assert-Exit
    Copy-Item -LiteralPath (Join-Path $resourceWorkspace 'resources.apk') -Destination (Join-Path $buildPath 'resources.apk') -Force
    Copy-Item -LiteralPath (Join-Path $resourceWorkspace 'compiled-res.zip') -Destination (Join-Path $buildPath 'compiled-res.zip') -Force
} finally {
    $resolvedWorkspace = [IO.Path]::GetFullPath($resourceWorkspace)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedWorkspace.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedWorkspace -Leaf) -notlike 'LyricBridge-resources-*') { throw 'Unexpected temporary resource cleanup target' }
    Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $buildPath 'resources.apk') -Destination (Join-Path $buildPath 'unsigned.apk') -Force
& (Join-Path $javaBin 'jar.exe') --update --file (Join-Path $buildPath 'unsigned.apk') -C (Join-Path $buildPath 'dex') classes.dex
Assert-Exit
& (Join-Path $tools 'zipalign.exe') -f -p 4 (Join-Path $buildPath 'unsigned.apk') (Join-Path $buildPath 'aligned.apk')
Assert-Exit
$localPath = Join-Path $taskRoot '.local'
New-Item -ItemType Directory -Path $localPath -Force | Out-Null
# Generate a developer-local debug key. Never commit or distribute this private key.
$keyPath = Join-Path $localPath 'debug.keystore'
if (-not (Test-Path -LiteralPath $keyPath)) {
    & (Join-Path $javaBin 'keytool.exe') -genkeypair -keystore $keyPath -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=LyriBeam Local Debug'
    Assert-Exit
}
$manifestXml = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AndroidManifest.xml') -Raw)
$versionName = $manifestXml.manifest.GetAttribute('versionName', 'http://schemas.android.com/apk/res/android')
$apk = Join-Path $packagePath ('LyriBeam-' + $versionName + '-debug.apk')
& (Join-Path $javaBin 'java.exe') -jar (Join-Path $tools 'lib\apksigner.jar') sign --ks $keyPath --ks-pass pass:android --key-pass pass:android --out $apk (Join-Path $buildPath 'aligned.apk')
Assert-Exit
& (Join-Path $javaBin 'java.exe') -jar (Join-Path $tools 'lib\apksigner.jar') verify --verbose $apk
Assert-Exit
& (Join-Path $tools 'aapt2.exe') dump badging $apk
Assert-Exit
Write-Output ('APK ready: ' + $apk)

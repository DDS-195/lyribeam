# Shared environment discovery for the standalone Android build and host tests.
function Get-LyriBeamJavaBin {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidate=Join-Path $env:JAVA_HOME 'bin'
    } else {
        $compiler=Get-Command javac.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $compiler) { throw 'Install JDK 17 and set JAVA_HOME, or add its bin directory to PATH.' }
        $candidate=Split-Path $compiler.Source -Parent
    }
    foreach ($tool in @('java.exe','javac.exe','jar.exe','keytool.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidate $tool))) { throw ('JDK tool missing: ' + $tool) }
    }
    return $candidate
}

function Get-LyriBeamSdkRoot {
    $candidate=$env:ANDROID_SDK_ROOT
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate=$env:ANDROID_HOME }
    if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw 'Install Android SDK Platform 35 and Build Tools 35.0.1, then set ANDROID_SDK_ROOT.'
    }
    return $candidate
}

function Get-LyriBeamAndroidJar {
    $candidate=$env:ANDROID_PLATFORM_JAR
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate=Join-Path (Get-LyriBeamSdkRoot) 'platforms\android-35\android.jar'
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Android Platform 35 android.jar was not found.' }
    return $candidate
}

function Get-LyriBeamBuildTools {
    $candidate=$env:ANDROID_BUILD_TOOLS
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $toolsRoot=Join-Path (Get-LyriBeamSdkRoot) 'build-tools'
        $candidate=Join-Path $toolsRoot '35.0.1'
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            $installed=Get-ChildItem -LiteralPath $toolsRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and [version]$_.Name -ge [version]'35.0.1' } |
                Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
            if ($null -eq $installed) { throw 'Install Android Build Tools 35.0.1 or newer.' }
            $candidate=$installed.FullName
        }
    }
    foreach ($tool in @('aapt2.exe','zipalign.exe','lib\d8.jar','lib\apksigner.jar')) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidate $tool) -PathType Leaf)) { throw ('Android build tool missing: ' + $tool) }
    }
    return $candidate
}

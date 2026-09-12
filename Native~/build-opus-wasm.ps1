<#
.SYNOPSIS
Builds libopus for WebAssembly and drops it into Runtime/Plugins/WebGL.

.DESCRIPTION
Dissonance runs Opus through a native library on every platform. There is no
WebAssembly build in the box, so a browser needs one built here; this is the
only manual step in installing Dissonance 4 Web, and it is a one-off.

The build deliberately uses the Emscripten that ships inside the Unity editor
rather than a separately installed emsdk. Unity links the plugin archive into the
player with its own Emscripten, and an archive built by a different LLVM version
fails that link with errors that do not say so. Using Unity's own toolchain keeps
the two in step by construction - and means nothing has to be installed for this
beyond CMake.

.PARAMETER UnityPath
Unity editor installation to take Emscripten from. Defaults to the newest one
found under the usual Hub location.

.PARAMETER Emscripten
Emscripten directory to use instead, for example an emsdk upgrade. This is the
directory holding the emscripten, llvm, node and python folders. Only worth
setting if you know Unity is using the same version.

.PARAMETER OpusSource
An existing libopus source tree to build. Defaults to downloading the release
named by -OpusVersion.

.PARAMETER OpusVersion
libopus release to download when -OpusSource is not given.

.PARAMETER Clean
Discard any previous build directory first.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File Native~/build-opus-wasm.ps1 -OpusSource C:\src\opus
#>

[CmdletBinding()]
param(
    [string] $UnityPath,
    [string] $Emscripten,
    [string] $OpusSource,
    [string] $OpusVersion = "1.5.2",
    [switch] $Clean
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$PackageRoot = Split-Path -Parent $PSScriptRoot
$PluginDir = Join-Path $PackageRoot "Runtime/Plugins/WebGL"
$WorkDir = Join-Path $PSScriptRoot "build"

function Write-Step([string] $message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Find-Emscripten {
    if ($Emscripten) {
        if (-not (Test-Path (Join-Path $Emscripten "emscripten/emcc.py"))) {
            throw "-Emscripten '$Emscripten' does not look like an Emscripten installation (no emscripten/emcc.py inside it)"
        }
        return (Resolve-Path $Emscripten).Path
    }

    $candidates = @()

    if ($UnityPath) {
        $candidates += $UnityPath
    }
    else {
        $hub = Join-Path ${env:ProgramFiles} "Unity/Hub/Editor"
        if (Test-Path $hub) {
            # Newest editor first. Sorting the names as strings is good enough for
            # Unity's scheme and avoids parsing "6000.6.0f1" into a version.
            $candidates += Get-ChildItem -Path $hub -Directory |
                Sort-Object Name -Descending |
                ForEach-Object { $_.FullName }
        }
    }

    foreach ($candidate in $candidates) {
        $path = Join-Path $candidate "Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
        if (Test-Path (Join-Path $path "emscripten/emcc.py")) {
            return $path
        }
    }

    throw "Could not find Unity's Emscripten. Pass -UnityPath <editor install> or -Emscripten <emsdk directory>."
}

function Get-OpusSource {
    if ($OpusSource) {
        if (-not (Test-Path (Join-Path $OpusSource "CMakeLists.txt"))) {
            throw "-OpusSource '$OpusSource' has no CMakeLists.txt in it"
        }
        return (Resolve-Path $OpusSource).Path
    }

    $extracted = Join-Path $WorkDir "opus-$OpusVersion"
    if (Test-Path (Join-Path $extracted "CMakeLists.txt")) {
        Write-Host "    reusing $extracted"
        return $extracted
    }

    $archive = Join-Path $WorkDir "opus-$OpusVersion.tar.gz"
    if (-not (Test-Path $archive)) {
        $url = "https://downloads.xiph.org/releases/opus/opus-$OpusVersion.tar.gz"
        Write-Host "    downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    }

    Write-Host "    extracting"
    tar -xzf $archive -C $WorkDir
    if ($LASTEXITCODE -ne 0) { throw "could not extract $archive" }

    if (-not (Test-Path (Join-Path $extracted "CMakeLists.txt"))) {
        throw "extracted archive does not contain opus-$OpusVersion/CMakeLists.txt"
    }

    return $extracted
}

function Assert-Command([string] $name, [string] $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' is not on PATH. $hint"
    }
}

# ---------------------------------------------------------------------------

Assert-Command "cmake" "Install CMake, or add it to PATH; Visual Studio and the Unity build tools both ship one."

if ($Clean -and (Test-Path $WorkDir)) {
    Write-Step "Removing $WorkDir"
    Remove-Item -Recurse -Force $WorkDir
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Write-Step "Locating Emscripten"
$emRoot = Find-Emscripten
$emVersion = (Get-Content (Join-Path $emRoot "emscripten/emscripten-version.txt") -ErrorAction SilentlyContinue) -replace '"', ''
Write-Host "    $emRoot"
Write-Host "    version $emVersion"

# emcc.bat finds its interpreter through EMSDK_PYTHON, and its configuration
# through EM_CONFIG. Setting both makes the bundled toolchain self contained,
# which is what lets this run on a machine with no emsdk and no python.
$env:EMSDK_PYTHON = Join-Path $emRoot "python/python.exe"
$env:EM_CONFIG = Join-Path $emRoot ".emscripten"
$env:PATH = (Join-Path $emRoot "emscripten") + [IO.Path]::PathSeparator + $env:PATH

if (-not (Test-Path $env:EMSDK_PYTHON)) {
    # A standalone emsdk keeps python elsewhere; let emcc.bat fall back to PATH.
    Remove-Item Env:EMSDK_PYTHON
    Assert-Command "python" "A standalone Emscripten needs python on PATH."
}

Write-Step "Locating libopus source"
$opus = Get-OpusSource
Write-Host "    $opus"

Write-Step "Configuring"
$buildDir = Join-Path $WorkDir "opus-wasm"
$toolchain = Join-Path $emRoot "emscripten/cmake/Modules/Platform/Emscripten.cmake"

# Only the encoder, decoder and the soft clipper are wanted. Everything else -
# the tools, the tests, the float/fixed variants Dissonance never selects - is
# turned off so the archive stays small; it is linked into every page load.
cmake -S $opus -B $buildDir `
    -DCMAKE_TOOLCHAIN_FILE="$toolchain" `
    -DCMAKE_BUILD_TYPE=Release `
    -DBUILD_SHARED_LIBS=OFF `
    -DOPUS_BUILD_SHARED_LIBRARY=OFF `
    -DOPUS_BUILD_PROGRAMS=OFF `
    -DOPUS_BUILD_TESTING=OFF `
    -DBUILD_TESTING=OFF `
    -DOPUS_STACK_PROTECTOR=OFF `
    -DOPUS_FIXED_POINT=OFF
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

Write-Step "Building"
cmake --build $buildDir --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Write-Step "Publishing"
$archive = Get-ChildItem -Path $buildDir -Recurse -Filter "libopus.a" | Select-Object -First 1
if (-not $archive) {
    throw "the build finished but produced no libopus.a under $buildDir"
}

New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
$destination = Join-Path $PluginDir "libopus.a"
Copy-Item $archive.FullName $destination -Force

$size = [math]::Round((Get-Item $destination).Length / 1MB, 2)
Write-Host "    $destination ($size MB, built with Emscripten $emVersion)"

Write-Host ""
Write-Host "Done. Unity will import the archive on the next domain reload." -ForegroundColor Green
Write-Host "Check the Plugin Inspector once: the platform should be WebGL and nothing else."
Write-Host ""
Write-Host "Rebuild this after upgrading Unity to a version with a different Emscripten."

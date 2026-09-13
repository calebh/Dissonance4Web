<#
.SYNOPSIS
Builds libopus for WebAssembly and drops it into Runtime/Plugins/WebGL.

.DESCRIPTION
Dissonance runs Opus through a native library on every platform, and ships no
WebAssembly build of it. This package commits one; run this to rebuild it when
your Unity bundles a different Emscripten, or to move to a newer Opus.

The build deliberately uses the Emscripten that ships inside the Unity editor
rather than a separately installed emsdk. Unity links the plugin archive into the
player with its own Emscripten, and an archive built by a different LLVM version
fails that link with errors that do not say so. Using Unity's own toolchain keeps
the two in step by construction - and means nothing has to be installed for this
beyond CMake and Ninja.

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

# Every Opus entry point Dissonance's OpusNative class P/Invokes, plus the two
# ctl functions dissonance_opus_shim.c forwards to. An archive missing any of
# these links into a player that fails the moment voice starts.
$RequiredSymbols = @(
    "opus_get_version_string",
    "opus_encoder_create",
    "opus_encoder_destroy",
    "opus_encode_float",
    "opus_encoder_ctl",
    "opus_decoder_create",
    "opus_decoder_destroy",
    "opus_decode_float",
    "opus_decoder_ctl",
    "opus_pcm_soft_clip"
)

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

function Find-LlvmTool([string] $emRoot, [string] $name) {
    foreach ($candidate in @("$name.exe", $name)) {
        $path = Join-Path $emRoot "llvm/$candidate"
        if (Test-Path $path) { return $path }
    }
    throw "Emscripten's LLVM has no $name under $(Join-Path $emRoot 'llvm')"
}

# The check that would have caught a build which quietly used the wrong
# compiler. Every member of the archive has to be a WebAssembly object: an MSVC
# .obj with a .a name makes Unity's link step warn "neither Wasm object file nor
# LLVM bitcode" once per member, and then fail on the symbols it could not use.
function Assert-WasmArchive([string] $archive, [string] $emRoot) {
    $readobj = Find-LlvmTool $emRoot "llvm-readobj"
    $nm = Find-LlvmTool $emRoot "llvm-nm"

    $headers = & $readobj --file-header $archive
    if ($LASTEXITCODE -ne 0) { throw "llvm-readobj could not read $archive" }

    $formats = @($headers | ForEach-Object { if ($_ -match '^Format:\s*(.+)$') { $Matches[1].Trim() } })
    if ($formats.Count -eq 0) { throw "$archive contains no object files" }

    $foreign = @($formats | Where-Object { $_ -ne "WASM" } | Sort-Object -Unique)
    if ($foreign.Count -gt 0) {
        throw "$archive is not WebAssembly: its objects are $($foreign -join ', '). CMake compiled with something other than Emscripten; see Native~/README.md."
    }

    # llvm-nm notes "no symbols" on stderr for Opus's empty debug.c object, which is
    # expected. Windows PowerShell turns redirected native stderr into a terminating
    # error while ErrorActionPreference is Stop, so relax it for this one call.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $symbols = & $nm --defined-only $archive 2>$null
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($LASTEXITCODE -ne 0) { throw "llvm-nm could not read $archive" }

    $defined = @($symbols | ForEach-Object { ($_ -split '\s+')[-1] })
    $missing = @($RequiredSymbols | Where-Object { $defined -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "$archive does not define symbols Dissonance imports: $($missing -join ', ')"
    }

    Write-Host "    $($formats.Count) WebAssembly objects; all $($RequiredSymbols.Count) symbols Dissonance imports are defined"
}

function Assert-Command([string] $name, [string] $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' is not on PATH. $hint"
    }
}

# ---------------------------------------------------------------------------

Assert-Command "cmake" "Install CMake, or add it to PATH; Visual Studio and the Unity build tools both ship one."
Assert-Command "ninja" "Install Ninja (winget install Ninja-build.Ninja, or choco install ninja) and add it to PATH. It is required: without it CMake falls back to Visual Studio, which compiles Opus for x64 instead of WebAssembly. See Native~/README.md."

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

# CMake will not change generator in an existing build directory, and one
# configured before this script chose Ninja will have recorded CMake's Windows
# default instead - Visual Studio, whose output is MSVC objects. Start it over.
$cache = Join-Path $buildDir "CMakeCache.txt"
if (Test-Path $cache) {
    $recorded = Select-String -Path $cache -Pattern '^CMAKE_GENERATOR:INTERNAL=(.*)$' | Select-Object -First 1
    if ($recorded -and $recorded.Matches[0].Groups[1].Value -ne "Ninja") {
        Write-Host "    discarding $buildDir, which was configured for '$($recorded.Matches[0].Groups[1].Value)'"
        Remove-Item -Recurse -Force $buildDir
    }
}

# Ninja, explicitly. Given no -G, CMake on Windows picks the newest Visual
# Studio, and a Visual Studio build compiles with the VS toolset rather than the
# toolchain file's compiler - so it builds Opus with MSVC, without complaint, and
# names the result libopus.a. Ninja uses the toolchain's compiler everywhere.
#
# Only the encoder, decoder and the soft clipper are wanted. Everything else -
# the tools, the tests, the float/fixed variants Dissonance never selects - is
# turned off so the archive stays small; it is linked into every page load.
cmake -G Ninja -S $opus -B $buildDir `
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
cmake --build $buildDir --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

Write-Step "Verifying"
$archive = Get-ChildItem -Path $buildDir -Recurse -Filter "libopus.a" | Select-Object -First 1
if (-not $archive) {
    throw "the build finished but produced no libopus.a under $buildDir"
}

# Before publishing, so an archive Unity cannot link never replaces one it can.
Assert-WasmArchive $archive.FullName $emRoot

Write-Step "Publishing"
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

[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot,
    [switch]$InitializeVersion,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path -LiteralPath $Root).Path
Set-Location -LiteralPath $Root

$sourceNames = @(
    "Program.cs",
    "Vision.cs",
    "CaptureDiagnostics.cs",
    "Overlay.cs",
    "Settings.cs",
    "FocusGuard.cs",
    "LiveRecovery.cs"
)
$assetNames = @("SDL2.dll", "icon.png", "detector.json", "settings.json")
$exeName = "SprintTimerIntegrated.exe"
$installNames = @("Program.cs", "Vision.cs", "LiveRecovery.cs", "Build.ps1", $exeName)

function Assert-AppClosed {
    if (Get-Process -Name "SprintTimerIntegrated" -ErrorAction SilentlyContinue) {
        throw "Close SprintTimerIntegrated completely, then run this again."
    }
}

function Get-GitBlobHash {
    param([string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $header = [Text.Encoding]::ASCII.GetBytes(
        "blob " + $bytes.Length + [char]0
    )
    $sha = [Security.Cryptography.SHA1]::Create()
    try {
        $buffer = New-Object byte[] ($header.Length + $bytes.Length)
        [Array]::Copy($header, 0, $buffer, 0, $header.Length)
        [Array]::Copy($bytes, 0, $buffer, $header.Length, $bytes.Length)
        return ([BitConverter]::ToString(
            $sha.ComputeHash($buffer)
        )).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-X64PE {
    param([string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Not a Windows PE file: $Path"
        }

        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or ($offset + 6) -gt $stream.Length) {
            throw "Invalid PE header offset: $Path"
        }

        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature: $Path"
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664) {
            throw ("Expected x64 PE machine 0x8664; found 0x{0:X4}: {1}" -f $machine, $Path)
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Replace-ExactlyOnce {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New
    )

    $count = [regex]::Matches($Text, [regex]::Escape($Old)).Count
    if ($count -ne 1) {
        throw "Expected exactly one patch anchor; found $count : $Old"
    }
    return $Text.Replace($Old, $New)
}

Assert-AppClosed

foreach ($name in ($sourceNames + $assetNames)) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $name) -PathType Leaf)) {
        throw "Required file missing: $name"
    }
}

$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The expected .NET Framework C# compiler was not found."
}

Assert-X64PE -Path (Join-Path $Root "SDL2.dll")

$inputNames = @($sourceNames + $assetNames)
foreach ($optional in @("Build.ps1", $exeName)) {
    if (Test-Path -LiteralPath (Join-Path $Root $optional) -PathType Leaf) {
        $inputNames += $optional
    }
}

$originalHashes = @{}
foreach ($name in $inputNames) {
    $originalHashes[$name] = (
        Get-FileHash -LiteralPath (Join-Path $Root $name) -Algorithm SHA256
    ).Hash
}

$stage = Join-Path $env:TEMP (
    "SprintTimer-build-" + [Guid]::NewGuid().ToString("N")
)
New-Item -ItemType Directory -Path $stage | Out-Null
Write-Host "Staging: $stage"

foreach ($name in ($sourceNames + $assetNames)) {
    Copy-Item -LiteralPath (Join-Path $Root $name) `
        -Destination (Join-Path $stage $name)
}
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stage "Build.ps1")

$programPath = Join-Path $stage "Program.cs"
$program = [IO.File]::ReadAllText($programPath)
$utf8 = New-Object Text.UTF8Encoding($false)

if ($InitializeVersion) {
    $expectedBlob = "9dcdbec1a41b4cf6bb10490dcad968db71083194"
    $actualBlob = Get-GitBlobHash -Path (Join-Path $Root "Program.cs")
    if ($actualBlob -ne $expectedBlob) {
        throw "Program.cs differs from the reviewed baseline. Nothing installed. Staging: $stage"
    }


    # Insert the version inside the existing class, without changing its hooks.
    $classPattern = 'public sealed partial class InputTest : Form\s*\{'
    $classMatch = [regex]::Match($program, $classPattern)
    if (-not $classMatch.Success) {
        throw "Could not locate the InputTest class body."
    }
    $insertAt = $classMatch.Index + $classMatch.Length
    $program = $program.Insert(
        $insertAt,
        "`r`n    private const string AppVersion = `"0.1.1`";`r`n"
    )

    $program = Replace-ExactlyOnce -Text $program `
        -Old 'Text = "Sprint Timer - Icon Integration Test";' `
        -New 'Text = "Sprint Timer - v" + AppVersion;'

    $program = Replace-ExactlyOnce -Text $program `
        -Old '        InitializePersistentSettings();' `
        -New (
            '        InitializePersistentSettings();' + "`r`n" +
            '        LogVision("Sprint Timer v" + AppVersion +' + "`r`n" +
            '            " | x64 | EXE: " + Application.ExecutablePath);'
        )

    [IO.File]::WriteAllText($programPath, $program, $utf8)
}

$versionMatch = [regex]::Match(
    $program,
    'private const string AppVersion = "(\d+\.\d+\.\d+)";'
)
if (-not $versionMatch.Success) {
    throw "AppVersion was not found. Nothing installed."
}
$version = $versionMatch.Groups[1].Value
$assemblyVersion = "$version.0"

$identity = @"
using System.Reflection;
[assembly: AssemblyTitle("SprintTimerIntegrated")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@
$identityPath = Join-Path $stage "BuildIdentity.cs"
[IO.File]::WriteAllText($identityPath, $identity, $utf8)

$stageExe = Join-Path $stage $exeName
$compilerArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/out:$stageExe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Web.Extensions.dll"
)
foreach ($name in $sourceNames) {
    $compilerArgs += Join-Path $stage $name
}
$compilerArgs += $identityPath

Write-Host "`nCompiling Sprint Timer v$version (x64)..."
$compilerOutput = @(& $compiler @compilerArgs 2>&1)
$compilerExit = $LASTEXITCODE
$compilerOutput | ForEach-Object { Write-Host $_ }
$compilerOutput | Out-File -LiteralPath (Join-Path $stage "compiler-output.txt") -Encoding utf8

if ($compilerExit -ne 0) {
    throw "Compilation failed with exit code $compilerExit. Nothing installed. See $stage"
}
if (-not (Test-Path -LiteralPath $stageExe -PathType Leaf)) {
    throw "Compiler returned success but no EXE exists. Nothing installed."
}
if ((Get-Item -LiteralPath $stageExe).Length -eq 0) {
    throw "Compiled EXE is empty. Nothing installed."
}

Assert-X64PE -Path $stageExe
$builtInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($stageExe)
if ($builtInfo.FileVersion -ne $assemblyVersion) {
    throw "Compiled EXE version check failed. Nothing installed."
}

Assert-AppClosed
foreach ($name in $inputNames) {
    $path = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$name disappeared during compilation. Nothing installed."
    }
    $currentHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($currentHash -ne $originalHashes[$name]) {
        throw "$name changed during compilation. Nothing installed."
    }
}
foreach ($name in $installNames) {
    if (-not $originalHashes.ContainsKey($name) -and
        (Test-Path -LiteralPath (Join-Path $Root $name))) {
        throw "$name appeared during compilation. Nothing installed."
    }
}

$backup = Join-Path $Root (
    "backup-build-v{0}-{1}-{2}" -f
    $version,
    (Get-Date -Format "yyyyMMdd-HHmmss"),
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
)
New-Item -ItemType Directory -Path $backup | Out-Null

foreach ($name in $inputNames) {
    $destination = Join-Path $backup $name
    Copy-Item -LiteralPath (Join-Path $Root $name) -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
        $originalHashes[$name]) {
        throw "Backup verification failed for $name. Nothing installed."
    }
}
Copy-Item -LiteralPath (Join-Path $stage "compiler-output.txt") `
    -Destination (Join-Path $backup "new-build-compiler-output.txt")

$touched = New-Object 'System.Collections.Generic.List[string]'
try {
    foreach ($name in $installNames) {
        $touched.Add($name)
        Copy-Item -LiteralPath (Join-Path $stage $name) `
            -Destination (Join-Path $Root $name) -Force

        $expected = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $Root $name) -Algorithm SHA256).Hash
        if ($actual -ne $expected) {
            throw "Installed file verification failed: $name"
        }
    }
}
catch {
    $installError = $_
    foreach ($name in $touched) {
        try {
            $destination = Join-Path $Root $name
            if ($originalHashes.ContainsKey($name)) {
                Copy-Item -LiteralPath (Join-Path $backup $name) `
                    -Destination $destination -Force
                if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
                    $originalHashes[$name]) {
                    throw "Restored file hash mismatch."
                }
            }
            elseif (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Force
            }
        }
        catch {
            Write-Warning "Rollback failed for $name. Recover it from $backup. $($_.Exception.Message)"
        }
    }
    throw $installError
}

$installedExe = Join-Path $Root $exeName
$item = Get-Item -LiteralPath $installedExe
$hash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash

Write-Host "`nBUILD AND INSTALL SUCCEEDED: Sprint Timer v$version (x64)" -ForegroundColor Green
Write-Host "Installed EXE: $installedExe"
Write-Host "EXE version:   $assemblyVersion"
Write-Host "Modified:      $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "Bytes:         $($item.Length)"
Write-Host "SHA256:        $hash"
Write-Host "Backup:        $backup"
Write-Host "Build installed. Runtime behavior is determined by the source; settings and assets were retained."

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

if (-not $NoLaunch) {
    try {
        Start-Process -FilePath $installedExe -WorkingDirectory $Root -ErrorAction Stop
        Write-Host "Launch requested for the exact installed EXE."
    }
    catch {
        Write-Warning "Build and installation succeeded, but launch failed: $($_.Exception.Message)"
        Write-Host "Launch manually: $installedExe"
    }
}
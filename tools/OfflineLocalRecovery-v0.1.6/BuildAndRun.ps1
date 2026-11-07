$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$version = "0.1.6"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework compiler not found."
}

$temporaryExe = Join-Path $PSScriptRoot (
    "OfflineLocalRecovery-" + [Guid]::NewGuid().ToString("N") + ".exe"
)
$exe = Join-Path $PSScriptRoot "OfflineLocalRecovery.exe"
$source = Join-Path $PSScriptRoot "OfflineLocalRecovery.cs"
$argsList = @(
    "/nologo", "/target:exe", "/platform:x64", "/optimize+",
    "/out:$temporaryExe",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Web.Extensions.dll",
    $source
)

& $compiler @argsList
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $temporaryExe)) {
    throw "Compilation failed; existing tool EXE was not replaced."
}
if ((Get-Item -LiteralPath $temporaryExe).Length -eq 0 -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($temporaryExe).FileVersion -ne "0.1.6.0") {
    throw "Compiled tool failed verification; existing EXE was not replaced."
}

$previousExe = $null
if (Test-Path -LiteralPath $exe) {
    $previousExe = Join-Path $PSScriptRoot (
        "results\backup-" + [Guid]::NewGuid().ToString("N") + ".exe"
    )
    New-Item -ItemType Directory -Path (Split-Path $previousExe -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $exe -Destination $previousExe
    if ((Get-FileHash -LiteralPath $previousExe).Hash -ne
        (Get-FileHash -LiteralPath $exe).Hash) {
        throw "EXE backup verification failed."
    }
}
try {
    Copy-Item -LiteralPath $temporaryExe -Destination $exe -Force
    if ((Get-FileHash -LiteralPath $temporaryExe).Hash -ne
        (Get-FileHash -LiteralPath $exe).Hash) {
        throw "Installed EXE verification failed."
    }
}
catch {
    if ($previousExe) {
        Copy-Item -LiteralPath $previousExe -Destination $exe -Force
    }
    elseif (Test-Path -LiteralPath $exe) {
        Remove-Item -LiteralPath $exe -Force
    }
    throw
}
Remove-Item -LiteralPath $temporaryExe -Force

Write-Host "BUILD SUCCEEDED: Offline Local Recovery v$version (x64)" -ForegroundColor Green
Get-Item -LiteralPath $exe | Select-Object FullName, Length, LastWriteTime | Format-List

$output = Join-Path $PSScriptRoot (
    "results\run-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
)
& $exe (Join-Path $PSScriptRoot "inputs") $output "2560" "1440" "0.595"
if ($LASTEXITCODE -ne 0) {
    throw "Analysis failed. See output above."
}

Import-Csv -LiteralPath (Join-Path $output "local-summary.csv") |
    Select-Object image, seed, score, detected, mean_search_ms, agrees_with_reported_presence |
    Format-Table -AutoSize

Write-Host "Results: $output"
Start-Process explorer.exe -ArgumentList ('"{0}"' -f $output)
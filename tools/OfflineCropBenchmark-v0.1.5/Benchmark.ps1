[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$version = "0.1.5"
$exe = Join-Path $PSScriptRoot "OfflineCropExperiment.exe"
$inputs = Join-Path $PSScriptRoot "inputs"

try {
    $Host.UI.RawUI.WindowTitle = "Offline Crop Benchmark v$version"
}
catch {}

Write-Host "Offline Crop Benchmark v$version"
Write-Host "One warm-up pass, then two measured passes."
Write-Host "Timer application unchanged. Saved images only."
Write-Host "Per-image timing includes loading, baseline detection, search and image writing."

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Benchmark EXE missing."
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne "0.1.5.0") {
    throw "Unexpected benchmark EXE version."
}

$session = Join-Path $PSScriptRoot (
    "results\benchmark-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
)
New-Item -ItemType Directory -Path $session -Force | Out-Null

$records = New-Object 'System.Collections.Generic.List[object]'
$passes = New-Object 'System.Collections.Generic.List[object]'
$csv = Join-Path $session "frame-timings.csv"

for ($pass = 0; $pass -lt 3; $pass++) {
    $warmup = ($pass -eq 0)
    $label = if ($warmup) { "warmup" } else { "measured-$pass" }
    $output = Join-Path $session $label
    $log = Join-Path $session ($label + "-console.txt")
    $logLines = New-Object 'System.Collections.Generic.List[string]'

    Write-Host "`n=== $label ===" -ForegroundColor Cyan

    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $exe
    $process.StartInfo.WorkingDirectory = $PSScriptRoot
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $false
    $process.StartInfo.Arguments = (
        '"{0}" "{1}" 2560 1440 0.595' -f $inputs, $output
    )

    $passWatch = [Diagnostics.Stopwatch]::StartNew()
    $frameWatch = $null
    $frameName = $null
    $completed = 0

    try {
        if (-not $process.Start()) {
            throw "Could not launch benchmark EXE."
        }

        while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
            $logLines.Add($line)
            Write-Host $line

            if ($line -match '^Image \d+/6: (.+)$') {
                if ($null -ne $frameWatch) {
                    throw "A new image started before the previous result was received."
                }
                $frameName = $Matches[1]
                $frameWatch = [Diagnostics.Stopwatch]::StartNew()
            }
            elseif ($line -match '^Baseline=') {
                if ($null -eq $frameWatch) {
                    throw "Received result without an image-start marker."
                }

                $frameWatch.Stop()
                $records.Add([PSCustomObject]@{
                    pass = $label
                    warmup = $warmup
                    image = $frameName
                    elapsed_ms = [Math]::Round(
                        $frameWatch.Elapsed.TotalMilliseconds, 3
                    )
                })
                $completed++
                $frameWatch = $null
                $frameName = $null

                $records | Export-Csv -LiteralPath $csv `
                    -NoTypeInformation -Encoding UTF8
            }
        }

        $process.WaitForExit()
        $passWatch.Stop()

        if ($process.ExitCode -ne 0) {
            throw "Analysis failed with exit code $($process.ExitCode)."
        }
        if ($completed -ne 6) {
            throw "Expected six frame results, received $completed."
        }

        $summaryPath = Join-Path $output "experiment-summary.csv"
        $summary = @(Import-Csv -LiteralPath $summaryPath)
        if ($summary.Count -ne 6 -or
            @($summary | Where-Object baseline_score_matches -ne "True").Count -gt 0) {
            throw "Analysis summary is incomplete or baseline reproduction failed."
        }

        $passes.Add([PSCustomObject]@{
            pass = $label
            warmup = $warmup
            process_elapsed_ms = [Math]::Round(
                $passWatch.Elapsed.TotalMilliseconds, 3
            )
        })
    }
    finally {
        $logLines | Set-Content -LiteralPath $log -Encoding UTF8

        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill()
                    $process.WaitForExit()
                }
            }
            catch {}
            $process.Dispose()
        }
    }
}

$passes | Export-Csv -LiteralPath (Join-Path $session "pass-timings.csv") `
    -NoTypeInformation -Encoding UTF8

$aggregate = @(
    $records |
        Where-Object { -not $_.warmup } |
        Group-Object image |
        ForEach-Object {
            $stats = $_.Group | Measure-Object elapsed_ms -Minimum -Maximum -Average
            [PSCustomObject]@{
                image = $_.Name
                measured_runs = $_.Count
                mean_ms = [Math]::Round($stats.Average, 3)
                min_ms = [Math]::Round($stats.Minimum, 3)
                max_ms = [Math]::Round($stats.Maximum, 3)
            }
        }
)

$aggregate | Export-Csv -LiteralPath (Join-Path $session "timing-summary.csv") `
    -NoTypeInformation -Encoding UTF8

@"
Offline Crop Benchmark v$version

One separate-process warm-up pass and two measured passes.
Each pass processes the same six saved images in the same order.
Each process therefore still has its own runtime/JIT initialization.
This is a practical offline timing sample, not a rigorous steady-state benchmark.

Frame time is measured externally between image-start and result output.
It includes image loading, baseline detection, crop search and image writing.
Console/pipe scheduling can affect the measurements.
It excludes live screen acquisition, UI-thread scheduling and live integration.
Total process time is recorded separately.

Two measured samples per image are insufficient for tail-latency claims.
Do not interpret a six-frame classification result as general reliability.
"@ | Set-Content -LiteralPath (Join-Path $session "README.txt") -Encoding UTF8

Write-Host "`nBENCHMARK COMPLETE: v$version" -ForegroundColor Green
Write-Host "`nTIMING SUMMARY CSV:"
Get-Content -LiteralPath (Join-Path $session "timing-summary.csv")
Write-Host "`nResults: $session"
Start-Process explorer.exe -ArgumentList ('"{0}"' -f $session)
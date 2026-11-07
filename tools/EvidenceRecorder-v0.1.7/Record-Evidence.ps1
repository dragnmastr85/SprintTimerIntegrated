param(
    [ValidateSet("Absent", "Activation", "Relocation")]
    [string]$Scenario
)

$ErrorActionPreference = "Stop"
$version = "0.1.7"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$configPath = Join-Path $root "tools\OfflineCropExperiment-v0.1.4\inputs\detector.json"

if ([IntPtr]::Size -ne 8) {
    throw "Run this recorder in 64-bit PowerShell."
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Detector snapshot missing."
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ("SprintEvidence.Native" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace SprintEvidence {
    public static class Native {
        [DllImport("user32.dll", SetLastError=true)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr window, out uint processId);
    }
}
"@
}
if (-not [SprintEvidence.Native]::SetProcessDPIAware()) {
    throw "Could not establish DPI awareness. Start a fresh powershell.exe process using the supplied launch command."
}

$git = Get-Command git -ErrorAction Stop
Push-Location $root
try {
    & $git.Source check-ignore --quiet -- "capture-diagnostics/validation-probe.png"
    if ($LASTEXITCODE -ne 0) {
        throw "capture-diagnostics is not Git-ignored. No recording created; do not capture until this is resolved."
    }
}
finally {
    Pop-Location
}

$cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
foreach ($name in @("band_left", "band_right", "band_top", "band_bottom")) {
    $property = $cfg.PSObject.Properties[$name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "Detector snapshot lacks $name."
    }
    $value = [double]$property.Value
    if ([double]::IsNaN($value) -or
        [double]::IsInfinity($value) -or
        $value -lt 0 -or $value -gt 1) {
        throw "Invalid band fraction: $name"
    }
}
if ([double]$cfg.band_left -ge [double]$cfg.band_right -or
    [double]$cfg.band_top -ge [double]$cfg.band_bottom) {
    throw "Detector band is empty or reversed."
}

$screens = @([System.Windows.Forms.Screen]::AllScreens)
Write-Host "`nEvidence Recorder v$version"
Write-Host "No timer or detector changes."
Write-Host "`nAvailable monitors:"
for ($i = 0; $i -lt $screens.Count; $i++) {
    $s = $screens[$i]
    Write-Host ("[{0}] {1}  {2}x{3} at ({4},{5})  Primary={6}" -f
        $i, $s.DeviceName, $s.Bounds.Width, $s.Bounds.Height,
        $s.Bounds.X, $s.Bounds.Y, $s.Primary)
}

$selection = Read-Host "Enter the number of the monitor displaying the game"
$index = 0
if (-not [int]::TryParse($selection, [ref]$index) -or
    $index -lt 0 -or $index -ge $screens.Count) {
    throw "Invalid monitor selection."
}
$screen = $screens[$index]

if (-not $Scenario) {
    Write-Host "`n[1] Absent:     15 seconds with Fresh Start absent"
    Write-Host "[2] Activation: 45 seconds covering activation through actual end"
    Write-Host "[3] Relocation: 30 seconds covering a horizontal position change"
    switch (Read-Host "Choose scenario") {
        "1" { $Scenario = "Absent" }
        "2" { $Scenario = "Activation" }
        "3" { $Scenario = "Relocation" }
        default { throw "Invalid scenario." }
    }
}

$seconds = switch ($Scenario) {
    "Absent" { 15 }
    "Activation" { 45 }
    "Relocation" { 30 }
}

$left = [int][Math]::Round($screen.Bounds.Width * [double]$cfg.band_left)
$right = [int][Math]::Round($screen.Bounds.Width * [double]$cfg.band_right)
$top = [int][Math]::Round($screen.Bounds.Height * [double]$cfg.band_top)
$bottom = [int][Math]::Round($screen.Bounds.Height * [double]$cfg.band_bottom)
$width = $right - $left
$height = $bottom - $top

if ($width -le 0 -or $height -le 0 -or
    $left -lt 0 -or $top -lt 0 -or
    $right -gt $screen.Bounds.Width -or
    $bottom -gt $screen.Bounds.Height) {
    throw "Calculated band is invalid."
}

Write-Host ("`nMonitor: {0}; band: {1}x{2}, local origin ({3},{4})" -f
    $screen.DeviceName, $width, $height, $left, $top)
Write-Host "Scenario: $Scenario; duration: $seconds seconds."
Write-Host "Captures contain the selected screen band, regardless of foreground app."
Write-Host "Only proceed if that band is safe to record."
$answer = Read-Host "Type RECORD to proceed, or anything else to cancel"
if ($answer -cne "RECORD") {
    Write-Host "Cancelled. No recording created."
    return
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$id = [Guid]::NewGuid().ToString("N").Substring(0, 6)
$output = Join-Path $root "capture-diagnostics\validation-$Scenario-$stamp-$id"
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $configPath -Destination (Join-Path $output "detector.json")

$utf8 = New-Object System.Text.UTF8Encoding($false)
$inv = [Globalization.CultureInfo]::InvariantCulture
$manifest = [ordered]@{
    recorder_version = $version
    scenario = $Scenario
    scenario_is_not_frame_ground_truth = $true
    requested_seconds = $seconds
    requested_interval_ms = 100
    monitor = $screen.DeviceName
    screen_x = $screen.Bounds.X
    screen_y = $screen.Bounds.Y
    screen_width = $screen.Bounds.Width
    screen_height = $screen.Bounds.Height
    band_local_x = $left
    band_local_y = $top
    band_width = $width
    band_height = $height
    detector_snapshot_sha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    timer_modified = $false
}
[IO.File]::WriteAllText(
    (Join-Path $output "manifest.json"),
    ($manifest | ConvertTo-Json -Depth 5), $utf8
)

Write-Host "`nSwitch to the game now. Recording starts in 5 seconds."
Start-Sleep -Seconds 5

$bitmap = $null
$graphics = $null
$writer = $null
$clock = [Diagnostics.Stopwatch]::new()
$rows = 0
$failures = 0
$missedSlots = 0
$nextMs = 0.0
$previousStart = $null
$intervals = [Collections.Generic.List[double]]::new()
$fatal = $null

try {
    $bitmap = [Drawing.Bitmap]::new(
        $width, $height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $writer = [IO.StreamWriter]::new(
        (Join-Path $output "frames.csv"), $false, $utf8)
    $writer.WriteLine(
        "frame,utc,capture_start_ms,interval_ms,capture_ms,save_ms,foreground_pid,status,file,error")

    $clock.Start()
    while ($clock.Elapsed.TotalMilliseconds -lt ($seconds * 1000)) {
        $wait = $nextMs - $clock.Elapsed.TotalMilliseconds
        if ($wait -gt 1) {
            Start-Sleep -Milliseconds ([int][Math]::Ceiling($wait))
        }
        if ($clock.Elapsed.TotalMilliseconds -ge ($seconds * 1000)) { break }

        $start = $clock.Elapsed.TotalMilliseconds
        $utc = [DateTime]::UtcNow.ToString("o", $inv)
        $interval = ""
        if ($null -ne $previousStart) {
            $delta = $start - $previousStart
            $intervals.Add($delta)
            $interval = $delta.ToString("F3", $inv)
        }
        $previousStart = $start

        [uint32]$foregroundPid = 0
        $window = [SprintEvidence.Native]::GetForegroundWindow()
        [void][SprintEvidence.Native]::GetWindowThreadProcessId(
            $window, [ref]$foregroundPid)

        $file = "frame-{0:D6}.png" -f $rows
        $captureMs = ""
        $saveMs = ""
        $status = "ok"
        $errorText = ""

        try {
            $operationStart = $clock.Elapsed.TotalMilliseconds
            $graphics.CopyFromScreen(
                $screen.Bounds.X + $left,
                $screen.Bounds.Y + $top,
                0, 0, $bitmap.Size,
                [Drawing.CopyPixelOperation]::SourceCopy)
            $captureMs = ($clock.Elapsed.TotalMilliseconds - $operationStart).
                ToString("F3", $inv)

            $operationStart = $clock.Elapsed.TotalMilliseconds
            $bitmap.Save(
                (Join-Path $output $file),
                [Drawing.Imaging.ImageFormat]::Png)
            $saveMs = ($clock.Elapsed.TotalMilliseconds - $operationStart).
                ToString("F3", $inv)
        }
        catch {
            $status = "error"
            $failures++
            $errorText = $_.Exception.Message.Replace('"', '""').
                Replace("`r", " ").Replace("`n", " ")
            $file = ""
        }

        $writer.WriteLine((
            '{0},{1},{2},{3},{4},{5},{6},{7},{8},"{9}"' -f
            $rows, $utc, $start.ToString("F3", $inv), $interval,
            $captureMs, $saveMs, $foregroundPid, $status, $file, $errorText
        ))
        $writer.Flush()
        $rows++

        if ($status -eq "error") {
            throw "Capture failed. Stopped rather than treating the frame as target-absent."
        }

        $nextMs += 100
        $now = $clock.Elapsed.TotalMilliseconds
        while ($nextMs -lt $now) {
            $nextMs += 100
            $missedSlots++
        }
    }
}
catch {
    $fatal = $_.Exception.Message
}
finally {
    $clock.Stop()
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $graphics) { $graphics.Dispose() }
    if ($null -ne $bitmap) { $bitmap.Dispose() }
}

$summary = [ordered]@{
    completed_without_error = ($null -eq $fatal)
    frame_attempts = $rows
    capture_errors = $failures
    skipped_schedule_slots = $missedSlots
    elapsed_ms = $clock.Elapsed.TotalMilliseconds
    mean_start_interval_ms = $null
    max_start_interval_ms = $null
    error = $fatal
}
if ($intervals.Count -gt 0) {
    $stats = $intervals | Measure-Object -Average -Maximum
    $summary.mean_start_interval_ms = $stats.Average
    $summary.max_start_interval_ms = $stats.Maximum
}
[IO.File]::WriteAllText(
    (Join-Path $output "recording-summary.json"),
    ($summary | ConvertTo-Json -Depth 5), $utf8
)

Write-Host "`nRecording finished."
$summary | Format-List
Write-Host "Saved: $output"
Write-Host "Inspect a few PNGs to confirm the correct game band was captured."

$notes = Read-Host "Describe what actually happened (or press Enter to leave unlabelled)"
[IO.File]::WriteAllText(
    (Join-Path $output "user-notes.txt"), $notes, $utf8
)

Start-Process explorer.exe -ArgumentList ('"{0}"' -f $output)
if ($null -ne $fatal) {
    throw "Recording incomplete: $fatal"
}
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

public sealed partial class InputTest
{
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr ArcForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint ArcWindowProcess(
        IntPtr window, out uint processId);

    private bool? lastArcFocus;
    private bool suppressFocusResetLog;

    private static bool IsArcForeground()
    {
        try
        {
            IntPtr window = ArcForegroundWindow();
            if (window == IntPtr.Zero)
                return false;

            uint processId;
            ArcWindowProcess(window, out processId);
            if (processId == 0)
                return false;

            using (Process process = Process.GetProcessById((int)processId))
            {
                return String.Equals(
                    process.ProcessName,
                    "PioneerGame",
                    StringComparison.OrdinalIgnoreCase
                );
            }
        }
        catch
        {
            return false;
        }
    }

    private bool AllowArcGameplayPolling(object sender, EventArgs e)
    {
        bool focused = IsArcForeground();

        if (!lastArcFocus.HasValue || lastArcFocus.Value != focused)
        {
            lastArcFocus = focused;

            suppressFocusResetLog = true;
            try
            {
                // Invalidates in-flight vision results and clears input state.
                ResetState();
                lastScore = 0;
                lastCandidateCount = 0;
            }
            finally
            {
                suppressFocusResetLog = false;
            }
        }

        if (focused)
            return true;

        // Drain SDL events without processing gameplay input.
        while (SDL.SDL_PollEvent(eventBuffer) != 0) { }

        // Explicit binding assignment remains available in this window.
        if (capturing && ContainsFocus)
        {
            PollInputCore(sender, e);
            return false;
        }

        stateLabel.Text = "WAITING FOR ARC FOCUS";
        stateLabel.ForeColor = Color.DimGray;
        counterLabel.Text = "0.0 s";
        if (ShouldRefreshDebugSummary()) visionSummary.Text =
            "Gameplay monitoring inactive: ARC Raiders is not foreground.\r\n" +
            "Input and icon detection are disabled until the game is focused.\r\n" +
            "The previous timer session was cleared; no off-focus gap is counted.";

        return false;
    }
}
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class SDL
{
    private const string Library = "SDL2.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_SetMainReady();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_SetHint(string name, string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_Init(uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GetError();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_NumJoysticks();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_IsGameController(int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GameControllerNameForIndex(int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SDL_GameControllerOpen(int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_GameControllerClose(IntPtr controller);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_GameControllerGetAttached(IntPtr controller);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte SDL_GameControllerGetButton(IntPtr controller, int button);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern short SDL_GameControllerGetAxis(IntPtr controller, int axis);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_PollEvent(IntPtr eventBuffer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_Quit();
}

public sealed partial class InputTest : Form
{
    private const string AppVersion = "0.2.3";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private sealed class Device
    {
        internal int Index;
        internal string Name;
        public override string ToString() { return Name; }
    }

    private static readonly string[] ButtonNames = {
        "A / Cross", "B / Circle", "X / Square", "Y / Triangle",
        "Back / Share", "Guide", "Start / Options",
        "Left stick click", "Right stick click",
        "Left shoulder", "Right shoulder",
        "D-pad up", "D-pad down", "D-pad left", "D-pad right",
        "Miscellaneous", "Paddle 1", "Paddle 2", "Paddle 3",
        "Paddle 4", "Touchpad click"
    };

    private readonly ComboBox devices = new ComboBox();
    private readonly ComboBox modes = new ComboBox();
    private readonly Label bindingLabel = new Label();
    private readonly Label stateLabel = new Label();
    private readonly Label counterLabel = new Label();
    private readonly Label messageLabel = new Label();
    private readonly Timer timer = new Timer();
    private readonly Stopwatch elapsed = new Stopwatch();
    private readonly IntPtr eventBuffer = Marshal.AllocHGlobal(64);

    private IntPtr controller = IntPtr.Zero;
    private bool capturing;
    private bool armed;
    private bool previousDown;
    private bool active;

    // Controller toggle tracking.
    private bool sprintArmed;
    private bool pendingStart;
    private long pendingDeadline;
    private readonly Stopwatch inputClock = Stopwatch.StartNew();

    // Normalized stick thresholds: 0.0 to 1.0.
    private const double NeutralDeadZone = 0.20;
    private const double ForwardStartThreshold = 0.08;
    private const double ForwardKeepThreshold = 0.04;
    private const int PendingStartMs = 300;
    private bool sdlReady;
    private int bindingButton = -1;
    private int bindingAxis = -1;
    private int bindingKey = -1;

    public InputTest()
    {
        Text = "Sprint Timer - v" + AppVersion;
        ClientSize = new Size(700, 650);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(30, 150);
        Font = new Font("Segoe UI", 9);

        Label deviceTitle = new Label();
        deviceTitle.Text = "Input device";
        deviceTitle.SetBounds(12, 12, 110, 20);
        Controls.Add(deviceTitle);

        devices.DropDownStyle = ComboBoxStyle.DropDownList;
        devices.SetBounds(12, 36, 320, 26);
        devices.SelectedIndexChanged += SelectDevice;
        Controls.Add(devices);

        Button refresh = new Button();
        refresh.Text = "Refresh";
        refresh.SetBounds(342, 35, 85, 27);
        refresh.Click += delegate { RefreshDevices(); };
        Controls.Add(refresh);

        modes.DropDownStyle = ComboBoxStyle.DropDownList;
        modes.Items.AddRange(new object[] { "Hold", "Toggle" });
        modes.SelectedIndex = 0;
        modes.SetBounds(12, 78, 125, 26);
        modes.SelectedIndexChanged += delegate { ResetState(); };
        Controls.Add(modes);

        Button capture = new Button();
        capture.Text = "Assign sprint input";
        capture.SetBounds(150, 77, 160, 29);
        capture.Click += delegate
        {
            ResetState();
            capturing = true;
            bindingLabel.Text = devices.SelectedIndex == 0
                ? "Press a keyboard key..."
                : "Press a controller button or trigger...";
        };
        Controls.Add(capture);

        Button reset = new Button();
        reset.Text = "Reset";
        reset.SetBounds(322, 77, 105, 29);
        reset.Click += delegate { ResetState(); };
        Controls.Add(reset);

        bindingLabel.SetBounds(12, 119, 415, 25);
        Controls.Add(bindingLabel);

        stateLabel.Font = new Font("Segoe UI", 15);
        stateLabel.TextAlign = ContentAlignment.MiddleCenter;
        stateLabel.SetBounds(12, 153, 415, 44);
        Controls.Add(stateLabel);

        counterLabel.Font = new Font("Segoe UI", 18);
        counterLabel.TextAlign = ContentAlignment.MiddleCenter;
        counterLabel.SetBounds(12, 199, 415, 40);
        Controls.Add(counterLabel);

        messageLabel.SetBounds(12, 251, 415, 48);
        messageLabel.Text =
            "Assign an input, release it, then focus ARC and test.\n" +
            "This window stays on top. No game inputs are generated.";
        Controls.Add(messageLabel);

        SDL.SDL_SetMainReady();
        SDL.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");

        if (SDL.SDL_Init(0x00002000) != 0)
            throw new Exception(
                "SDL initialization failed: " +
                Marshal.PtrToStringAnsi(SDL.SDL_GetError()));

        sdlReady = true;
        RefreshDevices();

        InitializeVision();
        InitializeReminderOverlay();
        InitializeCaptureDiagnostics();
        InitializePersistentSettings();
        InitializeDebugInterface();
        if (diagnosticLoggingEnabled) LogVision("Sprint Timer v" + AppVersion +
            " | x64 | EXE: " + Application.ExecutablePath);
        timer.Interval = 25;
        timer.Tick += PollInput;
        timer.Tick += UpdateReminderOverlay;
        timer.Start();
    }

    private void RefreshDevices()
    {
        capturing = false;
        CloseController();

        devices.Items.Clear();
        devices.Items.Add("Keyboard");

        while (SDL.SDL_PollEvent(eventBuffer) != 0) { }

        int count = SDL.SDL_NumJoysticks();
        for (int i = 0; i < count; i++)
        {
            if (SDL.SDL_IsGameController(i) == 0)
                continue;

            Device device = new Device();
            device.Index = i;
            device.Name = Marshal.PtrToStringAnsi(
                SDL.SDL_GameControllerNameForIndex(i));

            if (String.IsNullOrEmpty(device.Name))
                device.Name = "Controller " + i;

            devices.Items.Add(device);
        }

        devices.SelectedIndex = 0;
    }

    private void SelectDevice(object sender, EventArgs e)
    {
        CloseController();
        capturing = false;
        bindingKey = -1;
        bindingButton = -1;
        bindingAxis = -1;
        ResetState();
        bindingLabel.Text = "No sprint input assigned.";

        Device device = devices.SelectedItem as Device;
        if (device == null)
            return;

        controller = SDL.SDL_GameControllerOpen(device.Index);
        if (controller == IntPtr.Zero)
        {
            bindingLabel.Text = "Could not open controller.";
            messageLabel.Text = Marshal.PtrToStringAnsi(SDL.SDL_GetError());
        }
    }

    private void CloseController()
    {
        if (controller != IntPtr.Zero)
        {
            SDL.SDL_GameControllerClose(controller);
            controller = IntPtr.Zero;
        }
    }

    private void ResetState()
    {
        ResetVisionSession();
        elapsed.Reset();
        active = false;
        armed = false;
        previousDown = false;
        sprintArmed = false;
        pendingStart = false;
        pendingDeadline = 0;
        stateLabel.Text = "IDLE";
        stateLabel.ForeColor = Color.DimGray;
        counterLabel.Text = "0.0 s";
    }


    private void CaptureInput()
    {
        if (devices.SelectedIndex == 0)
        {
            for (int key = 8; key <= 254; key++)
            {
                // Skip generic modifiers; capture their left/right variants.
                if (key == 16 || key == 17 || key == 18)
                    continue;

                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                {
                    bindingKey = key;
                    bindingButton = -1;
                    bindingAxis = -1;
                    FinishCapture(((Keys)key).ToString());
                    return;
                }
            }
        }
        else if (controller != IntPtr.Zero)
        {
            for (int button = 0; button < ButtonNames.Length; button++)
            {
                if (SDL.SDL_GameControllerGetButton(controller, button) != 0)
                {
                    bindingButton = button;
                    bindingKey = -1;
                    bindingAxis = -1;
                    FinishCapture(ButtonNames[button]);
                    return;
                }
            }

            for (int axis = 4; axis <= 5; axis++)
            {
                if (SDL.SDL_GameControllerGetAxis(controller, axis) > 16000)
                {
                    bindingAxis = axis;
                    bindingKey = -1;
                    bindingButton = -1;
                    FinishCapture(axis == 4 ? "Left trigger" : "Right trigger");
                    return;
                }
            }
        }
    }

    private void FinishCapture(string name)
    {
        capturing = false;
        bindingLabel.Text = "Sprint input: " + name;
        stateLabel.Text = "RELEASE TO ARM";
        armed = false;
    }

    private bool ReadInput()
    {
        if (bindingKey >= 0)
            return (GetAsyncKeyState(bindingKey) & 0x8000) != 0;

        if (controller == IntPtr.Zero)
            return false;

        if (bindingButton >= 0)
            return SDL.SDL_GameControllerGetButton(
                controller, bindingButton) != 0;

        if (bindingAxis >= 0)
        {
            // Hysteresis avoids repeated edges near the trigger threshold.
            int threshold = previousDown ? 10000 : 16000;
            return SDL.SDL_GameControllerGetAxis(
                controller, bindingAxis) > threshold;
        }

        return false;
    }

    private void PollInputCore(object sender, EventArgs e)
    {
        while (SDL.SDL_PollEvent(eventBuffer) != 0) { }

        if (controller != IntPtr.Zero &&
            SDL.SDL_GameControllerGetAttached(controller) == 0)
        {
            CloseController();
            capturing = false;
            ResetState();
            bindingLabel.Text =
                "Controller disconnected. Reconnect and Refresh.";
            return;
        }

        if (capturing)
        {
            CaptureInput();
            return;
        }

        if (bindingKey < 0 && bindingButton < 0 && bindingAxis < 0)
            return;

        bool down = ReadInput();

        // Ignore the assignment press until the input has been released.
        if (!armed)
        {
            if (!down)
            {
                armed = true;
                previousDown = false;
                stateLabel.Text = "READY";
            }
            return;
        }

        bool pressed = down && !previousDown;
        bool controllerToggle =
            modes.SelectedIndex == 1 &&
            devices.SelectedIndex > 0 &&
            controller != IntPtr.Zero;

        bool nextActive;
        string status;
        Color statusColor;

        if (controllerToggle)
        {
            // SDL controller axes: left X = 0, left Y = 1.
            // Negative Y is forward/up.
            double x = SDL.SDL_GameControllerGetAxis(controller, 0) / 32768.0;
            double y = SDL.SDL_GameControllerGetAxis(controller, 1) / 32768.0;
            double magnitude = Math.Sqrt(x * x + y * y);
            bool neutral = magnitude < NeutralDeadZone;
            long now = inputClock.ElapsedMilliseconds;

            if (pressed)
            {
                if (sprintArmed)
                {
                    sprintArmed = false;
                    pendingStart = false;
                }
                else
                {
                    sprintArmed = true;

                    // Allow L3 slightly before stick movement.
                    pendingStart = neutral;
                    pendingDeadline = now + PendingStartMs;
                }
            }

            if (sprintArmed)
            {
                if (!neutral)
                {
                    pendingStart = false;
                }
                else if (!pendingStart || now >= pendingDeadline)
                {
                    // Neutral clears the remembered sprint toggle.
                    sprintArmed = false;
                    pendingStart = false;
                }
            }

            double forwardThreshold = active
                ? ForwardKeepThreshold
                : ForwardStartThreshold;

            bool forward = !neutral && y < -forwardThreshold;
            nextActive = sprintArmed && forward;

            if (nextActive)
            {
                status = "SPRINT ACTIVE";
                statusColor = Color.ForestGreen;
            }
            else if (sprintArmed)
            {
                status = pendingStart
                    ? "ARMED / MOVE FORWARD"
                    : "ARMED / NOT FORWARD";
                statusColor = Color.DarkGoldenrod;
            }
            else
            {
                status = "READY";
                statusColor = Color.DimGray;
            }

            if (DebugInterfaceVisible) messageLabel.Text = String.Format(
                "Stick X: {0:0.00}   Y: {1:0.00}   Neutral: {2}\n" +
                "Neutral clears sprint. Side/back retains the armed state.",
                x, y, neutral ? "yes" : "no");
        }
        else
        {
            // Keyboard toggle and all Hold bindings retain simple behavior.
            nextActive = modes.SelectedIndex == 0
                ? down
                : (pressed ? !active : active);

            status = nextActive ? "ACTIVE" : "READY";
            statusColor = nextActive ? Color.ForestGreen : Color.DimGray;

            messageLabel.Text =
                "Hold: active while pressed. Toggle: press to start/stop.\n" +
                "Directional tracking currently applies to controller Toggle.";
        }

        active = nextActive;

        previousDown = down;
        stateLabel.Text = status;
        stateLabel.ForeColor = statusColor;
        counterLabel.Text =
            elapsed.Elapsed.TotalSeconds.ToString("0.0") + " s";
    }


    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        CloseController();

        if (sdlReady)
            SDL.SDL_Quit();

        Marshal.FreeHGlobal(eventBuffer);
        base.OnFormClosed(e);
    }

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new InputTest());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Input test failed");
        }
    }
}

public sealed partial class InputTest
{
    private Form debugWindow;
    private bool diagnosticLoggingEnabled;
    private long nextDebugSummaryAt;
    private long nextDebugCaptureStatusAt;
    private readonly CheckBox diagnosticLoggingCheck = new CheckBox();

    private bool DebugInterfaceVisible
    {
        get
        {
            return debugWindow != null &&
                !debugWindow.IsDisposed &&
                debugWindow.Visible &&
                debugWindow.WindowState != FormWindowState.Minimized;
        }
    }

    private bool ShouldRefreshDebugSummary()
    {
        if (!DebugInterfaceVisible)
            return false;

        long now = visionClock.ElapsedMilliseconds;
        if (now < nextDebugSummaryAt)
            return false;

        nextDebugSummaryAt = now + 250;
        return true;
    }

    private bool ShouldRefreshCaptureStatus()
    {
        if (!DebugInterfaceVisible)
            return false;

        long now = visionClock.ElapsedMilliseconds;
        if (now < nextDebugCaptureStatusAt)
            return false;

        nextDebugCaptureStatusAt = now + 250;
        return true;
    }

    private Control RequireMainControl(string text)
    {
        foreach (Control control in Controls)
        {
            if (control.Text == text)
                return control;
        }
        throw new InvalidOperationException(
            "Cannot locate existing UI control: " + text);
    }

    private void MoveDebugControl(
        Control control, int x, int y, int width, int height)
    {
        debugWindow.Controls.Add(control);
        control.SetBounds(x, y, width, height);
    }

    private void LogDebugSnapshot()
    {
        if (!diagnosticLoggingEnabled)
            return;

        if (diagnosticLoggingEnabled) LogVision("Diagnostic logging enabled; earlier events were not collected.");
        if (diagnosticLoggingEnabled) LogVision("Sprint Timer v" + AppVersion +
            " | x64 | EXE: " + Application.ExecutablePath);
        if (diagnosticLoggingEnabled) LogVision(
            "Configuration: monitor: " + captureMonitor.Text +
            "; threshold: " + matchThreshold.Value.ToString("0.000") +
            "; device: " + devices.Text +
            "; mode: " + modes.Text +
            "; " + bindingLabel.Text);
        if (diagnosticLoggingEnabled) LogVision(
            "Reminder at: " + reminderSeconds.Value.ToString("0.0") +
            " s (0 = off); overlay: " +
            (overlayEnabled.Checked ? "on" : "off") +
            "; X/Y: " + overlayX.Value + "/" + overlayY.Value +
            "; opacity: " + overlayOpacity.Value + "%.");
        if (diagnosticLoggingEnabled) LogVision(
            "Current state: " + stateLabel.Text +
            "; elapsed: " +
            ((elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) /
                1000.0).ToString("0.000") +
            " s; track: " +
            (recoveryHasTrack ? recoveryX + "," + recoveryY : "none") +
            "; diagnostic images: " +
            (diagnosticCaptureEnabled ? "on" : "off") + ".");
    }

    private void InitializeDebugInterface()
    {
        // Capture references before reparenting the existing controls.
        Control monitorLabel = RequireMainControl("Capture monitor");
        Control thresholdLabel = RequireMainControl("Threshold");
        Control clear = RequireMainControl("Clear diagnostics");
        Control copy = RequireMainControl("Copy diagnostics");
        Control reset = RequireMainControl("Reset");
        Control captures = RequireMainControl("Open captures");
        Control confirmMonitor = RequireMainControl("Confirm monitor");
        Control reminderTitle =
            RequireMainControl("Reminder at (seconds; 0 = off)");
        Control xTitle = RequireMainControl("X offset");
        Control yTitle = RequireMainControl("Y offset");
        Control opacityTitle = RequireMainControl("Opacity %");

        debugWindow = new Form();
        debugWindow.Text = "Sprint Timer Debug - v" + AppVersion;
        debugWindow.ClientSize = new Size(760, 680);
        debugWindow.FormBorderStyle = FormBorderStyle.FixedDialog;
        debugWindow.MaximizeBox = false;
        debugWindow.MinimizeBox = true;
        debugWindow.StartPosition = FormStartPosition.CenterParent;
        debugWindow.Font = Font;

        diagnosticLoggingCheck.Text = "Enable diagnostic logging";
        diagnosticLoggingCheck.SetBounds(12, 12, 270, 25);
        diagnosticLoggingCheck.Checked = false;
        diagnosticLoggingCheck.CheckedChanged += delegate
        {
            diagnosticLoggingEnabled = diagnosticLoggingCheck.Checked;
            if (diagnosticLoggingEnabled)
                LogDebugSnapshot();
        };
        debugWindow.Controls.Add(diagnosticLoggingCheck);

        Label loggingNotice = new Label();
        loggingNotice.Text =
            "Off at startup. Text stays in memory; use Copy diagnostics to export.";
        loggingNotice.SetBounds(12, 43, 730, 23);
        debugWindow.Controls.Add(loggingNotice);

        MoveDebugControl(thresholdLabel, 12, 79, 80, 23);
        MoveDebugControl(matchThreshold, 95, 76, 95, 26);
        MoveDebugControl(reset, 215, 75, 125, 28);
        reset.Text = "Reset session";

        MoveDebugControl(visionSummary, 12, 115, 730, 112);
        MoveDebugControl(clear, 12, 237, 150, 28);
        MoveDebugControl(copy, 175, 237, 150, 28);
        MoveDebugControl(visionLog, 12, 275, 730, 238);

        MoveDebugControl(diagnosticCaptureCheck, 12, 524, 300, 25);
        MoveDebugControl(captures, 335, 522, 145, 28);
        MoveDebugControl(diagnosticCaptureStatus, 12, 558, 730, 36);
        MoveDebugControl(messageLabel, 12, 605, 730, 60);

        debugWindow.FormClosing += delegate(
            object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                debugWindow.Hide();
            }
        };

        FormClosed += delegate
        {
            if (debugWindow != null)
            {
                debugWindow.Dispose();
                debugWindow = null;
            }
        };

        // The actual controls are retained, preserving settings and handlers.
        monitorLabel.SetBounds(12, 265, 115, 23);
        captureMonitor.SetBounds(130, 262, 545, 26);

        reminderTitle.SetBounds(12, 310, 205, 23);
        reminderSeconds.SetBounds(220, 307, 90, 26);
        overlayEnabled.SetBounds(335, 308, 160, 25);

        xTitle.SetBounds(12, 350, 60, 23);
        overlayX.SetBounds(75, 347, 80, 26);
        yTitle.SetBounds(175, 350, 60, 23);
        overlayY.SetBounds(238, 347, 80, 26);
        opacityTitle.SetBounds(340, 350, 75, 23);
        overlayOpacity.SetBounds(420, 347, 75, 26);
        overlayNotice.SetBounds(12, 389, 675, 50);

        Label usage = new Label();
        usage.Text =
            "Assign sprint, release the input, then focus ARC Raiders.\r\n" +
            "Switching away clears the timer session. No game inputs are generated.";
        usage.SetBounds(12, 444, 675, 43);
        Controls.Add(usage);

        Button debug = new Button();
        debug.Text = "Debug...";
        debug.SetBounds(12, 500, 125, 28);
        debug.Click += delegate
        {
            nextDebugSummaryAt = 0;
            nextDebugCaptureStatusAt = 0;
            if (!debugWindow.Visible)
                debugWindow.Show(this);
            if (debugWindow.WindowState == FormWindowState.Minimized)
                debugWindow.WindowState = FormWindowState.Normal;
            debugWindow.Activate();
        };
        Controls.Add(debug);

        Label version = new Label();
        version.Text = "v" + AppVersion + " | x64";
        version.SetBounds(155, 505, 150, 23);
        Controls.Add(version);

        // Keep missing-monitor recovery available without opening Debug.
        confirmMonitor.SetBounds(500, 500, 175, 28);

        Text = "Sprint Timer - v" + AppVersion;
        ClientSize = new Size(700, 545);
    }
}

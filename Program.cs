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
    private const string AppVersion = "0.2.11";

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
        InitializeMonitorPlacementDefaults();
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
        bindingLabel.Text = "Not assigned";

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
        bindingLabel.Text = FriendlySprintBinding(name);
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
            "; sprint input: " + bindingLabel.Text);
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

        InitializeCompactInterface(
            monitorLabel, confirmMonitor, reminderTitle,
            xTitle, yTitle, opacityTitle);
    }
}


public sealed partial class InputTest
{
    private bool quickStartAcknowledged;

    private static Label UiLabel(string text)
    {
        Label label = new Label();
        label.Text = text;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 3, 8, 3);
        return label;
    }

    private static FlowLayoutPanel UiFlow(params Control[] controls)
    {
        FlowLayoutPanel panel = new FlowLayoutPanel();
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.WrapContents = false;
        panel.Margin = Padding.Empty;
        panel.Dock = DockStyle.Fill;
        foreach (Control control in controls)
        {
            control.Margin = new Padding(0, 3, 8, 3);
            panel.Controls.Add(control);
        }
        return panel;
    }

    private static TableLayoutPanel UiSection(string title)
    {
        TableLayoutPanel table = new TableLayoutPanel();
        table.AutoSize = true;
        table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        table.Dock = DockStyle.Top;
        table.ColumnCount = 3;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        table.Margin = new Padding(0, 8, 0, 8);
        table.Padding = new Padding(0, 4, 0, 4);

        Label heading = UiLabel(title);
        heading.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        heading.ForeColor = Color.FromArgb(45, 76, 105);
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 3);

        Panel divider = new Panel();
        divider.BackColor = Color.FromArgb(215, 221, 228);
        divider.Dock = DockStyle.Fill;
        divider.Margin = new Padding(0, 0, 0, 5);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        table.Controls.Add(divider, 0, 1);
        table.SetColumnSpan(divider, 3);
        table.RowCount = 2;
        return table;
    }

    private static void UiRow(
        TableLayoutPanel table, Control label, Control value, Control action)
    {
        int row = table.RowCount++;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 3, 8, 3);
        table.Controls.Add(label, 0, row);

        value.Anchor = AnchorStyles.Left;
        value.Margin = new Padding(0, 2, 8, 2);
        table.Controls.Add(value, 1, row);

        if (action == null)
        {
            table.SetColumnSpan(value, 2);
        }
        else
        {
            action.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            action.Margin = new Padding(0, 2, 0, 2);
            action.Height = 28;
            table.Controls.Add(action, 2, row);
        }
    }

    private void InitializeCompactInterface(
        Control monitorLabel, Control confirmMonitor, Control reminderTitle,
        Control xTitle, Control yTitle, Control opacityTitle)
    {
        Control deviceLabel = RequireMainControl("Input device");
        Control refresh = RequireMainControl("Refresh");
        Control assign = RequireMainControl("Assign sprint input");

        SuspendLayout();

        // Remove parent relationships only; retained controls are not disposed.
        Controls.Clear();
        BackColor = Color.FromArgb(248, 249, 251);
        AutoScroll = false;
        ClientSize = new Size(380, 665);
        Text = "Sprint Timer - v" + AppVersion;

        TableLayoutPanel root = new TableLayoutPanel();
        root.ColumnCount = 1;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.AutoSize = true;
        root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        root.Dock = DockStyle.Top;
        root.Padding = new Padding(16, 14, 16, 14);
        root.Margin = Padding.Empty;
        Controls.Add(root);

        FlowLayoutPanel title = UiFlow();
        Label name = UiLabel("Sprint Timer");
        name.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        title.Controls.Add(name);
        title.Controls.Add(UiLabel("v" + AppVersion + " | x64"));
        root.Controls.Add(title);

        TableLayoutPanel input = UiSection("INPUT");
        deviceLabel.Text = "Device";
        devices.Width = 140;
        devices.DropDownWidth = 320;
        UiRow(input, deviceLabel, devices, refresh);
        assign.Text = "Assign...";
        bindingLabel.AutoSize = false;
        bindingLabel.Height = 36;
        bindingLabel.Width = 156;
        bindingLabel.TextAlign = ContentAlignment.MiddleLeft;
        UiRow(input, UiLabel("Sprint input"), bindingLabel, assign);
        modes.Width = 110;
        UiRow(input, UiLabel("Mode"), UiFlow(modes), null);
        root.Controls.Add(input);

        TableLayoutPanel display = UiSection("DISPLAY");
        monitorLabel.Text = "Game monitor";
        captureMonitor.Width = 200;
        captureMonitor.DropDownWidth = 340;
        captureMonitor.FormattingEnabled = true;
        captureMonitor.Format += delegate(
            object sender, ListControlConvertEventArgs e)
        {
            int index = captureMonitor.Items.IndexOf(e.ListItem);
            Screen[] screens = Screen.AllScreens;
            if (index >= 0 && index < screens.Length)
            {
                Screen screen = screens[index];
                e.Value = PhysicalMonitorDisplayLabel(screen);
            }
        };
        captureMonitor.Refresh();
        UiRow(display, monitorLabel, captureMonitor, null);

        int confirmRow = display.RowCount++;
        confirmMonitor.Width = 170;
        confirmMonitor.Height = 28;
        confirmMonitor.Margin = new Padding(0, 4, 0, 4);
        display.Controls.Add(confirmMonitor, 1, confirmRow);
        display.SetColumnSpan(confirmMonitor, 2);

        overlayEnabled.AutoSize = true;
        UiRow(display, UiLabel("Overlay"), overlayEnabled, null);
        root.Controls.Add(display);

        TableLayoutPanel overlay = UiSection("OVERLAY");
        reminderTitle.Text = "Reminder at";
        reminderSeconds.Width = 78;
        UiRow(overlay, reminderTitle,
            UiFlow(reminderSeconds, UiLabel("seconds; 0 = off")), null);

        xTitle.Text = "X position";
        yTitle.Text = "Y position";
        xTitle.AutoSize = true;
        yTitle.AutoSize = true;
        overlayX.Width = 78;
        overlayY.Width = 78;
        UiRow(overlay, xTitle,
            UiFlow(overlayX, UiLabel("pixels")), null);
        UiRow(overlay, yTitle,
            UiFlow(overlayY, UiLabel("pixels")), null);

        opacityTitle.Text = "Opacity";
        overlayOpacity.Width = 78;
        UiRow(overlay, opacityTitle,
            UiFlow(overlayOpacity, UiLabel("%")), null);
        root.Controls.Add(overlay);

        overlayNotice.AutoSize = true;
        overlayNotice.MaximumSize = new Size(348, 0);
        overlayNotice.Dock = DockStyle.Fill;
        overlayNotice.ForeColor = Color.DimGray;
        overlayNotice.Margin = new Padding(0, 0, 0, 10);
        root.Controls.Add(overlayNotice);

        TableLayoutPanel status = UiSection("STATUS");
        stateLabel.AutoSize = false;
        stateLabel.Height = 34;
        stateLabel.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        stateLabel.TextAlign = ContentAlignment.MiddleLeft;
        stateLabel.Dock = DockStyle.Fill;
        stateLabel.Margin = new Padding(0, 4, 0, 0);
        status.Controls.Add(stateLabel, 0, 2);
        status.SetColumnSpan(stateLabel, 3);

        counterLabel.AutoSize = false;
        counterLabel.Height = 27;
        counterLabel.Font = new Font("Segoe UI", 11);
        counterLabel.TextAlign = ContentAlignment.MiddleLeft;
        counterLabel.Dock = DockStyle.Fill;
        status.Controls.Add(counterLabel, 0, 3);
        status.SetColumnSpan(counterLabel, 3);

        status.RowCount = 4;
        root.Controls.Add(status);

        Button debug = new Button();
        debug.Text = "Debug...";
        debug.Size = new Size(88, 29);
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

        Button help = new Button();
        help.Text = "Help...";
        help.Size = new Size(88, 29);
        help.Click += delegate { ShowQuickStart(); };

        Button minimize = new Button();
        minimize.Text = "Minimize";
        minimize.Size = new Size(96, 29);
        minimize.Click += delegate
        {
            WindowState = FormWindowState.Minimized;
        };

        FlowLayoutPanel footer = UiFlow(help, debug, minimize);
        footer.Margin = new Padding(0, 6, 0, 0);
        root.Controls.Add(footer);

        Shown += delegate
        {
            FitCompactWindow(root);
            if (!quickStartAcknowledged)
                BeginInvoke(new MethodInvoker(delegate { ShowQuickStart(); }));
        };

        ResumeLayout(true);
        FitCompactWindow(root);
    }

    private void PersistQuickStartAcknowledgement()
    {
        string path = SettingsFile();
        System.Collections.Generic.Dictionary<string, object> data;

        if (System.IO.File.Exists(path))
        {
            data = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                    System.IO.File.ReadAllText(path));
            if (data == null ||
                ReadSettingInt(data, "version", 0, 0, 100) != 1)
                throw new InvalidOperationException(
                    "Cannot update an unsupported settings file.");
        }
        else
        {
            // Establish the existing settings format using the existing saver.
            SavePersistentSettings();
            data = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                    System.IO.File.ReadAllText(path));
        }

        // Preserve existing monitor/input settings, even if a monitor is missing.
        data["quick_start_acknowledged"] = true;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(
                temporary,
                new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Serialize(data));
            System.IO.File.Replace(temporary, path, path + ".bak");
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }

    private static string FriendlySprintBinding(string name)
    {
        switch (name)
        {
            case "LShiftKey": return "Left Shift";
            case "RShiftKey": return "Right Shift";
            case "ShiftKey": return "Shift";
            case "LControlKey": return "Left Ctrl";
            case "RControlKey": return "Right Ctrl";
            case "ControlKey": return "Ctrl";
            case "LMenu": return "Left Alt";
            case "RMenu": return "Right Alt";
            case "Menu": return "Alt";
            case "Space": return "Space";
            case "Capital": return "Caps Lock";
            case "Return": return "Enter";
            case "Prior": return "Page Up";
            case "Next": return "Page Down";
            case "Back": return "Backspace";
            default: return name;
        }
    }

    private void FitCompactWindow(TableLayoutPanel root)
    {
        // Fit the settings content instead of hiding it behind a scrollbar.
        root.Width = ClientSize.Width;
        root.PerformLayout();
        int height = root.GetPreferredSize(
            new Size(ClientSize.Width, 0)).Height;
        ClientSize = new Size(ClientSize.Width, height + 4);
        PerformLayout();
    }

    private static void HelpHeading(
        RichTextBox text, string heading, Font headingFont)
    {
        text.SelectionStart = text.TextLength;
        text.SelectionFont = headingFont;
        text.SelectionColor = Color.FromArgb(45, 76, 105);
        text.AppendText(heading + "\r\n");
    }

    private static void HelpSetting(
        RichTextBox text, string name, string explanation,
        Font bodyFont, Font boldFont)
    {
        text.SelectionStart = text.TextLength;
        text.SelectionFont = boldFont;
        text.SelectionColor = Color.FromArgb(30, 34, 39);
        text.AppendText(name + "\r\n");
        text.SelectionFont = bodyFont;
        text.SelectionColor = Color.FromArgb(60, 65, 72);
        text.AppendText(explanation + "\r\n\r\n");
    }

    private void ShowQuickStart()
    {
        using (Font bodyFont = new Font("Segoe UI", 10))
        using (Font boldFont = new Font("Segoe UI", 10, FontStyle.Bold))
        using (Font headingFont = new Font("Segoe UI", 12, FontStyle.Bold))
        using (Form help = new Form())
        {
            help.Text = "Sprint Timer - Quick Start";
            help.Font = bodyFont;
            help.BackColor = Color.White;
            help.ClientSize = new Size(580, 630);
            help.FormBorderStyle = FormBorderStyle.FixedDialog;
            help.MaximizeBox = false;
            help.MinimizeBox = false;
            help.StartPosition = FormStartPosition.CenterParent;
            help.ShowInTaskbar = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20);
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            help.Controls.Add(layout);

            Label intro = new Label();
            intro.Text =
                "Set up once, then minimize\r\n" +
                "Use Help whenever you need a reminder about a setting.";
            intro.Font = headingFont;
            intro.ForeColor = Color.FromArgb(35, 45, 55);
            intro.Dock = DockStyle.Fill;
            layout.Controls.Add(intro, 0, 0);

            RichTextBox text = new RichTextBox();
            text.Font = bodyFont;
            text.ReadOnly = true;
            text.DetectUrls = false;
            text.BorderStyle = BorderStyle.None;
            text.BackColor = Color.White;
            text.Dock = DockStyle.Fill;
            text.ScrollBars = RichTextBoxScrollBars.Vertical;
            text.TabStop = false;
            text.WordWrap = true;
            layout.Controls.Add(text, 0, 1);

            HelpHeading(text, "INPUT", headingFont);
            HelpSetting(text, "Device and Refresh",
                "Choose your keyboard or controller. Refresh rescans connected devices.",
                bodyFont, boldFont);
            HelpSetting(text, "Sprint input and Assign",
                "Click Assign, press your sprint key or controller button, then release it.",
                bodyFont, boldFont);
            HelpSetting(text, "Hold or Toggle",
                "Match your in-game sprint mode. Controller Toggle also observes " +
                "left-stick direction. Returning the stick to neutral clears its " +
                "remembered sprint state.",
                bodyFont, boldFont);

            HelpHeading(text, "DISPLAY", headingFont);
            HelpSetting(text, "Game monitor",
                "Select the display showing ARC Raiders. The app does not " +
                "automatically choose the game monitor.",
                bodyFont, boldFont);
            HelpSetting(text, "Show overlay",
                "Show or hide the timer display. Hiding it does not disable monitoring.",
                bodyFont, boldFont);

            HelpHeading(text, "OVERLAY", headingFont);
            HelpSetting(text, "Reminder at",
                "Flash the overlay after this many elapsed seconds. Set 0 to disable. " +
                "The reminder does not determine when the benefit ends.",
                bodyFont, boldFont);
            HelpSetting(text, "Position",
                "X and Y are offsets from the selected monitor's top-left corner. " +
                "Manually changing monitors resets the overlay to horizontal center " +
                "and 14 percent down. Saved placement is retained at startup. " +
                "The overlay hides if it overlaps the detector band.",
                bodyFont, boldFont);
            HelpSetting(text, "Opacity",
                "Adjust how transparent the overlay appears.",
                bodyFont, boldFont);

            HelpHeading(text, "HELP, DEBUG, AND MINIMIZE", headingFont);
            HelpSetting(text, "Help",
                "Reopen this guide at any time.",
                bodyFont, boldFont);
            HelpSetting(text, "Debug",
                "Recognition settings, session reset, counters, and troubleshooting. " +
                "Text logging and diagnostic-image capture are separate. Both start off.",
                bodyFont, boldFont);
            HelpSetting(text, "Minimize and Close",
                "Minimize keeps monitoring and the overlay running. Closing the main " +
                "window saves settings and exits.",
                bodyFont, boldFont);

            HelpHeading(text, "HOW MONITORING WORKS", headingFont);
            HelpSetting(text, "The icon decides; the timer advises",
                "Sprint input can start an estimate before the ability icon appears. " +
                "Confirmed icon detection then controls benefit timing.",
                bodyFont, boldFont);
            HelpSetting(text, "ARC Raiders must be foreground",
                "Switching away from the game clears the current timer session. " +
                "Opening the app's Help or Debug windows also takes focus away.",
                bodyFont, boldFont);

            HelpHeading(text, "ANTI-CHEAT LIMITATION", headingFont);
            HelpSetting(text, "Approval has not been established",
                "The app observes screen and input only and does not generate game " +
                "input. That is not a guarantee of anti-cheat approval or ban safety.",
                bodyFont, boldFont);

            text.Select(0, 0);
            text.ScrollToCaret();

            Button gotIt = new Button();
            gotIt.Text = "Got it";
            gotIt.Size = new Size(100, 30);
            gotIt.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            gotIt.Click += delegate
            {
                try
                {
                    if (!quickStartAcknowledged)
                    {
                        PersistQuickStartAcknowledgement();
                        quickStartAcknowledged = true;
                    }
                    help.DialogResult = DialogResult.OK;
                    help.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        help,
                        "Could not save Quick Start acknowledgement:\r\n" +
                        ex.Message + "\r\n\r\nYou can close this guide with X.",
                        "Settings save failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            layout.Controls.Add(gotIt, 0, 2);
            help.AcceptButton = gotIt;
            help.ShowDialog(this);
        }
    }
}


public sealed partial class InputTest
{
    // Full DEVMODEW layout, including the display branch of its union.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorDisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport(
        "user32.dll",
        EntryPoint = "EnumDisplaySettingsW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadMonitorDisplayMode(
        string deviceName, int modeNumber, ref MonitorDisplayMode mode);

    private static string PhysicalMonitorDisplayLabel(Screen screen)
    {
        string name = screen.DeviceName.Replace(@"\\.\DISPLAY", "Display ");

        try
        {
            MonitorDisplayMode mode = new MonitorDisplayMode();
            int size = Marshal.SizeOf(typeof(MonitorDisplayMode));
            if (size != 220)
                return name + " - resolution unavailable";

            mode.dmSize = (ushort)size;
            if (ReadMonitorDisplayMode(screen.DeviceName, -1, ref mode) &&
                mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                return name + " - " +
                    mode.dmPelsWidth + " x " + mode.dmPelsHeight;
            }
        }
        catch
        {
            // Do not mislabel logical bounds as a physical resolution.
        }

        return name + " - resolution unavailable";
    }

    private void InitializeMonitorPlacementDefaults()
    {
        committedCaptureMonitor = captureMonitor.SelectedIndex;
        monitorSelectionReady = true;

        monitorPreviewEdges = new MonitorPreviewEdge[] {
            new MonitorPreviewEdge(), new MonitorPreviewEdge(),
            new MonitorPreviewEdge(), new MonitorPreviewEdge()
        };
        foreach (MonitorPreviewEdge edge in monitorPreviewEdges)
            edge.PreparePreview();

        monitorPreviewTimer = new Timer();
        monitorPreviewTimer.Interval = 40;
        monitorPreviewTimer.Tick += delegate
        {
            UpdateMonitorHoverPreview();
        };

        captureMonitor.DropDown += delegate
        {
            monitorPreviewTimer.Start();
        };

        captureMonitor.DropDownClosed += delegate
        {
            StopMonitorHoverPreview();

            // Defer restoration until selection/cancellation notifications
            // for this dropdown operation have finished.
            BeginInvoke(new MethodInvoker(delegate
            {
                if (!IsDisposed && !captureMonitor.IsDisposed &&
                    captureMonitor.SelectedIndex != committedCaptureMonitor)
                    captureMonitor.SelectedIndex = committedCaptureMonitor;
            }));
        };

        captureMonitor.SelectionChangeCommitted += delegate
        {
            int selected = captureMonitor.SelectedIndex;
            if (selected < 0 || selected >= Screen.AllScreens.Length)
                return;

            bool changed = selected != committedCaptureMonitor;
            committedCaptureMonitor = selected;
            StopMonitorHoverPreview();

            if (changed)
            {
                ResetVisionSession();
                ApplySelectedMonitorOverlayDefaults();
            }
        };

        FormClosed += delegate
        {
            if (monitorPreviewTimer != null)
            {
                monitorPreviewTimer.Stop();
                monitorPreviewTimer.Dispose();
                monitorPreviewTimer = null;
            }

            if (monitorPreviewEdges != null)
            {
                foreach (MonitorPreviewEdge edge in monitorPreviewEdges)
                    edge.Dispose();
                monitorPreviewEdges = null;
            }
        };
    }

    private void ApplySelectedMonitorOverlayDefaults()
    {
        Screen[] screens = Screen.AllScreens;
        int index = ActiveCaptureMonitorIndex;
        if (index < 0 || index >= screens.Length ||
            reminderOverlay == null || reminderOverlay.IsDisposed)
            return;

        // Match the existing overlay coordinate system, not the cosmetic
        // physical-resolution label.
        Rectangle bounds = screens[index].Bounds;
        int maxX = Math.Max(0, bounds.Width - reminderOverlay.Width);
        int maxY = Math.Max(0, bounds.Height - reminderOverlay.Height);

        int x = (int)Math.Round(
            maxX / 2.0, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(
            bounds.Height * 0.14, MidpointRounding.AwayFromZero);
        y = Math.Max(0, Math.Min(maxY, y));

        overlayX.Value = Math.Max(
            overlayX.Minimum, Math.Min(overlayX.Maximum, (decimal)x));
        overlayY.Value = Math.Max(
            overlayY.Minimum, Math.Min(overlayY.Maximum, (decimal)y));

        if (diagnosticLoggingEnabled)
        {
            LogVision(
                "Monitor selection committed; overlay defaults applied. " +
                "Coordinate bounds: " + bounds.Width + " x " + bounds.Height +
                "; X/Y: " + overlayX.Value + "/" + overlayY.Value + ".");
        }
    }
}


public sealed partial class InputTest
{
    private bool monitorSelectionReady;
    private int committedCaptureMonitor = -1;
    private Timer monitorPreviewTimer;
    private MonitorPreviewEdge[] monitorPreviewEdges;
    private Rectangle monitorPreviewBounds = Rectangle.Empty;

    private int ActiveCaptureMonitorIndex
    {
        get
        {
            return monitorSelectionReady
                ? committedCaptureMonitor : captureMonitor.SelectedIndex;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorPreviewRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorPreviewComboInfo
    {
        public int Size;
        public MonitorPreviewRect Item;
        public MonitorPreviewRect Button;
        public uint ButtonState;
        public IntPtr Combo;
        public IntPtr Edit;
        public IntPtr List;
    }

    [DllImport("user32.dll", EntryPoint = "GetComboBoxInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviewComboInfo(
        IntPtr combo, ref MonitorPreviewComboInfo info);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviewCursorPosition(out Point point);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviewScreenToClient(
        IntPtr window, ref Point point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr PreviewListMessage(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private void StopMonitorHoverPreview()
    {
        if (monitorPreviewTimer != null)
            monitorPreviewTimer.Stop();
        HideMonitorHoverBorder();
    }

    private void HideMonitorHoverBorder()
    {
        if (monitorPreviewEdges != null)
        {
            foreach (MonitorPreviewEdge edge in monitorPreviewEdges)
            {
                if (!edge.IsDisposed)
                    edge.HidePreview();
            }
        }
        monitorPreviewBounds = Rectangle.Empty;
    }

    private void UpdateMonitorHoverPreview()
    {
        if (!captureMonitor.DroppedDown ||
            WindowState == FormWindowState.Minimized)
        {
            StopMonitorHoverPreview();
            return;
        }

        MonitorPreviewComboInfo info = new MonitorPreviewComboInfo();
        info.Size = Marshal.SizeOf(typeof(MonitorPreviewComboInfo));
        Point point;

        if (!PreviewComboInfo(captureMonitor.Handle, ref info) ||
            info.List == IntPtr.Zero ||
            !PreviewCursorPosition(out point) ||
            !PreviewScreenToClient(info.List, ref point))
        {
            HideMonitorHoverBorder();
            return;
        }

        int packed = unchecked(
            (point.X & 0xffff) | ((point.Y & 0xffff) << 16));
        long hit = PreviewListMessage(
            info.List, 0x01A9, IntPtr.Zero, new IntPtr(packed)).ToInt64();

        // LB_ITEMFROMPOINT: high word is nonzero outside the list.
        int index = (int)(hit & 0xffff);
        bool outside = ((hit >> 16) & 0xffff) != 0;
        Screen[] screens = Screen.AllScreens;

        if (outside || index < 0 || index >= screens.Length ||
            index >= captureMonitor.Items.Count)
        {
            HideMonitorHoverBorder();
            return;
        }

        ShowMonitorHoverBorder(screens[index].Bounds);
    }

    private void ShowMonitorHoverBorder(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            HideMonitorHoverBorder();
            return;
        }

        if (monitorPreviewEdges == null)
            return;

        if (monitorPreviewBounds == bounds &&
            monitorPreviewEdges[0].PreviewVisible)
            return;

        int thickness = Math.Min(
            12, Math.Min(bounds.Width / 2, bounds.Height / 2));

        Rectangle[] edges = {
            new Rectangle(bounds.Left, bounds.Top,
                bounds.Width, thickness),
            new Rectangle(bounds.Left, bounds.Bottom - thickness,
                bounds.Width, thickness),
            new Rectangle(bounds.Left, bounds.Top + thickness,
                thickness, Math.Max(1, bounds.Height - thickness * 2)),
            new Rectangle(bounds.Right - thickness, bounds.Top + thickness,
                thickness, Math.Max(1, bounds.Height - thickness * 2))
        };

        for (int i = 0; i < monitorPreviewEdges.Length; i++)
        {
            if (!monitorPreviewEdges[i].ShowPreview(edges[i]))
            {
                StopMonitorHoverPreview();
                if (diagnosticLoggingEnabled)
                    LogVision("Monitor border could not be displayed; preview stopped.");
                return;
            }
        }
        monitorPreviewBounds = bounds;
    }
}

internal sealed class MonitorPreviewEdge : Form
{
    internal MonitorPreviewEdge()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Color.Yellow;
        Opacity = 1.0;
    }

    private bool previewPrepared;
    private bool previewVisible;

    internal bool PreviewVisible
    {
        get { return previewVisible; }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionPreviewWindow(
        IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll",
        EntryPoint = "SetLayeredWindowAttributes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPreviewAlpha(
        IntPtr window, uint colorKey, byte alpha, uint flags);

    internal void PreparePreview()
    {
        // Access creates the hidden native handle. No owner or Show call.
        IntPtr window = Handle;
        // A layered window needs its alpha initialized before native display.
        previewPrepared = SetPreviewAlpha(window, 0, 255, 0x00000002);
    }

    internal bool ShowPreview(Rectangle bounds)
    {
        if (IsDisposed || !previewPrepared || !IsHandleCreated)
            return false;

        // TOPMOST; NOACTIVATE | SHOWWINDOW | NOOWNERZORDER.
        bool shown = PositionPreviewWindow(
            Handle, new IntPtr(-1),
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            0x00000010 | 0x00000040 | 0x00000200);

        previewVisible = shown;
        return shown;
    }

    internal void HidePreview()
    {
        if (!IsDisposed && IsHandleCreated && previewVisible)
        {
            // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | HIDEWINDOW.
            bool hidden = PositionPreviewWindow(
                Handle, IntPtr.Zero, 0, 0, 0, 0,
                0x00000001 | 0x00000002 | 0x00000004 |
                0x00000010 | 0x00000080);
            if (hidden)
                previewVisible = false;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW
            cp.ExStyle |= 0x00080000 | 0x00000020 |
                          0x08000000 | 0x00000080;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            m.Result = new IntPtr(3); // MA_NOACTIVATE
            return;
        }
        if (m.Msg == 0x0084) // WM_NCHITTEST
        {
            m.Result = new IntPtr(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref m);
    }
}

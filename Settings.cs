using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public sealed partial class InputTest
{
    private string settingsWarning = "";
    private bool settingsInitialized;
    private bool savedMonitorNeedsConfirmation;

    private static string SettingsFile()
    {
        return Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.json"
        );
    }

    private void AddSettingsWarning(string text)
    {
        if (settingsWarning.Length > 0)
            settingsWarning += Environment.NewLine;
        settingsWarning += text;
    }

    private static string ReadSettingString(
        Dictionary<string, object> data, string key, string fallback)
    {
        object value;
        return data.TryGetValue(key, out value) && value is string
            ? (string)value : fallback;
    }

    private static int ReadSettingInt(
        Dictionary<string, object> data, string key,
        int fallback, int minimum, int maximum)
    {
        object value;
        if (!data.TryGetValue(key, out value) || value == null)
            return fallback;

        try
        {
            decimal number = Convert.ToDecimal(
                value, CultureInfo.InvariantCulture
            );
            if (number != Decimal.Truncate(number) ||
                number < minimum || number > maximum)
                return fallback;

            return (int)number;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadSettingBool(
        Dictionary<string, object> data, string key, bool fallback)
    {
        object value;
        return data.TryGetValue(key, out value) && value is bool
            ? (bool)value : fallback;
    }

    private static void RestoreNumber(
        Dictionary<string, object> data, string key, NumericUpDown control)
    {
        object value;
        if (!data.TryGetValue(key, out value) || value == null)
            return;

        try
        {
            decimal number = Convert.ToDecimal(
                value, CultureInfo.InvariantCulture
            );
            control.Value = Math.Max(
                control.Minimum, Math.Min(control.Maximum, number)
            );
        }
        catch
        {
            // Leave the current default when this setting is invalid.
        }
    }

    private void InitializePersistentSettings()
    {
        // This confirmation applies only when a saved monitor cannot be found.
        Button confirmMonitor = new Button();
        confirmMonitor.Text = "Confirm monitor";
        confirmMonitor.SetBounds(500, 788, 150, 28);
        confirmMonitor.Visible = false;
        Controls.Add(confirmMonitor);

        confirmMonitor.Click += delegate
        {
            if (captureMonitor.SelectedIndex < 0 ||
                captureMonitor.SelectedIndex >= Screen.AllScreens.Length)
                return;

            savedMonitorNeedsConfirmation = false;
            timer.Start();
            confirmMonitor.Visible = false;
            ResetVisionSession();
            if (diagnosticLoggingEnabled) LogVision("Capture monitor explicitly confirmed.");
        };

        string path = SettingsFile();

        if (File.Exists(path))
        {
            try
            {
                Dictionary<string, object> data =
                    new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(path)
                    );

                if (data == null ||
                    ReadSettingInt(data, "version", 0, 0, 100) != 1)
                    throw new Exception("Unsupported settings format.");

                quickStartAcknowledged =
                    ReadSettingBool(data, "quick_start_acknowledged", false);

                modes.SelectedIndex =
                    ReadSettingInt(data, "mode", 0, 0, 1);

                RestoreNumber(data, "threshold", matchThreshold);
                RestoreNumber(data, "reminder_seconds", reminderSeconds);
                swiftRank.SelectedIndex = ReadSettingInt(data, "swift_start_rank", 5, 1, 5) - 1;
                RestoreNumber(data, "warning_lead_seconds", warningLead);
                reminderMode.SelectedIndex = ReadSettingInt(data, "reminder_mode", 0, 0, 1);
                RefreshRankReminder();
                RestoreNumber(data, "overlay_x", overlayX);
                RestoreNumber(data, "overlay_y", overlayY);
                RestoreNumber(data, "overlay_opacity", overlayOpacity);

                overlayEnabled.Checked =
                    ReadSettingBool(data, "overlay_enabled", true);

                string monitorName =
                    ReadSettingString(data, "monitor_device", "");

                if (monitorName.Length > 0)
                {
                    int match = -1;
                    Screen[] screens = Screen.AllScreens;

                    for (int i = 0; i < screens.Length; i++)
                    {
                        if (String.Equals(
                            screens[i].DeviceName, monitorName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            match = i;
                            break;
                        }
                    }

                    if (match >= 0)
                    {
                        captureMonitor.SelectedIndex = match;

                        Rectangle bounds = screens[match].Bounds;
                        int oldWidth = ReadSettingInt(
                            data, "monitor_width", bounds.Width, 1, 100000
                        );
                        int oldHeight = ReadSettingInt(
                            data, "monitor_height", bounds.Height, 1, 100000
                        );

                        if (bounds.Width != oldWidth ||
                            bounds.Height != oldHeight)
                        {
                            AddSettingsWarning(
                                "Capture monitor resolution changed. " +
                                "Verify recognition and overlay placement."
                            );
                        }
                    }
                    else
                    {
                        savedMonitorNeedsConfirmation = true;
                        AddSettingsWarning(
                            "Saved capture monitor is unavailable. " +
                            "Detection is paused. Select the correct monitor, " +
                            "then click Confirm monitor."
                        );
                    }
                }

                string inputKind =
                    ReadSettingString(data, "input_kind", "keyboard");

                int selectedDevice = 0;
                bool deviceAvailable = true;

                if (inputKind == "controller")
                {
                    string name =
                        ReadSettingString(data, "controller_name", "");

                    int matches = 0;
                    for (int i = 1; i < devices.Items.Count; i++)
                    {
                        Device device = devices.Items[i] as Device;
                        if (device != null &&
                            String.Equals(device.Name, name,
                                StringComparison.Ordinal))
                        {
                            matches++;
                            selectedDevice = i;
                        }
                    }

                    if (matches != 1)
                    {
                        selectedDevice = 0;
                        deviceAvailable = false;
                        AddSettingsWarning(
                            "Saved controller is missing or its name is " +
                            "ambiguous. Select a device and reassign sprint."
                        );
                    }
                }
                else if (inputKind != "keyboard")
                {
                    deviceAvailable = false;
                    AddSettingsWarning(
                        "Saved input type is invalid. Reassign sprint."
                    );
                }

                devices.SelectedIndex = selectedDevice;

                if (selectedDevice > 0 && controller == IntPtr.Zero)
                {
                    deviceAvailable = false;
                    AddSettingsWarning(
                        "Saved controller could not be opened. " +
                        "Refresh devices and reassign sprint."
                    );
                }

                bindingKey = -1;
                bindingButton = -1;
                bindingAxis = -1;

                if (deviceAvailable)
                {
                    if (selectedDevice == 0)
                    {
                        bindingKey = ReadSettingInt(
                            data, "binding_key", -1, 8, 254
                        );

                        if (bindingKey == 16 ||
                            bindingKey == 17 || bindingKey == 18)
                            bindingKey = -1;
                    }
                    else
                    {
                        bindingButton = ReadSettingInt(
                            data, "binding_button", -1,
                            0, ButtonNames.Length - 1
                        );

                        if (bindingButton < 0)
                            bindingAxis = ReadSettingInt(
                                data, "binding_axis", -1, 4, 5
                            );
                    }
                }

                ResetState();

                string binding = null;
                if (bindingKey >= 0)
                    binding = ((Keys)bindingKey).ToString();
                else if (bindingButton >= 0)
                    binding = ButtonNames[bindingButton];
                else if (bindingAxis >= 0)
                    binding = bindingAxis == 4
                        ? "Left trigger" : "Right trigger";

                if (binding != null)
                {
                    bindingLabel.Text = FriendlySprintBinding(binding);
                    stateLabel.Text = "RELEASE TO ARM";
                }
                else
                {
                    bindingLabel.Text = "Not assigned";
                }

                // Keep the restored overlay within the selected monitor.
                int monitor = ActiveCaptureMonitorIndex;
                if (monitor >= 0 && monitor < Screen.AllScreens.Length)
                {
                    Rectangle bounds = Screen.AllScreens[monitor].Bounds;
                    overlayX.Value = Math.Min(
                        overlayX.Value,
                        Math.Max(0, bounds.Width - reminderOverlay.Width)
                    );
                    overlayY.Value = Math.Min(
                        overlayY.Value,
                        Math.Max(0, bounds.Height - reminderOverlay.Height)
                    );
                }

                if (diagnosticLoggingEnabled) LogVision("App settings restored from settings.json.");
            }
            catch (Exception ex)
            {
                AddSettingsWarning(
                    "Settings could not be fully restored: " + ex.Message +
                    Environment.NewLine +
                    "Check input, capture monitor, threshold and reminder settings."
                );
                if (diagnosticLoggingEnabled) LogVision("Settings restore error: " + ex.Message);
            }
        }
        else
        {
            if (diagnosticLoggingEnabled) LogVision("No saved app settings; using current defaults.");
        }

        settingsInitialized = true;

        Shown += delegate
        {
            // Program starts the timer after initialization, so pause here.
            if (savedMonitorNeedsConfirmation)
            {
                timer.Stop();
                confirmMonitor.Visible = true;
                if (reminderOverlay != null)
                    reminderOverlay.Hide();
            }

            if (settingsWarning.Length > 0)
                MessageBox.Show(
                    this, settingsWarning, "Check restored settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning
                );
        };
    }

    private void SavePersistentSettings()
    {
        Dictionary<string, object> data =
            new Dictionary<string, object>();

        data["version"] = 1;
        data["quick_start_acknowledged"] = quickStartAcknowledged;
        data["mode"] = modes.SelectedIndex;
        data["input_kind"] =
            devices.SelectedIndex > 0 ? "controller" : "keyboard";

        Device selected = devices.SelectedItem as Device;
        data["controller_name"] = selected != null ? selected.Name : "";

        data["binding_key"] = bindingKey;
        data["binding_button"] = bindingButton;
        data["binding_axis"] = bindingAxis;

        int monitor = ActiveCaptureMonitorIndex;

        if (monitor >= 0 && monitor < Screen.AllScreens.Length)
        {
            Screen screen = Screen.AllScreens[monitor];
            data["monitor_device"] = screen.DeviceName;
            data["monitor_width"] = screen.Bounds.Width;
            data["monitor_height"] = screen.Bounds.Height;
        }

        data["threshold"] = matchThreshold.Value;
        data["reminder_seconds"] = reminderSeconds.Value;
        data["reminder_mode"] = reminderMode.SelectedIndex;
        data["swift_start_rank"] = swiftRank.SelectedIndex + 1;
        data["warning_lead_seconds"] = warningLead.Value;
        data["overlay_enabled"] = overlayEnabled.Checked;
        data["overlay_x"] = overlayX.Value;
        data["overlay_y"] = overlayY.Value;
        data["overlay_opacity"] = overlayOpacity.Value;

        string path = SettingsFile();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(
                temporary, new JavaScriptSerializer().Serialize(data)
            );

            if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (e.Cancel || !settingsInitialized)
            return;

        if (reminderMode.SelectedIndex == 1 && !RankReminderValid)
        {
            e.Cancel = true;
            MessageBox.Show(this,
                "Warn before must be shorter than the selected rank estimate. " +
                "Correct it or select Manual before closing.",
                "Invalid reminder settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Do not save an unconfirmed replacement for a missing monitor.
        if (savedMonitorNeedsConfirmation)
            return;

        try
        {
            SavePersistentSettings();
        }
        catch (Exception ex)
        {
            DialogResult result = MessageBox.Show(
                this,
                "Settings could not be saved:\r\n" + ex.Message +
                "\r\n\r\nClose anyway?",
                "Settings save failed",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );
            if (result == DialogResult.No)
                e.Cancel = true;
        }
    }
}
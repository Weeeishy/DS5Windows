using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HidLibrary;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Windows.Forms;

const int SonyVendorId = 0x054C;
const int ReadTimeoutMs = 1000;
const uint RequestedTimerResolutionMs = 1;
const short GenericDesktopUsagePage = 0x01;
const short JoystickUsage = 0x04;
const short GamepadUsage = 0x05;
const int PreferredHidInputBufferCount = 3;
const int DefaultLightbarHue = 220;
const byte DualSenseOutputReportUsbId = 0x02;
const int DualSenseOutputReportUsbLength = 63;
const byte DualSenseOutputReportBluetoothId = 0x31;
const int DualSenseOutputReportBluetoothLength = 78;
const byte DualSenseOutputTag = 0x10;
const byte DualSenseLightbarControlEnableFlag = 0x04;
const byte DualSenseLightbarSetupControlEnableFlag = 0x02;
const byte DualSenseLightbarSetupLightOut = 0x02;
const byte DualSenseOutputCrcSeed = 0xA2;
const int DualSenseUsbValidFlag1Byte = 2;
const int DualSenseUsbValidFlag2Byte = 39;
const int DualSenseUsbLightbarSetupByte = 42;
const int DualSenseUsbLightbarRedByte = 45;
const int DualSenseUsbLightbarGreenByte = 46;
const int DualSenseUsbLightbarBlueByte = 47;
const int DualSenseBluetoothValidFlag1Byte = 4;
const int DualSenseBluetoothValidFlag2Byte = 41;
const int DualSenseBluetoothLightbarSetupByte = 44;
const int DualSenseBluetoothLightbarRedByte = 47;
const int DualSenseBluetoothLightbarGreenByte = 48;
const int DualSenseBluetoothLightbarBlueByte = 49;

const int LeftStickXByte = 1;
const int LeftStickYByte = 2;
const int RightStickXByte = 3;
const int RightStickYByte = 4;
const int LeftTriggerByte = 5;
const int RightTriggerByte = 6;
const int Buttons1Byte = 8;
const int Buttons2Byte = 9;
const int Buttons3Byte = 10;
const byte DPadMask = 0x0F;
const byte SquareMask = 0x10;
const byte CrossMask = 0x20;
const byte CircleMask = 0x40;
const byte TriangleMask = 0x80;
const byte LeftShoulderMask = 0x01;
const byte RightShoulderMask = 0x02;
const byte LeftThumbMask = 0x40;
const byte RightThumbMask = 0x80;
const byte OptionsMask = 0x20;
const byte ShareMask = 0x10;
const byte PsMask = 0x01;
const byte TouchpadMask = 0x02;
const bool InvertLeftStickY = true;
const bool InvertRightStickY = true;

const int SmartDropHoldMs = 500; // Hold threshold for stack drop (matches Controllable's 10 ticks)
const int McScrollCooldownMs = 120; // Minimum time between repeated scroll events
const int RumbleDurationMs = 100; // Rumble pulse length in milliseconds
const uint KEYEVENTF_KEYUP = 0x0002;
const uint MOUSEEVENTF_MOVE = 0x0001;
const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
const uint MOUSEEVENTF_WHEEL = 0x0800;

// Gyro byte offsets (int16 LE pairs, relative to report data start)
const int GyroXLoByte = 16;
const int GyroYLoByte = 18;
const int GyroZLoByte = 20;

// Rumble output report
const int DualSenseUsbMotorFlagByte = 1;
const int DualSenseBluetoothMotorFlagByte = 3;
const byte DualSenseMotorEnableFlag = 0x01;
const int DualSenseUsbRightMotorByte = 3;
const int DualSenseUsbLeftMotorByte = 4;
const int DualSenseBluetoothRightMotorByte = 5;
const int DualSenseBluetoothLeftMotorByte = 6;

// Cursor state and gyro
const int CURSOR_SHOWING = 0x00000001;
const float GyroScaleFactor = 0.005f;

var devices = new List<(HidDevice Device, string Product, string Manufacturer, int InputLength, int OutputLength, int FeatureLength)>();
CancellationTokenSource? activeRunCts = null;
Task? activeRunTask = null;
var currentMode = "idle";
var lightbarHue = DefaultLightbarHue;
var lightbarColorArgb = ColorFromHue(lightbarHue).ToArgb();
var isClosingAfterStop = false;
byte dualSenseOutputSequence = 0;
var mcPressedKeys = new HashSet<byte>();
var mcActiveComboKeys = new HashSet<string>();
var mcSprintToggled = false;
var mcLeftMouseDown = false;
var mcRightMouseDown = false;
var mcMiddleMouseDown = false;

var mcConfigPath = Path.Combine(AppContext.BaseDirectory, "mc-bindings.json");
var mcSettingsPath = Path.Combine(AppContext.BaseDirectory, "mc-settings.json");

// Configurable settings (saved to mc-settings.json)
float settingDeadzone = 0.10f;
float settingSensitivityX = 22.0f;
float settingSensitivityY = 16.0f;
float settingCursorSpeed = 12.0f;
bool settingGyroEnabled = false;
float settingGyroSensitivity = 1.0f;
bool settingRumbleEnabled = true;
float settingRumbleStrength = 0.10f;
bool settingInvertLookX = false;
bool settingInvertLookY = false;
float settingTriggerDeadzone = 0.05f;
bool settingSmartDrop = true;

// Action types: "key:VK" for keyboard, "mouse:left/right/middle" for mouse buttons, "scroll:up/down" for scroll
var mcBindings = new Dictionary<string, string>
{
    ["Cross"] = "key:Space",
    ["Circle"] = "key:LShift",
    ["Square"] = "key:F",
    ["Triangle"] = "key:E",
    ["L1"] = "scroll:up",
    ["R1"] = "scroll:down",
    ["L2"] = "mouse:right",
    ["R2"] = "mouse:left",
    ["L3"] = "key:LCtrl",
    ["R3"] = "key:None",
    ["Options"] = "key:Escape",
    ["Share"] = "key:Tab",
    ["PS"] = "key:None",
    ["Touchpad"] = "key:T",
    ["DPadUp"] = "key:F5",
    ["DPadDown"] = "key:Q",
    ["DPadLeft"] = "mouse:middle",
    ["DPadRight"] = "key:None",
};

var mcKeyNameToVk = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
{
    ["None"] = 0x00,
    ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
    ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
    ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
    ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
    ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
    ["Z"] = 0x5A,
    ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
    ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
    ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
    ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
    ["F11"] = 0x7A, ["F12"] = 0x7B,
    ["Space"] = 0x20, ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Tab"] = 0x09,
    ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D,
    ["LShift"] = 0xA0, ["RShift"] = 0xA1, ["LCtrl"] = 0xA2, ["RCtrl"] = 0xA3,
    ["LAlt"] = 0xA4, ["RAlt"] = 0xA5,
    ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
    ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
};

var mcActionChoices = new[] {
    "key:None",
    "key:Space", "key:LShift", "key:LCtrl", "key:Tab", "key:Escape", "key:Enter",
    "key:A", "key:B", "key:C", "key:D", "key:E", "key:F", "key:G", "key:H",
    "key:I", "key:J", "key:K", "key:L", "key:M", "key:N", "key:O", "key:P",
    "key:Q", "key:R", "key:S", "key:T", "key:U", "key:V", "key:W", "key:X",
    "key:Y", "key:Z",
    "key:1", "key:2", "key:3", "key:4", "key:5", "key:6", "key:7", "key:8", "key:9", "key:0",
    "key:F1", "key:F2", "key:F3", "key:F4", "key:F5", "key:F6", "key:F7", "key:F8", "key:F9", "key:F10", "key:F11", "key:F12",
    "key:LAlt", "key:RAlt", "key:RCtrl", "key:RShift",
    "key:Up", "key:Down", "key:Left", "key:Right",
    "key:Backspace", "key:Delete", "key:Insert", "key:Home", "key:End", "key:PageUp", "key:PageDown",
    "mouse:left", "mouse:right", "mouse:middle",
    "scroll:up", "scroll:down",
    "combo:ctrl+q", "combo:ctrl+middle",
};

LoadMcBindings();
LoadMcSettings();

ApplicationConfiguration.Initialize();

// ── Dark theme colors ──
var bgDark = Color.FromArgb(24, 24, 32);
var bgCard = Color.FromArgb(32, 33, 44);
var bgCardHover = Color.FromArgb(40, 42, 56);
var bgInput = Color.FromArgb(42, 44, 58);
var bgLog = Color.FromArgb(18, 18, 24);
var accentBlue = Color.FromArgb(88, 130, 255);
var accentGreen = Color.FromArgb(76, 217, 130);
var accentRed = Color.FromArgb(255, 92, 92);
var accentOrange = Color.FromArgb(255, 183, 77);
var accentMc = Color.FromArgb(80, 200, 120);
var textPrimary = Color.FromArgb(230, 233, 240);
var textSecondary = Color.FromArgb(140, 148, 168);
var textDim = Color.FromArgb(90, 96, 112);
var borderColor = Color.FromArgb(52, 56, 72);

// ── Helper: create a modern flat button ──
Button MakeButton(string text, Color bg, Color fg, int hPad = 18, int vPad = 7)
{
    var btn = new Button
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = bg,
        ForeColor = fg,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Cursor = Cursors.Hand,
        Padding = new Padding(hPad, vPad, hPad, vPad),
        Margin = new Padding(0, 0, 10, 0),
        AutoSize = true
    };
    btn.FlatAppearance.BorderSize = 0;
    btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(
        Math.Min(bg.R + 20, 255), Math.Min(bg.G + 20, 255), Math.Min(bg.B + 20, 255));
    btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(
        Math.Max(bg.R - 10, 0), Math.Max(bg.G - 10, 0), Math.Max(bg.B - 10, 0));
    return btn;
}

// ── Helper: styled card panel ──
Panel MakeCard()
{
    var card = new Panel
    {
        Dock = DockStyle.Top,
        BackColor = bgCard,
        Padding = new Padding(16, 14, 16, 14),
        Margin = new Padding(0, 0, 0, 10),
        AutoSize = true
    };
    return card;
}

Label MakeCardTitle(string text)
{
    return new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10f),
        ForeColor = textPrimary,
        Margin = new Padding(0, 0, 0, 8)
    };
}

var form = new Form
{
    Text = "DualSense Mapper",
    StartPosition = FormStartPosition.CenterScreen,
    Width = 780,
    Height = 620,
    MinimumSize = new Size(700, 520),
    Font = new Font("Segoe UI", 10),
    BackColor = bgDark,
    ForeColor = textPrimary,
    Icon = LoadAppIcon()
};

var root = new TableLayoutPanel
{
    Dock = DockStyle.Fill,
    ColumnCount = 1,
    RowCount = 7,
    Padding = new Padding(20, 16, 20, 16),
    BackColor = bgDark
};
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // subtitle
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // device card
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // LED card
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // actions
root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status
root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
form.Controls.Add(root);

// ── Title ──
var titleLabel = new Label
{
    AutoSize = true,
    Font = new Font("Segoe UI", 20, FontStyle.Bold),
    ForeColor = textPrimary,
    Text = "DualSense Mapper",
    Margin = new Padding(0, 0, 0, 2)
};

var subtitleLabel = new Label
{
    AutoSize = true,
    MaximumSize = new Size(720, 0),
    ForeColor = textSecondary,
    Font = new Font("Segoe UI", 9.5f),
    Text = "Map your DualSense to Xbox 360 or directly to Minecraft.  USB & Bluetooth  \u2022  Low-latency HID",
    Margin = new Padding(0, 0, 0, 14)
};

// ── Controller Card ──
var deviceCard = MakeCard();
var deviceCardLayout = new TableLayoutPanel
{
    Dock = DockStyle.Top,
    AutoSize = true,
    ColumnCount = 2,
    RowCount = 3,
    BackColor = Color.Transparent
};
deviceCardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
deviceCardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
deviceCardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
deviceCardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
deviceCardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

var deviceCardTitle = MakeCardTitle("\uD83C\uDFAE  Controller");

var deviceCombo = new ComboBox
{
    Dock = DockStyle.Top,
    DropDownStyle = ComboBoxStyle.DropDownList,
    BackColor = bgInput,
    ForeColor = textPrimary,
    FlatStyle = FlatStyle.Flat,
    Font = new Font("Segoe UI", 9.5f),
    Margin = new Padding(0, 0, 10, 6)
};

var refreshButton = MakeButton("\u21BB  Refresh", Color.FromArgb(50, 54, 72), textPrimary, 14, 5);
refreshButton.Margin = new Padding(0, 0, 0, 6);

var deviceDetailsLabel = new Label
{
    AutoSize = true,
    MaximumSize = new Size(680, 0),
    ForeColor = textDim,
    Font = new Font("Segoe UI", 8.5f),
    Text = "No device selected.",
    Margin = new Padding(0, 2, 0, 0)
};

deviceCardLayout.Controls.Add(deviceCombo, 0, 0);
deviceCardLayout.Controls.Add(refreshButton, 1, 0);
deviceCardLayout.Controls.Add(deviceDetailsLabel, 0, 1);
deviceCardLayout.SetColumnSpan(deviceDetailsLabel, 2);

deviceCard.Controls.Add(deviceCardLayout);
deviceCard.Controls.Add(deviceCardTitle);
deviceCardTitle.Dock = DockStyle.Top;
deviceCardLayout.Dock = DockStyle.Top;

// ── LED Card ──
var lightbarCard = MakeCard();
var lightbarInner = new TableLayoutPanel
{
    Dock = DockStyle.Top,
    AutoSize = true,
    ColumnCount = 3,
    RowCount = 2,
    BackColor = Color.Transparent
};
lightbarInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
lightbarInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
lightbarInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
lightbarInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
lightbarInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

var lightbarCardTitle = MakeCardTitle("\uD83D\uDCA1  Lightbar");

var lightbarLabel = new Label
{
    AutoSize = true,
    Text = "Hue",
    ForeColor = textSecondary,
    Font = new Font("Segoe UI", 9f),
    Anchor = AnchorStyles.Left,
    Margin = new Padding(0, 8, 10, 0)
};

var lightbarSlider = new TrackBar
{
    Dock = DockStyle.Top,
    Minimum = 0,
    Maximum = 359,
    TickFrequency = 30,
    LargeChange = 15,
    SmallChange = 1,
    Value = lightbarHue,
    BackColor = bgCard,
    Margin = new Padding(0, 0, 10, 0)
};

var lightbarPreview = new Panel
{
    Width = 36,
    Height = 20,
    Margin = new Padding(0, 8, 0, 0),
    BorderStyle = BorderStyle.None
};

var lightbarValueLabel = new Label
{
    AutoSize = true,
    ForeColor = textDim,
    Font = new Font("Segoe UI", 8.5f),
    Margin = new Padding(0, 4, 0, 0)
};

lightbarInner.Controls.Add(lightbarLabel, 0, 0);
lightbarInner.Controls.Add(lightbarSlider, 1, 0);
lightbarInner.Controls.Add(lightbarPreview, 2, 0);
lightbarInner.Controls.Add(lightbarValueLabel, 1, 1);
lightbarInner.SetColumnSpan(lightbarValueLabel, 2);

lightbarCard.Controls.Add(lightbarInner);
lightbarCard.Controls.Add(lightbarCardTitle);
lightbarCardTitle.Dock = DockStyle.Top;
lightbarInner.Dock = DockStyle.Top;

// ── Action Buttons ──
var actionPanel = new FlowLayoutPanel
{
    Dock = DockStyle.Top,
    AutoSize = true,
    WrapContents = false,
    Margin = new Padding(0, 4, 0, 10),
    BackColor = Color.Transparent
};

var mapButton = MakeButton("\u25B6  Xbox 360", accentBlue, Color.White);
var mcButton = MakeButton("\u26CF  Minecraft", accentMc, Color.FromArgb(20, 20, 20));
var mcConfigButton = MakeButton("\u2699  Configure", Color.FromArgb(50, 54, 72), textPrimary, 14, 7);

var stopButton = MakeButton("\u25A0  Stop", Color.FromArgb(60, 36, 36), accentRed, 14, 7);
stopButton.Enabled = false;

actionPanel.Controls.Add(mapButton);
actionPanel.Controls.Add(mcButton);
actionPanel.Controls.Add(mcConfigButton);
actionPanel.Controls.Add(stopButton);

// ── Status Label ──
var statusLabel = new Label
{
    AutoSize = true,
    Padding = new Padding(12, 7, 12, 7),
    BackColor = Color.FromArgb(36, 38, 52),
    ForeColor = textSecondary,
    Font = new Font("Segoe UI Semibold", 9.5f),
    Text = "\u25CF  Status: Idle",
    Margin = new Padding(0, 0, 0, 10)
};

// ── Log ──
var logBox = new TextBox
{
    Dock = DockStyle.Fill,
    Multiline = true,
    ReadOnly = true,
    ScrollBars = ScrollBars.Vertical,
    BackColor = bgLog,
    ForeColor = Color.FromArgb(160, 170, 190),
    Font = new Font("Cascadia Mono, Consolas", 9f),
    BorderStyle = BorderStyle.None
};

root.Controls.Add(titleLabel, 0, 0);
root.Controls.Add(subtitleLabel, 0, 1);
root.Controls.Add(deviceCard, 0, 2);
root.Controls.Add(lightbarCard, 0, 3);
root.Controls.Add(actionPanel, 0, 4);
root.Controls.Add(statusLabel, 0, 5);
var logHost = new Panel
{
    Dock = DockStyle.Fill,
    Margin = new Padding(0),
    Padding = new Padding(1),
    BackColor = borderColor
};
logHost.Controls.Add(logBox);
root.Controls.Add(logHost, 0, 6);

refreshButton.Click += (_, _) => RefreshDevices();
deviceCombo.SelectedIndexChanged += (_, _) => UpdateSelectedDeviceDetails();
lightbarSlider.ValueChanged += (_, _) => HandleLightbarSliderChanged();

mapButton.Click += async (_, _) =>
{
    if (currentMode == "mapping")
    {
        return;
    }

    var selected = GetSelectedDevice();
    if (selected is null)
    {
        MessageBox.Show(form, "Connect your DualSense by USB or Bluetooth and click Refresh first.", "No device", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    await StopCurrentRunAsync();
    AppendLog($"Starting mapping on PID 0x{selected.Value.Device.Attributes.ProductId:X4} over {GetConnectionLabel(selected.Value.Device)}.");
    StartRun("mapping", "Mapping running", token => RunMappedMode(selected.Value.Device, token));
};

mcButton.Click += async (_, _) =>
{
    if (currentMode == "minecraft")
    {
        return;
    }

    var selected = GetSelectedDevice();
    if (selected is null)
    {
        MessageBox.Show(form, "Connect your DualSense by USB or Bluetooth and click Refresh first.", "No device", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    await StopCurrentRunAsync();
    AppendLog($"Starting Minecraft mode on PID 0x{selected.Value.Device.Attributes.ProductId:X4} over {GetConnectionLabel(selected.Value.Device)}.");
    StartRun("minecraft", "Minecraft mapping running", token => RunMinecraftMode(selected.Value.Device, token));
};

stopButton.Click += async (_, _) => await StopCurrentRunAsync();

mcConfigButton.Click += (_, _) =>
{
    ShowMcConfigDialog();
};

form.FormClosing += async (_, e) =>
{
    if (isClosingAfterStop || activeRunCts is null)
    {
        return;
    }

    e.Cancel = true;
    isClosingAfterStop = true;
    SetStatus("Stopping...");
    await StopCurrentRunAsync();

    if (!form.IsDisposed)
    {
        form.BeginInvoke(new Action(form.Close));
    }
};

UpdateLightbarPreview();
RefreshDevices();
Application.Run(form);

void RefreshDevices()
{
    devices = HidDevices.Enumerate(SonyVendorId)
        .Select(device => (
            Device: device,
            Product: TryReadProduct(device),
            Manufacturer: TryReadManufacturer(device),
            InputLength: (int)device.Capabilities.InputReportByteLength,
            OutputLength: (int)device.Capabilities.OutputReportByteLength,
            FeatureLength: (int)device.Capabilities.FeatureReportByteLength))
        .Where(device => device.InputLength > 0 && IsPrimaryControllerInterface(device.Device))
        .OrderByDescending(device => ScoreDualSenseLikelihood(device.Product, device.Manufacturer))
        .ThenBy(device => IsBluetoothConnection(device.Device) ? 1 : 0)
        .ThenByDescending(device => device.InputLength)
        .ToList();

    deviceCombo.Items.Clear();
    foreach (var device in devices)
    {
        deviceCombo.Items.Add(FormatDeviceListItem(device));
    }

    if (deviceCombo.Items.Count > 0)
    {
        deviceCombo.SelectedIndex = 0;
        SetStatus("Device ready");
        AppendLog($"Detected {devices.Count} Sony gamepad HID device(s).");
    }
    else
    {
        deviceDetailsLabel.Text = "No Sony USB or Bluetooth gamepad HID devices found.";
        SetStatus("No controller found");
        AppendLog("No Sony USB or Bluetooth gamepad HID devices found. Connect the controller and click Refresh.");
    }

    UpdateActionAvailability();
}

void UpdateSelectedDeviceDetails()
{
    var selected = GetSelectedDevice();
    if (selected is null)
    {
        deviceDetailsLabel.Text = "No device selected.";
        UpdateActionAvailability();
        return;
    }

    deviceDetailsLabel.Text =
        $"PID 0x{selected.Value.Device.Attributes.ProductId:X4} | Product=\"{NullToUnknown(selected.Value.Product)}\" | " +
        $"Input={selected.Value.InputLength} Output={selected.Value.OutputLength} Feature={selected.Value.FeatureLength} | " +
        $"Connection={GetConnectionLabel(selected.Value.Device)} | UsagePage=0x{selected.Value.Device.Capabilities.UsagePage:X2} Usage=0x{selected.Value.Device.Capabilities.Usage:X2}";

    UpdateActionAvailability();
}

void HandleLightbarSliderChanged()
{
    lightbarHue = lightbarSlider.Value;
    var color = ColorFromHue(lightbarHue);
    lightbarColorArgb = color.ToArgb();
    UpdateLightbarPreview();

    if (currentMode == "mapping")
    {
        return;
    }

    var selected = GetSelectedDevice();
    if (selected is null)
    {
        return;
    }

    TryApplyLightbarColor(selected.Value.Device, color, initializeLightbar: true, logErrors: false);
}

void UpdateLightbarPreview()
{
    var color = Color.FromArgb(lightbarColorArgb);
    lightbarPreview.BackColor = color;
    lightbarValueLabel.Text = $"Hue {lightbarHue:D3} deg  RGB {color.R}, {color.G}, {color.B}";
}

(HidDevice Device, string Product, string Manufacturer, int InputLength, int OutputLength, int FeatureLength)? GetSelectedDevice()
{
    var index = deviceCombo.SelectedIndex;
    if (index < 0 || index >= devices.Count)
    {
        return null;
    }

    return devices[index];
}

void UpdateActionAvailability()
{
    if (form.IsDisposed)
    {
        return;
    }

    if (mapButton.InvokeRequired)
    {
        mapButton.BeginInvoke(new Action(UpdateActionAvailability));
        return;
    }

    var hasDevice = GetSelectedDevice() is not null;
    var isBusy = currentMode != "idle";

    refreshButton.Enabled = !isBusy;
    deviceCombo.Enabled = !isBusy;
    mapButton.Enabled = !isBusy && hasDevice;
    mcButton.Enabled = !isBusy && hasDevice;
    mcConfigButton.Enabled = !isBusy;
    stopButton.Enabled = isBusy;
}

void StartRun(string modeName, string statusText, Action<CancellationToken> runAction)
{
    if (activeRunTask is not null && !activeRunTask.IsCompleted)
    {
        return;
    }

    currentMode = modeName;
    activeRunCts = new CancellationTokenSource();
    UpdateActionAvailability();
    SetStatus(statusText);

    activeRunTask = Task.Run(() =>
    {
        var hadError = false;
        try
        {
            runAction(activeRunCts.Token);
        }
        catch (Exception ex) when (IsSpuriousSuccessException(ex))
        {
            AppendLog("Ignored a spurious Win32 success exception from the device API.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Run cancelled.");
        }
        catch (VigemBusNotFoundException)
        {
            AppendLog("ViGEmBus is not installed, so the virtual Xbox 360 controller cannot be created.");
            AppendLog("This app emulates an Xbox 360 controller, not an Xbox One controller.");
            AppendLog("Install the ViGEmBus driver, then start mapping again.");
            SetStatus("Error");
            hadError = true;
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error");
            hadError = true;
        }
        finally
        {
            if (!form.IsDisposed)
            {
                form.BeginInvoke(new Action(() =>
                {
                    currentMode = "idle";
                    UpdateActionAvailability();
                    if (!hadError) SetStatus("Idle");
                }));
            }
        }
    }, activeRunCts.Token);
}

async Task StopCurrentRunAsync()
{
    if (activeRunCts is null)
    {
        currentMode = "idle";
        UpdateActionAvailability();
        return;
    }

    activeRunCts.Cancel();

    if (activeRunTask is not null)
    {
        try
        {
            await activeRunTask;
        }
        catch
        {
        }
    }

    activeRunTask = null;
    activeRunCts.Dispose();
    activeRunCts = null;
    currentMode = "idle";
    UpdateActionAvailability();
    SetStatus("Idle");
}

void RunMappedMode(HidDevice device, CancellationToken cancellationToken)
{
    OpenDevice(device);
    PrepareDeviceForLowLatency(device);
    var lastLightbarColorArgb = lightbarColorArgb;
    TryApplyLightbarColor(device, Color.FromArgb(lastLightbarColorArgb), initializeLightbar: true, logErrors: true);
    var highResolutionTimerEnabled = TryEnableHighResolutionTimer();
    var previousThreadPriority = Thread.CurrentThread.Priority;
    Thread.CurrentThread.Priority = ThreadPriority.Highest;
    using var client = new ViGEmClient();
    var xbox = client.CreateXbox360Controller();
    xbox.AutoSubmitReport = false;
    ConnectVirtualController(xbox);
    AppendLog("Virtual Xbox 360 controller connected.");

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!device.IsConnected)
            {
                AppendLog("Controller disconnected.");
                break;
            }

            try
            {
                var report = device.ReadReport(ReadTimeoutMs);
                if (report.ReadStatus == HidDeviceData.ReadStatus.WaitTimedOut)
                {
                    continue;
                }

                if (report.ReadStatus == HidDeviceData.ReadStatus.NotConnected)
                {
                    AppendLog("Read stopped: controller is no longer connected.");
                    break;
                }

                if (report.ReadStatus != HidDeviceData.ReadStatus.Success || !report.Exists)
                {
                    AppendLog($"Read status: {report.ReadStatus}");
                    continue;
                }

                var desiredLightbarColorArgb = lightbarColorArgb;
                if (desiredLightbarColorArgb != lastLightbarColorArgb &&
                    TryApplyLightbarColor(device, Color.FromArgb(desiredLightbarColorArgb), initializeLightbar: false, logErrors: false))
                {
                    lastLightbarColorArgb = desiredLightbarColorArgb;
                }

                var raw = report.GetBytes();
                var reportOffset = GetReportOffset(device, raw);
                if (reportOffset < 0 || !RawIndexesExist(raw, reportOffset))
                {
                    continue;
                }

                var leftX = MapUnsignedByteToXboxAxis(raw[LeftStickXByte + reportOffset], invert: false);
                var leftY = MapUnsignedByteToXboxAxis(raw[LeftStickYByte + reportOffset], invert: InvertLeftStickY);
                var rightX = MapUnsignedByteToXboxAxis(raw[RightStickXByte + reportOffset], invert: false);
                var rightY = MapUnsignedByteToXboxAxis(raw[RightStickYByte + reportOffset], invert: InvertRightStickY);
                var leftTrigger = raw[LeftTriggerByte + reportOffset];
                var rightTrigger = raw[RightTriggerByte + reportOffset];

                var buttons1 = raw[Buttons1Byte + reportOffset];
                var buttons2 = raw[Buttons2Byte + reportOffset];
                var buttons3 = raw[Buttons3Byte + reportOffset];
                var dpad = buttons1 & DPadMask;

                ushort xboxButtons = 0;
                unchecked
                {
                    if ((buttons1 & CrossMask) != 0) xboxButtons |= Xbox360Button.A.Value;
                    if ((buttons1 & CircleMask) != 0) xboxButtons |= Xbox360Button.B.Value;
                    if ((buttons1 & SquareMask) != 0) xboxButtons |= Xbox360Button.X.Value;
                    if ((buttons1 & TriangleMask) != 0) xboxButtons |= Xbox360Button.Y.Value;
                    if ((buttons2 & LeftShoulderMask) != 0) xboxButtons |= Xbox360Button.LeftShoulder.Value;
                    if ((buttons2 & RightShoulderMask) != 0) xboxButtons |= Xbox360Button.RightShoulder.Value;
                    if ((buttons2 & LeftThumbMask) != 0) xboxButtons |= Xbox360Button.LeftThumb.Value;
                    if ((buttons2 & RightThumbMask) != 0) xboxButtons |= Xbox360Button.RightThumb.Value;
                    if ((buttons2 & OptionsMask) != 0) xboxButtons |= Xbox360Button.Start.Value;
                    if ((buttons2 & ShareMask) != 0) xboxButtons |= Xbox360Button.Back.Value;
                    // DualSense has more center buttons than an Xbox 360 pad, so PS and touchpad click share Guide.
                    if ((buttons3 & (PsMask | TouchpadMask)) != 0) xboxButtons |= Xbox360Button.Guide.Value;
                    if (dpad == 0 || dpad == 1 || dpad == 7) xboxButtons |= Xbox360Button.Up.Value;
                    if (dpad == 1 || dpad == 2 || dpad == 3) xboxButtons |= Xbox360Button.Right.Value;
                    if (dpad == 3 || dpad == 4 || dpad == 5) xboxButtons |= Xbox360Button.Down.Value;
                    if (dpad == 5 || dpad == 6 || dpad == 7) xboxButtons |= Xbox360Button.Left.Value;
                }

                xbox.SetButtonsFull(xboxButtons);
                xbox.LeftThumbX = leftX;
                xbox.LeftThumbY = leftY;
                xbox.RightThumbX = rightX;
                xbox.RightThumbY = rightY;
                xbox.LeftTrigger = leftTrigger;
                xbox.RightTrigger = rightTrigger;
                xbox.SubmitReport();
            }
            catch (Exception ex) when (IsSpuriousSuccessException(ex))
            {
                continue;
            }
        }
    }
    finally
    {
        Thread.CurrentThread.Priority = previousThreadPriority;
        DisableHighResolutionTimer(highResolutionTimerEnabled);

        try
        {
            xbox.Disconnect();
        }
        catch
        {
        }

        try
        {
            device.CloseDevice();
        }
        catch
        {
        }

        AppendLog("Virtual Xbox 360 controller disconnected.");
    }
}

void RunMinecraftMode(HidDevice device, CancellationToken cancellationToken)
{
    OpenDevice(device);
    PrepareDeviceForLowLatency(device);
    var lastLightbarColorArgb = lightbarColorArgb;
    TryApplyLightbarColor(device, Color.FromArgb(lastLightbarColorArgb), initializeLightbar: true, logErrors: true);
    var highResolutionTimerEnabled = TryEnableHighResolutionTimer();
    var previousThreadPriority = Thread.CurrentThread.Priority;
    Thread.CurrentThread.Priority = ThreadPriority.Highest;
    AppendLog("Minecraft keyboard/mouse mode active. Switch to Minecraft now!");
    AppendLog("Configuration loaded. Click Configure (while stopped) to adjust bindings and settings.");

    var scrollCooldown = Stopwatch.StartNew(); scrollCooldown.Reset();
    var prevDpadUp = false;
    var prevDpadDown = false;
    var prevDpadLeft = false;
    var prevDpadRight = false;
    var prevL1 = false;
    var prevR1 = false;
    var sprintToggled = false;
    var sneakToggled = false;
    var prevL3 = false;
    var prevR3 = false;
    var prevR2Down = false;
    var prevL2Down = false;
    var rumbleTimer = Stopwatch.StartNew(); rumbleTimer.Reset();
    bool wasRumbling = false;
    bool wasInMenu = false;
    // Smart drop state: tap Q = single, hold Ctrl+Q = stack
    bool dropHeld = false;
    var dropHoldTimer = Stopwatch.StartNew(); dropHoldTimer.Reset();
    bool dropStackSent = false;
    // Float accumulators for sub-pixel camera/cursor movement
    float cameraAccumX = 0f, cameraAccumY = 0f;
    float cursorAccumX = 0f, cursorAccumY = 0f;
    byte mcTriggerThreshold = (byte)Math.Clamp((int)(settingTriggerDeadzone * 255), 1, 254);
    // Menu click state: separate from gameplay mouse state
    bool menuLeftDown = false;
    bool menuRightDown = false;
    bool menuShiftHeld = false;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!device.IsConnected)
            {
                AppendLog("Controller disconnected.");
                break;
            }

            try
            {
                var report = device.ReadReport(ReadTimeoutMs);
                if (report.ReadStatus == HidDeviceData.ReadStatus.WaitTimedOut)
                {
                    continue;
                }

                if (report.ReadStatus == HidDeviceData.ReadStatus.NotConnected)
                {
                    AppendLog("Read stopped: controller is no longer connected.");
                    break;
                }

                if (report.ReadStatus != HidDeviceData.ReadStatus.Success || !report.Exists)
                {
                    continue;
                }

                var desiredLightbarColorArgb = lightbarColorArgb;
                if (desiredLightbarColorArgb != lastLightbarColorArgb &&
                    TryApplyLightbarColor(device, Color.FromArgb(desiredLightbarColorArgb), initializeLightbar: false, logErrors: false))
                {
                    lastLightbarColorArgb = desiredLightbarColorArgb;
                }

                var raw = report.GetBytes();
                var reportOffset = GetReportOffset(device, raw);
                if (reportOffset < 0 || !RawIndexesExist(raw, reportOffset))
                {
                    continue;
                }

                // Detect cursor state (visible = Minecraft menu/inventory open)
                bool inMenu = IsCursorVisible();

                // On menu transition: release gameplay keys / menu clicks
                if (inMenu && !wasInMenu)
                {
                    // Entering menu: release all gameplay keys, mouse buttons, and toggles
                    McReleaseAll();
                    sprintToggled = false; sneakToggled = false;
                    mcSprintToggled = false;
                    prevL3 = false; prevR3 = false;
                    prevDpadLeft = false; prevDpadRight = false;
                }
                else if (!inMenu && wasInMenu)
                {
                    // Leaving menu: release menu clicks
                    McMenuRelease(ref menuLeftDown, ref menuRightDown, ref menuShiftHeld);
                }
                wasInMenu = inMenu;

                // ── Sticks ──
                float lx = (raw[LeftStickXByte + reportOffset] - 128) / 128.0f;
                float ly = (raw[LeftStickYByte + reportOffset] - 128) / 128.0f;
                float rx = (raw[RightStickXByte + reportOffset] - 128) / 128.0f;
                float ry = (raw[RightStickYByte + reportOffset] - 128) / 128.0f;

                // Apply proper deadzone scaling (Controllable-style: remap post-deadzone to 0-1)
                lx = ApplyDeadzone(lx, settingDeadzone);
                ly = ApplyDeadzone(ly, settingDeadzone);
                rx = ApplyDeadzone(rx, settingDeadzone);
                ry = ApplyDeadzone(ry, settingDeadzone);

                if (inMenu)
                {
                    // ── MENU MODE: Left stick moves cursor, face buttons do inventory clicks ──

                    // Left stick → cursor movement (squared curve with float accumulation)
                    float cursorMoveX = Math.Sign(lx) * lx * lx;
                    float cursorMoveY = Math.Sign(ly) * ly * ly;
                    cursorAccumX += cursorMoveX * settingCursorSpeed;
                    cursorAccumY += cursorMoveY * settingCursorSpeed;

                    // Right stick also moves cursor in menus (like Controllable uses left, we support both)
                    float rCursorX = Math.Sign(rx) * rx * rx;
                    float rCursorY = Math.Sign(ry) * ry * ry;
                    cursorAccumX += rCursorX * settingCursorSpeed;
                    cursorAccumY += rCursorY * settingCursorSpeed;

                    int cursorDx = (int)cursorAccumX;
                    int cursorDy = (int)cursorAccumY;
                    cursorAccumX -= cursorDx;
                    cursorAccumY -= cursorDy;

                    if (cursorDx != 0 || cursorDy != 0)
                        NativeInput.mouse_event(MOUSEEVENTF_MOVE, cursorDx, cursorDy, 0, UIntPtr.Zero);

                    var buttons1 = raw[Buttons1Byte + reportOffset];
                    var buttons2 = raw[Buttons2Byte + reportOffset];
                    var buttons3 = raw[Buttons3Byte + reportOffset];
                    var leftTrigger = raw[LeftTriggerByte + reportOffset];
                    var rightTrigger = raw[RightTriggerByte + reportOffset];
                    var dpad = buttons1 & DPadMask;

                    // Cross = left click (pick up / place item)
                    bool crossDown = (buttons1 & CrossMask) != 0;
                    McSetMouseState(ref menuLeftDown, crossDown, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);

                    // Square = right click (split stack)
                    bool squareDown = (buttons1 & SquareMask) != 0;
                    McSetMouseState(ref menuRightDown, squareDown, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);

                    // Circle = shift + left click (quick move item)
                    bool circleDown = (buttons1 & CircleMask) != 0;
                    if (circleDown && !menuShiftHeld)
                    {
                        NativeInput.keybd_event(0xA0, 0, 0, UIntPtr.Zero); // LShift down
                        menuShiftHeld = true;
                        NativeInput.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        NativeInput.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    }
                    if (!circleDown && menuShiftHeld)
                    {
                        NativeInput.keybd_event(0xA0, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // LShift up
                        menuShiftHeld = false;
                    }

                    // Triangle = close inventory (sends configured binding, default E)
                    McApplyBinding("Triangle", (buttons1 & TriangleMask) != 0);

                    // Shoulders — edge-triggered for scroll bindings in menus too
                    bool l1Menu = (buttons2 & LeftShoulderMask) != 0;
                    bool r1Menu = (buttons2 & RightShoulderMask) != 0;
                    if (McIsScrollAction("L1", out _))
                    {
                        if (l1Menu && !prevL1) McApplyBinding("L1", true);
                    }
                    else McApplyBinding("L1", l1Menu);
                    if (McIsScrollAction("R1", out _))
                    {
                        if (r1Menu && !prevR1) McApplyBinding("R1", true);
                    }
                    else McApplyBinding("R1", r1Menu);
                    prevL1 = l1Menu;
                    prevR1 = r1Menu;

                    // L2/R2 in menus: Use configured bindings (scroll tabs etc)
                    McApplyBinding("L2", leftTrigger >= mcTriggerThreshold);
                    McApplyBinding("R2", rightTrigger >= mcTriggerThreshold);

                    // Options still opens/closes escape menu
                    McApplyBinding("Options", (buttons2 & OptionsMask) != 0);

                    // D-pad in menus = scroll wheel (for list scrolling)
                    bool dpadUp = (dpad == 0 || dpad == 1 || dpad == 7);
                    bool dpadDown = (dpad == 3 || dpad == 4 || dpad == 5);
                    if (dpadUp && (!prevDpadUp || scrollCooldown.ElapsedMilliseconds >= McScrollCooldownMs))
                    {
                        NativeInput.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120, UIntPtr.Zero);
                        scrollCooldown.Restart();
                    }
                    if (dpadDown && (!prevDpadDown || scrollCooldown.ElapsedMilliseconds >= McScrollCooldownMs))
                    {
                        NativeInput.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -120, UIntPtr.Zero);
                        scrollCooldown.Restart();
                    }
                    prevDpadUp = dpadUp;
                    prevDpadDown = dpadDown;
                }
                else
                {
                    // ── GAMEPLAY MODE: Left stick = WASD, Right stick = camera look ──

                    // Left stick → WASD
                    McSetKeyState(0x57, ly < -0.01f);  // W (deadzone already applied)
                    McSetKeyState(0x53, ly > 0.01f);   // S
                    McSetKeyState(0x41, lx < -0.01f);  // A
                    McSetKeyState(0x44, lx > 0.01f);   // D

                    // Right stick → camera look (squared curve with float accumulation)
                    rx = Math.Sign(rx) * rx * rx;
                    ry = Math.Sign(ry) * ry * ry;
                    float camDx = rx * settingSensitivityX;
                    float camDy = ry * settingSensitivityY;

                    // Invert look (Controllable: invertRotation / invertLook)
                    if (settingInvertLookX) camDx = -camDx;
                    if (settingInvertLookY) camDy = -camDy;

                    // Gyroscope aiming
                    if (settingGyroEnabled && GyroIndexesExist(raw, reportOffset))
                    {
                        short gyroRawPitch = BitConverter.ToInt16(raw, GyroXLoByte + reportOffset);
                        short gyroRawYaw = BitConverter.ToInt16(raw, GyroYLoByte + reportOffset);
                        camDx += gyroRawYaw * GyroScaleFactor * settingGyroSensitivity;
                        camDy += gyroRawPitch * GyroScaleFactor * settingGyroSensitivity;
                    }

                    cameraAccumX += camDx;
                    cameraAccumY += camDy;
                    int mouseDx = (int)cameraAccumX;
                    int mouseDy = (int)cameraAccumY;
                    cameraAccumX -= mouseDx;
                    cameraAccumY -= mouseDy;

                    if (mouseDx != 0 || mouseDy != 0)
                        NativeInput.mouse_event(MOUSEEVENTF_MOVE, mouseDx, mouseDy, 0, UIntPtr.Zero);

                    var buttons1 = raw[Buttons1Byte + reportOffset];
                    var buttons2 = raw[Buttons2Byte + reportOffset];
                    var buttons3 = raw[Buttons3Byte + reportOffset];
                    var leftTrigger = raw[LeftTriggerByte + reportOffset];
                    var rightTrigger = raw[RightTriggerByte + reportOffset];
                    var dpad = buttons1 & DPadMask;

                    // Face buttons (gameplay bindings)
                    McApplyBinding("Cross", (buttons1 & CrossMask) != 0);
                    McApplyBinding("Circle", (buttons1 & CircleMask) != 0);
                    McApplyBinding("Square", (buttons1 & SquareMask) != 0);
                    McApplyBinding("Triangle", (buttons1 & TriangleMask) != 0);

                    // Shoulder buttons — edge-triggered for scroll bindings (one scroll per press)
                    bool l1 = (buttons2 & LeftShoulderMask) != 0;
                    bool r1 = (buttons2 & RightShoulderMask) != 0;
                    if (McIsScrollAction("L1", out _))
                    {
                        if (l1 && !prevL1) McApplyBinding("L1", true);
                    }
                    else McApplyBinding("L1", l1);
                    if (McIsScrollAction("R1", out _))
                    {
                        if (r1 && !prevR1) McApplyBinding("R1", true);
                    }
                    else McApplyBinding("R1", r1);
                    prevL1 = l1;
                    prevR1 = r1;

                    McApplyBinding("L2", leftTrigger >= mcTriggerThreshold);
                    McApplyBinding("R2", rightTrigger >= mcTriggerThreshold);

                    // Haptic rumble on attack/use (edge-triggered)
                    bool r2Down = rightTrigger >= mcTriggerThreshold;
                    bool l2Down = leftTrigger >= mcTriggerThreshold;
                    if (settingRumbleEnabled)
                    {
                        if ((r2Down && !prevR2Down) || (l2Down && !prevL2Down))
                            rumbleTimer.Restart();

                        bool shouldRumble = rumbleTimer.IsRunning && rumbleTimer.ElapsedMilliseconds < RumbleDurationMs;
                        if (shouldRumble)
                        {
                            byte motorStrength = (byte)(settingRumbleStrength * 200);
                            TryApplyRumble(device, motorStrength, motorStrength);
                        }
                        else if (wasRumbling)
                        {
                            TryApplyRumble(device, 0, 0);
                            rumbleTimer.Reset();
                        }
                        wasRumbling = shouldRumble;
                    }
                    prevR2Down = r2Down;
                    prevL2Down = l2Down;

                    // Thumb stick clicks -> toggle sprint / sneak
                    bool l3 = (buttons2 & LeftThumbMask) != 0;
                    bool r3 = (buttons2 & RightThumbMask) != 0;
                    if (l3 && !prevL3) sprintToggled = !sprintToggled;
                    if (r3 && !prevR3) sneakToggled = !sneakToggled;
                    prevL3 = l3;
                    prevR3 = r3;
                    mcSprintToggled = sprintToggled;
                    McApplyBinding("L3", sprintToggled);
                    McApplyBinding("R3", sneakToggled);

                    // Menu buttons
                    McApplyBinding("Options", (buttons2 & OptionsMask) != 0);
                    McApplyBinding("Share", (buttons2 & ShareMask) != 0);
                    McApplyBinding("PS", (buttons3 & PsMask) != 0);
                    McApplyBinding("Touchpad", (buttons3 & TouchpadMask) != 0);

                    // D-pad
                    bool dpadUp = (dpad == 0 || dpad == 1 || dpad == 7);
                    bool dpadDown = (dpad == 3 || dpad == 4 || dpad == 5);
                    bool dpadLeft = (dpad == 5 || dpad == 6 || dpad == 7);
                    bool dpadRight = (dpad == 1 || dpad == 2 || dpad == 3);

                    // DPadUp: scroll or key binding (handled independently)
                    if (McIsScrollAction("DPadUp", out _))
                    {
                        if (dpadUp && (!prevDpadUp || scrollCooldown.ElapsedMilliseconds >= McScrollCooldownMs))
                        {
                            McApplyBinding("DPadUp", true);
                            scrollCooldown.Restart();
                        }
                    }
                    else
                    {
                        if (dpadUp && !prevDpadUp) McApplyBinding("DPadUp", true);
                        else if (!dpadUp && prevDpadUp) McApplyBinding("DPadUp", false);
                    }

                    // DPadDown: scroll, smart drop, or key binding (handled independently)
                    if (McIsScrollAction("DPadDown", out _))
                    {
                        if (dpadDown && (!prevDpadDown || scrollCooldown.ElapsedMilliseconds >= McScrollCooldownMs))
                        {
                            McApplyBinding("DPadDown", true);
                            scrollCooldown.Restart();
                        }
                    }
                    else if (settingSmartDrop && mcBindings.TryGetValue("DPadDown", out var ddAction) && ddAction == "key:Q")
                    {
                        if (dpadDown)
                        {
                            if (!dropHeld)
                            {
                                dropHeld = true;
                                dropHoldTimer.Restart();
                                dropStackSent = false;
                            }

                            // Hold threshold reached: press Ctrl+Q for stack drop
                            if (dropHoldTimer.ElapsedMilliseconds >= SmartDropHoldMs && !dropStackSent)
                            {
                                // Use raw keybd_event for momentary Ctrl so we don't clear sprint toggle's tracked LCtrl state
                                NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);           // LCtrl down
                                NativeInput.keybd_event(0x51, 0, 0, UIntPtr.Zero);           // Q down
                                NativeInput.keybd_event(0x51, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Q up
                                NativeInput.keybd_event(0xA2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // LCtrl up
                                // Re-assert LCtrl only if sprint toggle is using it (L3 bound to LCtrl)
                                if (mcSprintToggled && McResolveVk("L3") == 0xA2) NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);
                                dropStackSent = true;
                            }
                        }
                        else if (dropHeld)
                        {
                            // Released: if short tap and no stack drop was sent, drop single
                            if (!dropStackSent)
                            {
                                McSetKeyState(0x51, true);  // Q down
                                McSetKeyState(0x51, false); // Q up
                            }
                            dropHeld = false;
                            dropHoldTimer.Reset();
                            dropStackSent = false;
                        }
                    }
                    else
                    {
                        if (dpadDown && !prevDpadDown) McApplyBinding("DPadDown", true);
                        else if (!dpadDown && prevDpadDown) McApplyBinding("DPadDown", false);
                    }

                    if (dpadLeft && !prevDpadLeft) McApplyBinding("DPadLeft", true);
                    else if (!dpadLeft && prevDpadLeft) McApplyBinding("DPadLeft", false);
                    if (dpadRight && !prevDpadRight) McApplyBinding("DPadRight", true);
                    else if (!dpadRight && prevDpadRight) McApplyBinding("DPadRight", false);

                    prevDpadUp = dpadUp;
                    prevDpadDown = dpadDown;
                    prevDpadLeft = dpadLeft;
                    prevDpadRight = dpadRight;
                }
            }
            catch (Exception ex) when (IsSpuriousSuccessException(ex))
            {
                continue;
            }
        }
    }
    finally
    {
        Thread.CurrentThread.Priority = previousThreadPriority;
        DisableHighResolutionTimer(highResolutionTimerEnabled);
        McMenuRelease(ref menuLeftDown, ref menuRightDown, ref menuShiftHeld);
        McReleaseAll();

        // Stop rumble on exit
        try { TryApplyRumble(device, 0, 0); } catch { }

        try
        {
            device.CloseDevice();
        }
        catch
        {
        }

        AppendLog("Minecraft mode stopped.");
    }
}

void McSetKeyState(byte vk, bool wantPressed)
{
    if (wantPressed && mcPressedKeys.Add(vk))
    {
        NativeInput.keybd_event(vk, 0, 0, UIntPtr.Zero);
    }
    else if (!wantPressed && mcPressedKeys.Remove(vk))
    {
        NativeInput.keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}

void McSetMouseState(ref bool isDown, bool wantDown, uint downFlag, uint upFlag)
{
    if (wantDown && !isDown)
    {
        NativeInput.mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
        isDown = true;
    }
    else if (!wantDown && isDown)
    {
        NativeInput.mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
        isDown = false;
    }
}

void McReleaseAll()
{
    foreach (var vk in mcPressedKeys)
    {
        NativeInput.keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    mcPressedKeys.Clear();
    mcActiveComboKeys.Clear();

    if (mcLeftMouseDown)
    {
        NativeInput.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        mcLeftMouseDown = false;
    }

    if (mcRightMouseDown)
    {
        NativeInput.mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        mcRightMouseDown = false;
    }

    if (mcMiddleMouseDown)
    {
        NativeInput.mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
        mcMiddleMouseDown = false;
    }
}

// Controllable-style deadzone: remap [deadzone, 1.0] → [0.0, 1.0]
float ApplyDeadzone(float input, float deadzone)
{
    float abs = Math.Abs(input);
    if (abs < deadzone) return 0f;
    return Math.Sign(input) * (abs - deadzone) / (1f - deadzone);
}

void McMenuRelease(ref bool menuLeftDown, ref bool menuRightDown, ref bool menuShiftHeld)
{
    if (menuLeftDown)
    {
        NativeInput.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        menuLeftDown = false;
    }
    if (menuRightDown)
    {
        NativeInput.mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        menuRightDown = false;
    }
    if (menuShiftHeld)
    {
        NativeInput.keybd_event(0xA0, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        menuShiftHeld = false;
    }
}

void ShowMcConfigDialog()
{
    var dlgBg = Color.FromArgb(24, 24, 32);
    var dlgCard = Color.FromArgb(32, 33, 44);
    var dlgText = Color.FromArgb(230, 233, 240);
    var dlgAccent = Color.FromArgb(88, 130, 255);
    var dlgInput = Color.FromArgb(42, 44, 58);
    var dlgDim = Color.FromArgb(140, 148, 168);

    using var dlg = new Form
    {
        Text = "Minecraft Configuration",
        StartPosition = FormStartPosition.CenterParent,
        Width = 520, Height = 740,
        MinimumSize = new Size(440, 500),
        Font = new Font("Segoe UI", 9.5f),
        BackColor = dlgBg, ForeColor = dlgText,
        FormBorderStyle = FormBorderStyle.Sizable,
        MaximizeBox = false, MinimizeBox = false,
        ShowIcon = false, ShowInTaskbar = false
    };

    var headerLabel = new Label
    {
        Text = "\u2699  Minecraft Configuration",
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = dlgText, AutoSize = true,
        Dock = DockStyle.Top,
        Padding = new Padding(16, 14, 16, 10),
        BackColor = dlgCard
    };

    var outerPanel = new Panel
    {
        Dock = DockStyle.Fill, AutoScroll = true,
        Padding = new Padding(16, 10, 16, 10),
        BackColor = dlgBg
    };

    var grid = new TableLayoutPanel
    {
        Dock = DockStyle.Top, AutoSize = true,
        ColumnCount = 2, Padding = new Padding(0),
        BackColor = Color.Transparent
    };
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

    // ── Bindings section header ──
    var row = 0;
    grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    var bindingsHeader = new Label
    {
        Text = "Button Bindings",
        Font = new Font("Segoe UI Semibold", 11f),
        ForeColor = dlgText, AutoSize = true,
        Margin = new Padding(0, 0, 0, 6)
    };
    grid.Controls.Add(bindingsHeader, 0, row);
    grid.SetColumnSpan(bindingsHeader, 2);
    row++;

    var buttonNames = new[] {
        "Cross", "Circle", "Square", "Triangle",
        "L1", "R1", "L2", "R2",
        "L3", "R3",
        "Options", "Share", "PS", "Touchpad",
        "DPadUp", "DPadDown", "DPadLeft", "DPadRight"
    };

    var combos = new Dictionary<string, ComboBox>();

    foreach (var btn in buttonNames)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = btn, AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = Color.FromArgb(180, 186, 200),
            Margin = new Padding(4, 9, 4, 4)
        };

        var combo = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = dlgInput, ForeColor = Color.FromArgb(220, 224, 234),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(4, 6, 4, 4)
        };

        foreach (var choice in mcActionChoices) combo.Items.Add(choice);

        if (mcBindings.TryGetValue(btn, out var current))
        {
            var idx = Array.IndexOf(mcActionChoices, current);
            combo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else combo.SelectedIndex = 0;

        combos[btn] = combo;
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(combo, 1, row);
        row++;
    }

    // ── Settings section header ──
    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // spacer
    row++;

    grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    var settingsHeader = new Label
    {
        Text = "Stick & Look",
        Font = new Font("Segoe UI Semibold", 11f),
        ForeColor = dlgText, AutoSize = true,
        Margin = new Padding(0, 8, 0, 6)
    };
    grid.Controls.Add(settingsHeader, 0, row);
    grid.SetColumnSpan(settingsHeader, 2);
    row++;

    // Helper to add a slider row
    TrackBar AddSlider(string text, int min, int max, int val, Func<int, string> fmt)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label
        {
            Text = $"{text}: {fmt(val)}", AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9f),
            ForeColor = dlgDim,
            Margin = new Padding(4, 10, 4, 4)
        };
        var slider = new TrackBar
        {
            Dock = DockStyle.Top,
            Minimum = min, Maximum = max,
            Value = Math.Clamp(val, min, max),
            TickFrequency = Math.Max(1, (max - min) / 10),
            BackColor = dlgBg,
            Margin = new Padding(4, 4, 4, 4)
        };
        slider.ValueChanged += (_, _) => lbl.Text = $"{text}: {fmt(slider.Value)}";
        grid.Controls.Add(lbl, 0, row);
        grid.Controls.Add(slider, 1, row);
        row++;
        return slider;
    }

    // Helper to add a checkbox row
    CheckBox AddCheck(string text, bool isChecked)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var chk = new CheckBox
        {
            Text = text, AutoSize = true,
            Checked = isChecked,
            ForeColor = dlgDim,
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(4, 8, 4, 4)
        };
        grid.Controls.Add(chk, 0, row);
        grid.SetColumnSpan(chk, 2);
        row++;
        return chk;
    }

    var dzSlider = AddSlider("Deadzone", 0, 50, (int)(settingDeadzone * 100), v => $"{v / 100f:F2}");
    var sensXSlider = AddSlider("Look X (Yaw)", 1, 50, (int)settingSensitivityX, v => $"{v}");
    var sensYSlider = AddSlider("Look Y (Pitch)", 1, 50, (int)settingSensitivityY, v => $"{v}");
    var cursorSlider = AddSlider("Cursor Speed", 1, 30, (int)settingCursorSpeed, v => $"{v}");
    var triggerDzSlider = AddSlider("Trigger Deadzone", 1, 50, (int)(settingTriggerDeadzone * 100), v => $"{v / 100f:F2}");
    var invertXCheck = AddCheck("Invert Look X (Yaw)", settingInvertLookX);
    var invertYCheck = AddCheck("Invert Look Y (Pitch)", settingInvertLookY);
    var smartDropCheck = AddCheck("Smart Drop (hold = stack, tap = single)", settingSmartDrop);

    // ── Gyroscope section ──
    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
    row++;
    grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    var gyroHeader = new Label
    {
        Text = "Gyroscope",
        Font = new Font("Segoe UI Semibold", 11f),
        ForeColor = dlgText, AutoSize = true,
        Margin = new Padding(0, 8, 0, 6)
    };
    grid.Controls.Add(gyroHeader, 0, row);
    grid.SetColumnSpan(gyroHeader, 2);
    row++;

    var gyroCheck = AddCheck("Enable Gyro Aiming", settingGyroEnabled);
    var gyroSensSlider = AddSlider("Gyro Sensitivity", 1, 50, (int)(settingGyroSensitivity * 10), v => $"{v / 10f:F1}");

    // ── Haptic section ──
    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
    row++;
    grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    var rumbleHeader = new Label
    {
        Text = "Haptic Feedback",
        Font = new Font("Segoe UI Semibold", 11f),
        ForeColor = dlgText, AutoSize = true,
        Margin = new Padding(0, 8, 0, 6)
    };
    grid.Controls.Add(rumbleHeader, 0, row);
    grid.SetColumnSpan(rumbleHeader, 2);
    row++;

    var rumbleCheck = AddCheck("Enable Rumble", settingRumbleEnabled);
    var rumbleSlider = AddSlider("Rumble Strength", 0, 100, (int)(settingRumbleStrength * 100), v => $"{v}%");

    outerPanel.Controls.Add(grid);

    var bottomPanel = new FlowLayoutPanel
    {
        Dock = DockStyle.Bottom, Height = 54,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(12, 10, 12, 10),
        BackColor = dlgCard
    };

    var saveBtn = new Button
    {
        Text = "\u2714  Save", AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = dlgAccent, ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Padding = new Padding(18, 5, 18, 5), Cursor = Cursors.Hand
    };
    saveBtn.FlatAppearance.BorderSize = 0;

    var resetBtn = new Button
    {
        Text = "Reset Defaults", AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(50, 54, 72),
        ForeColor = Color.FromArgb(180, 186, 200),
        Font = new Font("Segoe UI", 9f),
        Padding = new Padding(12, 5, 12, 5),
        Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand
    };
    resetBtn.FlatAppearance.BorderSize = 0;

    saveBtn.Click += (_, _) =>
    {
        // Save bindings
        foreach (var kvp in combos)
        {
            if (kvp.Value.SelectedItem is string val)
                mcBindings[kvp.Key] = val;
        }
        SaveMcBindings();

        // Save settings
        settingDeadzone = dzSlider.Value / 100f;
        settingSensitivityX = sensXSlider.Value;
        settingSensitivityY = sensYSlider.Value;
        settingCursorSpeed = cursorSlider.Value;
        settingTriggerDeadzone = triggerDzSlider.Value / 100f;
        settingInvertLookX = invertXCheck.Checked;
        settingInvertLookY = invertYCheck.Checked;
        settingSmartDrop = smartDropCheck.Checked;
        settingGyroEnabled = gyroCheck.Checked;
        settingGyroSensitivity = gyroSensSlider.Value / 10f;
        settingRumbleEnabled = rumbleCheck.Checked;
        settingRumbleStrength = rumbleSlider.Value / 100f;
        SaveMcSettings();

        AppendLog("Minecraft configuration saved.");
        dlg.Close();
    };

    resetBtn.Click += (_, _) =>
    {
        // Reset bindings
        var defaults = GetDefaultMcBindings();
        foreach (var kvp in defaults)
        {
            if (combos.TryGetValue(kvp.Key, out var combo))
            {
                var idx = Array.IndexOf(mcActionChoices, kvp.Value);
                combo.SelectedIndex = idx >= 0 ? idx : 0;
            }
        }
        // Reset settings
        dzSlider.Value = 10;
        sensXSlider.Value = 22;
        sensYSlider.Value = 16;
        cursorSlider.Value = 12;
        triggerDzSlider.Value = 5;
        invertXCheck.Checked = false;
        invertYCheck.Checked = false;
        smartDropCheck.Checked = true;
        gyroCheck.Checked = false;
        gyroSensSlider.Value = 10;
        rumbleCheck.Checked = true;
        rumbleSlider.Value = 10;
    };

    bottomPanel.Controls.Add(saveBtn);
    bottomPanel.Controls.Add(resetBtn);
    dlg.Controls.Add(outerPanel);
    dlg.Controls.Add(bottomPanel);
    dlg.Controls.Add(headerLabel);
    dlg.ShowDialog(form);
}

Dictionary<string, string> GetDefaultMcBindings()
{
    return new Dictionary<string, string>
    {
        ["Cross"] = "key:Space",
        ["Circle"] = "key:LShift",
        ["Square"] = "key:F",
        ["Triangle"] = "key:E",
        ["L1"] = "scroll:up",
        ["R1"] = "scroll:down",
        ["L2"] = "mouse:right",
        ["R2"] = "mouse:left",
        ["L3"] = "key:LCtrl",
        ["R3"] = "key:None",
        ["Options"] = "key:Escape",
        ["Share"] = "key:Tab",
        ["PS"] = "key:None",
        ["Touchpad"] = "key:T",
        ["DPadUp"] = "key:F5",
        ["DPadDown"] = "key:Q",
        ["DPadLeft"] = "mouse:middle",
        ["DPadRight"] = "key:None",
    };
}

void SaveMcBindings()
{
    try
    {
        var json = JsonSerializer.Serialize(mcBindings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(mcConfigPath, json);
    }
    catch (Exception ex)
    {
        AppendLog($"Failed to save bindings: {ex.Message}");
    }
}

void LoadMcBindings()
{
    try
    {
        if (!File.Exists(mcConfigPath)) return;
        var json = File.ReadAllText(mcConfigPath);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (loaded is null) return;
        foreach (var kvp in loaded)
        {
            if (mcBindings.ContainsKey(kvp.Key))
            {
                mcBindings[kvp.Key] = kvp.Value;
            }
        }
    }
    catch
    {
    }
}

byte McResolveVk(string bindingKey)
{
    if (!mcBindings.TryGetValue(bindingKey, out var action)) return 0;
    if (!action.StartsWith("key:")) return 0;
    var keyName = action.Substring(4);
    return mcKeyNameToVk.TryGetValue(keyName, out var vk) ? vk : (byte)0;
}

bool McIsScrollAction(string bindingKey, out int direction)
{
    direction = 0;
    if (!mcBindings.TryGetValue(bindingKey, out var action)) return false;
    if (action == "scroll:up") { direction = 120; return true; }
    if (action == "scroll:down") { direction = -120; return true; }
    return false;
}

void McApplyBinding(string bindingKey, bool pressed)
{
    if (!mcBindings.TryGetValue(bindingKey, out var action) || action == "key:None") return;

    if (action.StartsWith("key:"))
    {
        var vk = McResolveVk(bindingKey);
        if (vk != 0) McSetKeyState(vk, pressed);
    }
    else if (action.StartsWith("mouse:"))
    {
        var mouseType = action.Substring(6);
        switch (mouseType)
        {
            case "left":
                McSetMouseState(ref mcLeftMouseDown, pressed, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                break;
            case "right":
                McSetMouseState(ref mcRightMouseDown, pressed, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
                break;
            case "middle":
                McSetMouseState(ref mcMiddleMouseDown, pressed, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
                break;
        }
    }
    else if (action.StartsWith("scroll:") && pressed)
    {
        if (McIsScrollAction(bindingKey, out var dir))
        {
            NativeInput.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, dir, UIntPtr.Zero);
        }
    }
    else if (action.StartsWith("combo:"))
    {
        if (pressed && mcActiveComboKeys.Add(bindingKey))
        {
            var combo = action.Substring(6);
            switch (combo)
            {
                case "ctrl+q":
                    // Use raw keybd_event for momentary Ctrl so we don't clear sprint toggle's tracked LCtrl state
                    NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);           // LCtrl down
                    NativeInput.keybd_event(0x51, 0, 0, UIntPtr.Zero);           // Q down
                    NativeInput.keybd_event(0x51, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Q up
                    NativeInput.keybd_event(0xA2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // LCtrl up
                    if (mcSprintToggled && McResolveVk("L3") == 0xA2) NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);
                    break;
                case "ctrl+middle":
                    NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);           // LCtrl down
                    NativeInput.mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
                    NativeInput.mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                    NativeInput.keybd_event(0xA2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // LCtrl up
                    if (mcSprintToggled && McResolveVk("L3") == 0xA2) NativeInput.keybd_event(0xA2, 0, 0, UIntPtr.Zero);
                    break;
            }
        }
        else if (!pressed)
        {
            mcActiveComboKeys.Remove(bindingKey);
        }
    }
}

bool IsCursorVisible()
{
    var ci = new NativeInput.CURSORINFO();
    ci.cbSize = Marshal.SizeOf<NativeInput.CURSORINFO>();
    return NativeInput.GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0;
}

bool GyroIndexesExist(byte[] raw, int reportOffset)
{
    return GyroZLoByte + 1 + reportOffset < raw.Length;
}

void TryApplyRumble(HidDevice device, byte rightMotor, byte leftMotor)
{
    try
    {
        if (!device.IsOpen || !device.IsConnected) return;

        var report = CreateDualSenseOutputReport(device);
        report[GetDualSenseReportByte(device, DualSenseUsbMotorFlagByte, DualSenseBluetoothMotorFlagByte)] |= DualSenseMotorEnableFlag;
        report[GetDualSenseReportByte(device, DualSenseUsbRightMotorByte, DualSenseBluetoothRightMotorByte)] = rightMotor;
        report[GetDualSenseReportByte(device, DualSenseUsbLeftMotorByte, DualSenseBluetoothLeftMotorByte)] = leftMotor;
        FinalizeDualSenseOutputReport(device, report);
        device.Write(report, 100);
    }
    catch { }
}

void SaveMcSettings()
{
    try
    {
        var settings = new Dictionary<string, object>
        {
            ["deadzone"] = settingDeadzone,
            ["sensitivityX"] = settingSensitivityX,
            ["sensitivityY"] = settingSensitivityY,
            ["cursorSpeed"] = settingCursorSpeed,
            ["triggerDeadzone"] = settingTriggerDeadzone,
            ["invertLookX"] = settingInvertLookX,
            ["invertLookY"] = settingInvertLookY,
            ["smartDrop"] = settingSmartDrop,
            ["gyroEnabled"] = settingGyroEnabled,
            ["gyroSensitivity"] = settingGyroSensitivity,
            ["rumbleEnabled"] = settingRumbleEnabled,
            ["rumbleStrength"] = settingRumbleStrength,
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(mcSettingsPath, json);
    }
    catch (Exception ex)
    {
        AppendLog($"Failed to save settings: {ex.Message}");
    }
}

void LoadMcSettings()
{
    try
    {
        if (!File.Exists(mcSettingsPath)) return;
        var json = File.ReadAllText(mcSettingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("deadzone", out var dz)) settingDeadzone = Math.Clamp((float)dz.GetDouble(), 0f, 0.5f);
        if (root.TryGetProperty("sensitivityX", out var sx)) settingSensitivityX = Math.Clamp((float)sx.GetDouble(), 1f, 50f);
        if (root.TryGetProperty("sensitivityY", out var sy)) settingSensitivityY = Math.Clamp((float)sy.GetDouble(), 1f, 50f);
        if (root.TryGetProperty("cursorSpeed", out var cs)) settingCursorSpeed = Math.Clamp((float)cs.GetDouble(), 1f, 30f);
        if (root.TryGetProperty("triggerDeadzone", out var td)) settingTriggerDeadzone = Math.Clamp((float)td.GetDouble(), 0.01f, 0.5f);
        if (root.TryGetProperty("invertLookX", out var ilx)) settingInvertLookX = ilx.GetBoolean();
        if (root.TryGetProperty("invertLookY", out var ily)) settingInvertLookY = ily.GetBoolean();
        if (root.TryGetProperty("smartDrop", out var sd)) settingSmartDrop = sd.GetBoolean();
        if (root.TryGetProperty("gyroEnabled", out var ge)) settingGyroEnabled = ge.GetBoolean();
        if (root.TryGetProperty("gyroSensitivity", out var gs)) settingGyroSensitivity = Math.Clamp((float)gs.GetDouble(), 0.1f, 5f);
        if (root.TryGetProperty("rumbleEnabled", out var re)) settingRumbleEnabled = re.GetBoolean();
        if (root.TryGetProperty("rumbleStrength", out var rs)) settingRumbleStrength = Math.Clamp((float)rs.GetDouble(), 0f, 1f);
    }
    catch { }
}

void OpenDevice(HidDevice device)
{
    if (device.IsOpen)
    {
        return;
    }

    try
    {
        device.OpenDevice(
            DeviceMode.Overlapped,
            DeviceMode.Overlapped,
            (ShareMode)((int)ShareMode.ShareRead | (int)ShareMode.ShareWrite));
    }
    catch (Exception ex) when (IsSpuriousSuccessException(ex) && device.IsOpen)
    {
        return;
    }

    if (!device.IsOpen)
    {
        throw new InvalidOperationException("Failed to open the HID device.");
    }
}

void PrepareDeviceForLowLatency(HidDevice device)
{
    if (!device.IsOpen)
    {
        return;
    }

    var readHandle = device.ReadHandle;
    if (readHandle == IntPtr.Zero || readHandle == new IntPtr(-1))
    {
        return;
    }

    NativeHid.HidD_SetNumInputBuffers(readHandle, PreferredHidInputBufferCount);
    NativeHid.HidD_FlushQueue(readHandle);
}

bool TryApplyLightbarColor(HidDevice device, Color color, bool initializeLightbar, bool logErrors)
{
    var openedHere = false;

    try
    {
        if (!device.IsOpen)
        {
            OpenDevice(device);
            openedHere = true;
        }

        if (initializeLightbar)
        {
            var setupReport = CreateDualSenseLightbarSetupReport(device);
            if (!device.Write(setupReport, ReadTimeoutMs))
            {
                throw new InvalidOperationException("Failed to initialize the DualSense lightbar.");
            }
        }

        var colorReport = CreateDualSenseLightbarColorReport(device, color);
        if (!device.Write(colorReport, ReadTimeoutMs))
        {
            throw new InvalidOperationException("Failed to send the DualSense lightbar color.");
        }

        return true;
    }
    catch (Exception ex) when (IsSpuriousSuccessException(ex))
    {
        return true;
    }
    catch (Exception ex)
    {
        if (logErrors)
        {
            AppendLog($"Lightbar update failed: {ex.Message}");
        }

        return false;
    }
    finally
    {
        if (openedHere)
        {
            try
            {
                device.CloseDevice();
            }
            catch
            {
            }
        }
    }
}

byte[] CreateDualSenseLightbarSetupReport(HidDevice device)
{
    var report = CreateDualSenseOutputReport(device);
    report[GetDualSenseReportByte(device, DualSenseUsbValidFlag2Byte, DualSenseBluetoothValidFlag2Byte)] |= DualSenseLightbarSetupControlEnableFlag;
    report[GetDualSenseReportByte(device, DualSenseUsbLightbarSetupByte, DualSenseBluetoothLightbarSetupByte)] = DualSenseLightbarSetupLightOut;
    FinalizeDualSenseOutputReport(device, report);
    return report;
}

byte[] CreateDualSenseLightbarColorReport(HidDevice device, Color color)
{
    var report = CreateDualSenseOutputReport(device);
    report[GetDualSenseReportByte(device, DualSenseUsbValidFlag1Byte, DualSenseBluetoothValidFlag1Byte)] |= DualSenseLightbarControlEnableFlag;
    report[GetDualSenseReportByte(device, DualSenseUsbLightbarRedByte, DualSenseBluetoothLightbarRedByte)] = color.R;
    report[GetDualSenseReportByte(device, DualSenseUsbLightbarGreenByte, DualSenseBluetoothLightbarGreenByte)] = color.G;
    report[GetDualSenseReportByte(device, DualSenseUsbLightbarBlueByte, DualSenseBluetoothLightbarBlueByte)] = color.B;
    FinalizeDualSenseOutputReport(device, report);
    return report;
}

byte[] CreateDualSenseOutputReport(HidDevice device)
{
    if (IsBluetoothConnection(device))
    {
        var report = new byte[DualSenseOutputReportBluetoothLength];
        report[0] = DualSenseOutputReportBluetoothId;
        report[1] = (byte)((dualSenseOutputSequence & 0x0F) << 4);
        report[2] = DualSenseOutputTag;
        dualSenseOutputSequence = (byte)((dualSenseOutputSequence + 1) & 0x0F);
        return report;
    }

    var usbReport = new byte[DualSenseOutputReportUsbLength];
    usbReport[0] = DualSenseOutputReportUsbId;
    return usbReport;
}

int GetDualSenseReportByte(HidDevice device, int usbIndex, int bluetoothIndex)
{
    return IsBluetoothConnection(device) ? bluetoothIndex : usbIndex;
}

void FinalizeDualSenseOutputReport(HidDevice device, byte[] report)
{
    if (!IsBluetoothConnection(device))
    {
        return;
    }

    var crc = ComputeDualSenseBluetoothCrc(report, report.Length - 4);
    report[^4] = (byte)(crc & 0xFF);
    report[^3] = (byte)((crc >> 8) & 0xFF);
    report[^2] = (byte)((crc >> 16) & 0xFF);
    report[^1] = (byte)((crc >> 24) & 0xFF);
}

uint ComputeDualSenseBluetoothCrc(byte[] report, int lengthWithoutCrc)
{
    var crc = UpdateCrc32(0xFFFFFFFFu, DualSenseOutputCrcSeed);

    for (var i = 0; i < lengthWithoutCrc; i++)
    {
        crc = UpdateCrc32(crc, report[i]);
    }

    return ~crc;
}

uint UpdateCrc32(uint crc, byte value)
{
    crc ^= value;

    for (var i = 0; i < 8; i++)
    {
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }

    return crc;
}

void ConnectVirtualController(IXbox360Controller xbox)
{
    try
    {
        xbox.Connect();
    }
    catch (Exception ex) when (IsSpuriousSuccessException(ex))
    {
    }
}

bool RawIndexesExist(byte[] raw, int reportOffset)
{
    return LeftStickXByte + reportOffset < raw.Length &&
           LeftStickYByte + reportOffset < raw.Length &&
           RightStickXByte + reportOffset < raw.Length &&
           RightStickYByte + reportOffset < raw.Length &&
           LeftTriggerByte + reportOffset < raw.Length &&
           RightTriggerByte + reportOffset < raw.Length &&
           Buttons1Byte + reportOffset < raw.Length &&
           Buttons2Byte + reportOffset < raw.Length &&
           Buttons3Byte + reportOffset < raw.Length;
}

bool IsSpuriousSuccessException(Exception ex)
{
    if (ex is Win32Exception win32 && win32.NativeErrorCode == 0)
    {
        return true;
    }

    if (ex is ExternalException && (ex.HResult & 0xFFFF) == 0)
    {
        return true;
    }

    return ex.Message.Contains("operation completed successfully", StringComparison.OrdinalIgnoreCase);
}

bool TryEnableHighResolutionTimer()
{
    try
    {
        return Winmm.timeBeginPeriod(RequestedTimerResolutionMs) == 0;
    }
    catch
    {
        return false;
    }
}

void DisableHighResolutionTimer(bool enabled)
{
    if (!enabled)
    {
        return;
    }

    try
    {
        Winmm.timeEndPeriod(RequestedTimerResolutionMs);
    }
    catch
    {
    }
}

void AppendLog(string message)
{
    if (form.IsDisposed)
    {
        return;
    }

    if (logBox.InvokeRequired)
    {
        logBox.BeginInvoke(new Action<string>(AppendLog), message);
        return;
    }

    logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    logBox.SelectionStart = logBox.TextLength;
    logBox.ScrollToCaret();
}

void SetStatus(string text)
{
    if (form.IsDisposed)
    {
        return;
    }

    if (statusLabel.InvokeRequired)
    {
        statusLabel.BeginInvoke(new Action<string>(SetStatus), text);
        return;
    }

    statusLabel.Text = $"\u25CF  Status: {text}";

    var normalized = text.Trim().ToLowerInvariant();
    var (backColor, foreColor) = normalized switch
    {
        "idle" => (Color.FromArgb(36, 38, 52), Color.FromArgb(140, 148, 168)),
        "mapping running" => (Color.FromArgb(28, 48, 60), Color.FromArgb(88, 200, 255)),
        "minecraft mapping running" => (Color.FromArgb(24, 50, 36), Color.FromArgb(80, 220, 130)),
        "device ready" => (Color.FromArgb(28, 46, 38), Color.FromArgb(76, 217, 130)),
        "no controller found" => (Color.FromArgb(52, 44, 28), Color.FromArgb(255, 183, 77)),
        "error" => (Color.FromArgb(56, 28, 28), Color.FromArgb(255, 92, 92)),
        "stopping..." => (Color.FromArgb(48, 40, 28), Color.FromArgb(220, 180, 90)),
        _ => (Color.FromArgb(36, 38, 52), Color.FromArgb(140, 148, 168))
    };

    statusLabel.BackColor = backColor;
    statusLabel.ForeColor = foreColor;
}

string FormatDeviceListItem((HidDevice Device, string Product, string Manufacturer, int InputLength, int OutputLength, int FeatureLength) device)
{
    return $"[{GetConnectionLabel(device.Device)}] VID 0x{device.Device.Attributes.VendorId:X4} PID 0x{device.Device.Attributes.ProductId:X4} | {NullToUnknown(device.Product)}";
}

Color ColorFromHue(int hue)
{
    var normalizedHue = ((hue % 360) + 360) % 360;
    var sector = normalizedHue / 60.0;
    var chroma = 1.0;
    var x = chroma * (1.0 - Math.Abs(sector % 2.0 - 1.0));

    var (red, green, blue) = sector switch
    {
        >= 0 and < 1 => (chroma, x, 0.0),
        >= 1 and < 2 => (x, chroma, 0.0),
        >= 2 and < 3 => (0.0, chroma, x),
        >= 3 and < 4 => (0.0, x, chroma),
        >= 4 and < 5 => (x, 0.0, chroma),
        _ => (chroma, 0.0, x)
    };

    return Color.FromArgb(
        (int)Math.Round(red * 255.0),
        (int)Math.Round(green * 255.0),
        (int)Math.Round(blue * 255.0));
}

short MapUnsignedByteToXboxAxis(byte value, bool invert)
{
    var centered = value - 128;
    var scaled = centered * 256;

    if (value == 255)
    {
        scaled = short.MaxValue;
    }
    else if (value == 0)
    {
        scaled = short.MinValue;
    }

    if (invert)
    {
        scaled = -scaled;
    }

    return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
}

bool IsPrimaryControllerInterface(HidDevice device)
{
    return device.Capabilities.UsagePage == GenericDesktopUsagePage &&
           (device.Capabilities.Usage == GamepadUsage || device.Capabilities.Usage == JoystickUsage);
}

bool IsBluetoothConnection(HidDevice device)
{
    return device.DevicePath.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
           device.Capabilities.InputReportByteLength > 64;
}

string GetConnectionLabel(HidDevice device)
{
    return IsBluetoothConnection(device) ? "Bluetooth" : "USB";
}

int GetReportOffset(HidDevice device, byte[] raw)
{
    if (raw.Length == 0)
    {
        return -1;
    }

    if (raw[0] == 0x31)
    {
        return 1;
    }

    if (IsBluetoothConnection(device))
    {
        return -1;
    }

    return 0;
}

int ScoreDualSenseLikelihood(string? product, string? manufacturer)
{
    var score = 0;

    if (!string.IsNullOrWhiteSpace(product) && product.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
    {
        score += 100;
    }

    if (!string.IsNullOrWhiteSpace(product) && product.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase))
    {
        score += 25;
    }

    if (!string.IsNullOrWhiteSpace(manufacturer) && manufacturer.Contains("Sony", StringComparison.OrdinalIgnoreCase))
    {
        score += 10;
    }

    return score;
}

string TryReadProduct(HidDevice device)
{
    try
    {
        return device.ReadProduct(out var data) ? DecodeUsbString(data) : string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

string TryReadManufacturer(HidDevice device)
{
    try
    {
        return device.ReadManufacturer(out var data) ? DecodeUsbString(data) : string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

string DecodeUsbString(byte[] data)
{
    return Encoding.Unicode.GetString(data).TrimEnd('\0', ' ');
}

string NullToUnknown(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}

Icon? LoadAppIcon()
{
    try
    {
        var exePath = Environment.ProcessPath;
        if (exePath != null)
            return Icon.ExtractAssociatedIcon(exePath);
    }
    catch { }
    return null;
}

static class Winmm
{
    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint uPeriod);
}

static class NativeHid
{
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_FlushQueue(IntPtr hidDeviceObject);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_SetNumInputBuffers(IntPtr hidDeviceObject, int numberBuffers);
}

static class NativeInput
{
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int ptScreenPosX;
        public int ptScreenPosY;
    }
}

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        const string mutexName = "AudioCrossfader_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("Audio Crossfader is already running.", "Audio Crossfader", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());

        _mutex.ReleaseMutex();
    }
}

// Manages System Tray and Background Worker
class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon? _trayIcon;
    private AppSettings _settings;
    private AudioCrossfaderWorker? _crossfaderWorker;
    private SettingsForm? _settingsForm;
    private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public TrayApplicationContext()
    {
        _settings = LoadSettings();

        // Setup System Tray Icon with robust fallback loading
        ContextMenuStrip contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Settings...", null, (s, e) => ShowSettings());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());

        Icon trayIconAsset = SystemIcons.Application;
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                trayIconAsset = new Icon(iconPath);
            }
        }
        catch { }

        _trayIcon = new NotifyIcon
        {
            Icon = trayIconAsset,
            ContextMenuStrip = contextMenu,
            Visible = true,
            Text = "Audio Crossfader"
        };
        _trayIcon.DoubleClick += (s, e) => ShowSettings();

        StartWorker();
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings newSettings)
    {
        _settings = newSettings;
        try
        {
            string json = JsonSerializer.Serialize(_settings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }

        _crossfaderWorker?.Stop();
        StartWorker();
    }

    private void StartWorker()
    {
        _crossfaderWorker = new AudioCrossfaderWorker(_settings);
        _crossfaderWorker.Start();
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, this);
            _settingsForm.Show();
        }
        else
        {
            _settingsForm.BringToFront();
        }
    }

    private void ExitApp()
    {
        _crossfaderWorker?.Stop();
        _trayIconFormCleanup();
        Application.Exit();
    }

    private void _trayIconFormCleanup()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
    }
}

class AppSettings
{
    public string Source1 { get; set; } = "chrome.exe";
    public string Source2 { get; set; } = "firefox.exe";
    public bool RunOnStartup { get; set; } = false;
    
    public int KeyUp { get; set; } = 0x7E;    // VK_F15
    public int KeyMute { get; set; } = 0x7F;   // VK_F16
    public int KeyDown { get; set; } = 0x80;   // VK_F17

    public string UnmutedColor { get; set; } = "#00A8FF"; // Modern Electric Blue
    public string MutedColor { get; set; } = "#FF4757";   // Modern Coral / Orange-Red
    public string OverlayPosition { get; set; } = "Bottom Center";
    public int PopupDurationMs { get; set; } = 2500;

    public int OverlayWidth { get; set; } = 340;
    public int OverlayHeight { get; set; } = 75;
    public bool EnableInterpolation { get; set; } = true;
}

class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly AppSettings _settings;
    private float _position = 0.5f;
    private float _targetPosition = 0.5f;
    private int _muteState = 0;
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private bool _running = false;

    private DateTime _lastCenterStepTime = DateTime.MinValue;
    private const double CenterDetentDurationMs = 400;

    public AudioCrossfaderWorker(AppSettings settings)
    {
        _settings = settings;
        _targetPosition = _position;
        _deviceEnumerator = new MMDeviceEnumerator();
    }

    public void Start()
    {
        _running = true;
        _workerThread = new Thread(RunLoop);
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();
    }

    public void Stop()
    {
        _running = false;
        ResetVolumesToFull();
        _overlay?.Invoke(new Action(() => _overlay.Close()));
    }

    private void ResetVolumesToFull()
    {
        try
        {
            SetApplicationVolume(_settings.Source1, 1.0f);
            SetApplicationVolume(_settings.Source2, 1.0f);
        }
        catch { }
    }

    private void RunLoop()
    {
        string app1KeyName = _settings.Source1.ToLower().Replace(".exe", "");
        string app2KeyName = _settings.Source2.ToLower().Replace(".exe", "");

        Thread uiThread = new Thread(() =>
        {
            _overlay = new VolumeOverlayForm(app1KeyName, app2KeyName, _settings);
            Application.Run(_overlay);
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        Thread.Sleep(200);
        ApplyVolumes(true, true);

        DateTime lastAudioApplyTime = DateTime.MinValue;
        bool lastMuteState = false;

        while (_running)
        {
            bool physicalInputRegistered = false;

            // Drain-loop to catch every single micro-click from high-speed encoder knobs
            int upSteps = 0;
            while ((GetAsyncKeyState(_settings.KeyUp) & 0x0001) != 0) { upSteps++; }

            int downSteps = 0;
            while ((GetAsyncKeyState(_settings.KeyDown) & 0x0001) != 0) { downSteps++; }

            bool currentMute = (GetAsyncKeyState(_settings.KeyMute) & 0x8000) != 0;
            if (currentMute && !lastMuteState) 
            { 
                CycleMuteState(); 
                physicalInputRegistered = true; 
            }
            lastMuteState = currentMute;

            if (upSteps > 0)
            {
                Step(0.05f * upSteps);
                physicalInputRegistered = true;
            }
            if (downSteps > 0)
            {
                Step(-0.05f * downSteps);
                physicalInputRegistered = true;
            }

            // Handle smooth interpolation if enabled
            if (_settings.EnableInterpolation)
            {
                float diff = _targetPosition - _position;
                if (Math.Abs(diff) > 0.0005f)
                {
                    _position += diff * 0.5f; // Fast, fluid interpolation factor
                }
                else
                {
                    _position = _targetPosition;
                }
            }
            else
            {
                _position = _targetPosition;
            }

            // Update UI/Overlay every frame
            ApplyVolumes(false, physicalInputRegistered);

            // Throttle Windows Audio API session updates to prevent CPU lag
            var now = DateTime.Now;
            if (physicalInputRegistered && (now - lastAudioApplyTime).TotalMilliseconds >= 15)
            {
                ApplyVolumes(true, false);
                lastAudioApplyTime = now;
            }

            Thread.Sleep(2); // Ultra-responsive tight loop
        }
    }

    public void Step(float delta)
    {
        float newTarget = _targetPosition + delta;

        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            var now = DateTime.Now;
            if ((now - _lastCenterStepTime).TotalMilliseconds > CenterDetentDurationMs)
            {
                _lastCenterStepTime = now;
                return;
            }
            else
            {
                _targetPosition = Math.Clamp(newTarget, 0.0f, 1.0f);
                _lastCenterStepTime = DateTime.MinValue;
            }
        }
        else
        {
            if ((_targetPosition < 0.5f && newTarget > 0.5f) || (_targetPosition > 0.5f && newTarget < 0.5f))
            {
                _targetPosition = 0.5f;
                _lastCenterStepTime = DateTime.Now;
            }
            else
            {
                _targetPosition = Math.Clamp(newTarget, 0.0f, 1.0f);
            }
        }
    }

    public void CycleMuteState()
    {
        _muteState = (_muteState + 1) % 3;
    }

    private void ApplyVolumes(bool updateAudioSessions, bool forceShowPopup)
    {
        float baseApp1Vol;
        float baseApp2Vol;

        if (_position >= 0.5f)
        {
            baseApp1Vol = 1.0f;
            baseApp2Vol = 1.0f - ((_position - 0.5f) * 2.0f);
        }
        else
        {
            baseApp2Vol = 1.0f;
            baseApp1Vol = _position * 2.0f;
        }

        baseApp1Vol = Math.Clamp(baseApp1Vol, 0.0f, 1.0f);
        baseApp2Vol = Math.Clamp(baseApp2Vol, 0.0f, 1.0f);

        float actualApp1Vol = baseApp1Vol;
        float actualApp2Vol = baseApp2Vol;

        if (_muteState == 1) actualApp2Vol = 0.0f;
        else if (_muteState == 2) actualApp1Vol = 0.0f;

        if (updateAudioSessions)
        {
            SetApplicationVolume(_settings.Source1, actualApp1Vol);
            SetApplicationVolume(_settings.Source2, actualApp2Vol);
        }

        _overlay?.UpdateVolumes(baseApp1Vol, baseApp2Vol, _muteState, forceShowPopup);
    }

    private void SetApplicationVolume(string targetApp, float volume)
    {
        try
        {
            string targetKey = targetApp.ToLower().Replace(".exe", "");
            var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var pid = session.GetProcessID;
                    if (pid > 0)
                    {
                        try
                        {
                            var proc = Process.GetProcessById((int)pid);
                            if (proc.ProcessName.ToLower().Contains(targetKey))
                            {
                                session.SimpleAudioVolume.Volume = volume;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }
}

// Configuration Menu Window (Win 11 Dark Mode & Interactive Key Recorder)
class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private TextBox _txtSource1;
    private TextBox _txtSource2;
    private TextBox _txtKeyUp;
    private TextBox _txtKeyMute;
    private TextBox _txtKeyDown;
    private ComboBox _cmbPosition;
    private TextBox _txtPopupDuration;
    private TextBox _txtWidth;
    private TextBox _txtHeight;
    private CheckBox _chkStartup;
    private CheckBox _chkInterpolation;
    private TrayApplicationContext _context;
    
    private string _currentUnmutedHex;
    private string _currentMutedHex;

    private bool _isRecording = false;
    private Button? _activeRecordButton = null;
    private TextBox? _activeTargetTextBox = null;
    private string _previousButtonText = "";
    private KeyCaptureFilter? _mouseFilter;

    public SettingsForm(AppSettings settings, TrayApplicationContext context)
    {
        _context = context;
        _currentUnmutedHex = settings.UnmutedColor;
        _currentMutedHex = settings.MutedColor;

        this.Text = "Audio Crossfader Settings";
        this.Size = new Size(380, 680);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.KeyPreview = true;

        this.BackColor = Color.FromArgb(32, 32, 32);
        this.ForeColor = Color.FromArgb(240, 240, 240);

        int y = 20;

        AddLabel("Source 1 (e.g. chrome.exe):", 20, ref y);
        _txtSource1 = AddTextBox(settings.Source1, 20, ref y);

        AddLabel("Source 2 (e.g. firefox.exe):", 20, ref y);
        _txtSource2 = AddTextBox(settings.Source2, 20, ref y);

        AddLabel("Keybinds:", 20, ref y);
        
        _txtKeyUp = AddKeyBindRow("Up / Right Key:", $"0x{settings.KeyUp:X}", ref y);
        _txtKeyDown = AddKeyBindRow("Down / Left Key:", $"0x{settings.KeyDown:X}", ref y);
        _txtKeyMute = AddKeyBindRow("Cycle Mute Key:", $"0x{settings.KeyMute:X}", ref y);

        y += 5;
        AddLabel("Overlay Colors:", 20, ref y);

        AddColorPickerRow("Unmuted Color:", _currentUnmutedHex, (hex) => _currentUnmutedHex = hex, ref y);
        AddColorPickerRow("Muted Color:", _currentMutedHex, (hex) => _currentMutedHex = hex, ref y);

        y += 5;
        AddLabel("Overlay Position:", 20, ref y);
        _cmbPosition = new ComboBox
        {
            Left = 160,
            Top = y - 3,
            Width = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbPosition.Items.AddRange(new object[] { "Bottom Center", "Top Center", "Bottom Right", "Bottom Left", "Top Right", "Top Left" });
        _cmbPosition.SelectedItem = settings.OverlayPosition;
        if (_cmbPosition.SelectedIndex == -1) _cmbPosition.SelectedIndex = 0;
        Controls.Add(_cmbPosition);
        y += 30;

        AddLabel("Popup Duration (ms, 500 - 10000):", 20, ref y);
        _txtPopupDuration = AddTextBox(settings.PopupDurationMs.ToString(), 20, ref y);

        AddLabel("Overlay Size (Width: 100-2000, Height: 50-1000):", 20, ref y);
        _txtWidth = AddNumericRow("Width:", settings.OverlayWidth.ToString(), 20, ref y);
        _txtHeight = AddNumericRow("Height:", settings.OverlayHeight.ToString(), 20, ref y);

        _chkInterpolation = new CheckBox { Text = "Smooth Volume Bar Interpolation", Checked = settings.EnableInterpolation, Left = 20, Top = y, Width = 320, ForeColor = Color.White };
        Controls.Add(_chkInterpolation);
        y += 30;

        _chkStartup = new CheckBox { Text = "Run on Windows Startup", Checked = settings.RunOnStartup, Left = 20, Top = y, Width = 320, ForeColor = Color.White };
        Controls.Add(_chkStartup);

        Button btnSave = new Button 
        { 
            Text = "Save & Apply", 
            Left = 230, 
            Top = (y += 35), 
            Width = 110, 
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => SaveChanges();
        Controls.Add(btnSave);

        _mouseFilter = new KeyCaptureFilter(CancelRecording);
        Application.AddMessageFilter(_mouseFilter);

        this.FormClosed += (s, e) =>
        {
            if (_mouseFilter != null) Application.RemoveMessageFilter(_mouseFilter);
        };

        this.KeyDown += SettingsForm_KeyDown;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(this.Handle, 20, ref useImmersiveDarkMode, sizeof(int));
            
            int preference = 2;
            DwmSetWindowAttribute(this.Handle, 33, ref preference, sizeof(int));
        }
        catch { }
    }

    private void SettingsForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;

        if (e.KeyCode == Keys.Escape)
        {
            CancelRecording();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
            return;

        int vk = (int)e.KeyCode;
        if (_activeTargetTextBox != null)
        {
            _activeTargetTextBox.Text = $"0x{vk:X}";
        }

        StopRecording();
        e.Handled = true;
    }

    private void StartRecording(Button btn, TextBox targetBox)
    {
        if (_isRecording) StopRecording();

        _isRecording = true;
        _activeRecordButton = btn;
        _activeTargetTextBox = targetBox;
        _previousButtonText = btn.Text;

        btn.Text = "Press any key...";
        btn.BackColor = Color.FromArgb(200, 100, 0);
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        if (_activeRecordButton != null)
        {
            _activeRecordButton.Text = _previousButtonText;
            _activeRecordButton.BackColor = Color.FromArgb(60, 60, 60);
        }
        _isRecording = false;
        _activeRecordButton = null;
        _activeTargetTextBox = null;
    }

    private void CancelRecording()
    {
        if (!_isRecording) return;
        StopRecording();
    }

    private class KeyCaptureFilter : IMessageFilter
    {
        private readonly Action _onMouseClick;
        public KeyCaptureFilter(Action onMouseClick) => _onMouseClick = onMouseClick;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x0201 || m.Msg == 0x0204)
            {
                _onMouseClick();
            }
            return false;
        }
    }

    private void AddLabel(string text, int left, ref int top)
    {
        Label lbl = new Label { Text = text, Left = left, Top = top, Width = 320, ForeColor = Color.FromArgb(200, 200, 200) };
        Controls.Add(lbl);
        top += 20;
    }

    private TextBox AddTextBox(string text, int left, ref int top)
    {
        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = left, 
            Top = top, 
            Width = 320, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txt);
        top += 30;
        return txt;
    }

    private TextBox AddNumericRow(string labelText, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = left, Top = top + 3, Width = 60, ForeColor = Color.FromArgb(200, 200, 200) };
        Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = left + 65, 
            Top = top, 
            Width = 90, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txt);
        top += 30;
        return txt;
    }

    private TextBox AddKeyBindRow(string labelText, string initialHex, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 130, ForeColor = Color.FromArgb(200, 200, 200) };
        Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = initialHex, 
            Left = 160, 
            Top = top, 
            Width = 80, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };
        Controls.Add(txt);

        Button btnRecord = new Button
        {
            Text = "Record",
            Left = 248,
            Top = top - 1,
            Width = 92,
            Height = 24,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRecord.FlatAppearance.BorderSize = 0;
        btnRecord.Click += (s, e) => StartRecording(btnRecord, txt);
        Controls.Add(btnRecord);

        top += 28;
        return txt;
    }

    private void AddColorPickerRow(string labelText, string initialHex, Action<string> onColorChanged, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 130, ForeColor = Color.FromArgb(200, 200, 200) };
        Controls.Add(lbl);

        Panel colorBox = new Panel 
        { 
            Left = 160, 
            Top = top, 
            Width = 180, 
            Height = 22, 
            BackColor = ColorTranslator.FromHtml(initialHex),
            Cursor = Cursors.Hand
        };

        colorBox.Paint += (s, e) =>
        {
            using Pen p = new Pen(Color.FromArgb(80, 80, 80));
            e.Graphics.DrawRectangle(p, 0, 0, colorBox.Width - 1, colorBox.Height - 1);
        };

        colorBox.Click += (s, e) =>
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                try { dlg.Color = colorBox.BackColor; } catch { }
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    colorBox.BackColor = dlg.Color;
                    string hex = ColorTranslator.ToHtml(dlg.Color);
                    onColorChanged(hex);
                }
            }
        };

        Controls.Add(colorBox);
        top += 28;
    }

    private void SaveChanges()
    {
        int ParseHexKey(string text, int defaultVal)
        {
            try
            {
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(text, 16);
                return int.Parse(text);
            }
            catch
            {
                return defaultVal;
            }
        }

        int ParseInt(string text, int defaultVal)
        {
            return int.TryParse(text, out int val) ? val : defaultVal;
        }

        var newSettings = new AppSettings
        {
            Source1 = _txtSource1.Text.Trim(),
            Source2 = _txtSource2.Text.Trim(),
            RunOnStartup = _chkStartup.Checked,
            KeyUp = ParseHexKey(_txtKeyUp.Text, 0x7E),
            KeyMute = ParseHexKey(_txtKeyMute.Text, 0x7F),
            KeyDown = ParseHexKey(_txtKeyDown.Text, 0x80),
            UnmutedColor = _currentUnmutedHex,
            MutedColor = _currentMutedHex,
            OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center",
            PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000),
            OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 340), 100, 2000),
            OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 75), 50, 1000),
            EnableInterpolation = _chkInterpolation.Checked
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (newSettings.RunOnStartup)
            {
                key?.SetValue("AudioCrossfader", $"\"{exePath}\"");
            }
            else
            {
                key?.DeleteValue("AudioCrossfader", false);
            }
        }
        catch { }

        _context.SaveSettings(newSettings);
    }
}

// On-Screen Overlay Popup (Rounded Rectangle Bar Style & Custom Position)
class VolumeOverlayForm : Form
{
    private readonly string _app1Label;
    private readonly string _app2Label;
    private readonly AppSettings _settings;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private int _totalDurationMs = 2500;
    private bool _isFadingOut = false;

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = app1.Replace(".exe", "").ToUpper();
        _app2Label = app2.Replace(".exe", "").ToUpper();
        _settings = settings;

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ClientSize = new Size(Math.Max(100, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

        _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);

        // Calculate position based on user settings (~65px gap from taskbar/screen edge)
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int x = workingArea.Left + (workingArea.Width - this.Width) / 2;
        int y = workingArea.Bottom - this.Height - 65;

        switch (_settings.OverlayPosition)
        {
            case "Top Center":
                x = workingArea.Left + (workingArea.Width - this.Width) / 2;
                y = workingArea.Top + 40;
                break;
            case "Bottom Right":
                x = workingArea.Right - this.Width - 40;
                y = workingArea.Bottom - this.Height - 65;
                break;
            case "Bottom Left":
                x = workingArea.Left + 40;
                y = workingArea.Bottom - this.Height - 65;
                break;
            case "Top Right":
                x = workingArea.Right - this.Width - 40;
                y = workingArea.Top + 40;
                break;
            case "Top Left":
                x = workingArea.Left + 40;
                y = workingArea.Top + 40;
                break;
            case "Bottom Center":
            default:
                x = workingArea.Left + (workingArea.Width - this.Width) / 2;
                y = workingArea.Bottom - this.Height - 65;
                break;
        }

        this.Location = new Point(x, y);
        this.KeyPreview = true;
        this.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                _displayCountdownMs = 0;
                _isFadingOut = true;
                e.Handled = true;
            }
        };

        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // High-frequency smooth fade timer (15ms interval)
        _fadeTimer = new System.Windows.Forms.Timer();
        _fadeTimer.Interval = 15;
        _fadeTimer.Tick += (s, e) =>
        {
            if (!_isFadingOut)
            {
                if (this.Opacity < 1.0)
                {
                    this.Opacity = Math.Min(1.0, this.Opacity + 0.15);
                }

                if (_displayCountdownMs > 0)
                {
                    _displayCountdownMs -= 15;
                    if (_displayCountdownMs <= 0)
                    {
                        _isFadingOut = true;
                    }
                }
            }
            else
            {
                if (this.Opacity > 0.0)
                {
                    this.Opacity = Math.Max(0.0, this.Opacity - 0.08);
                }
                else
                {
                    this.Opacity = 0.0;
                    _fadeTimer.Stop();
                }
            }
        };
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateRegionPath();
    }

    private void UpdateRegionPath()
    {
        try
        {
            if (this.Width < 10 || this.Height < 10) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                int radius = Math.Min(16, this.Height / 2);
                path.AddArc(0, 0, radius, radius, 180, 90);
                path.AddArc(this.Width - radius, 0, radius, radius, 270, 90);
                path.AddArc(this.Width - radius, this.Height - radius, radius, radius, 0, 90);
                path.AddArc(0, this.Height - radius, radius, radius, 90, 90);
                path.CloseFigure();
                this.Region = new Region(path);
            }
        }
        catch { }
    }

    public void UpdateVolumes(float app1Vol, float app2Vol, int muteState, bool forceShow)
    {
        _app1Vol = app1Vol;
        _app2Vol = app2Vol;
        _muteState = muteState;
        
        if (forceShow)
        {
            _displayCountdownMs = _totalDurationMs;
            _isFadingOut = false;
        }

        if (!this.IsDisposed && this.IsHandleCreated)
        {
            this.Invoke(new Action(() => 
            {
                this.Invalidate();
                if (forceShow && _fadeTimer != null && !_fadeTimer.Enabled)
                {
                    _fadeTimer.Start();
                }
            }));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (this.ClientRectangle.Width <= 10 || this.ClientRectangle.Height <= 10) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (Brush bgBrush = new SolidBrush(Color.FromArgb(235, 25, 25, 25)))
        {
            g.FillRectangle(bgBrush, this.ClientRectangle);
        }

        int centerX = this.Width / 2;

        // Scale vertical and font layout factor relative to default height (75)
        float scale = Math.Clamp((float)this.Height / 75f, 0.5f, 5.0f);

        int centerLineTop = (int)(10 * scale);
        int centerLineHeight = Math.Max(10, this.Height - (centerLineTop * 2));
        g.FillRectangle(Brushes.DimGray, centerX - 1, centerLineTop, 2, centerLineHeight);

        float fontSize = Math.Clamp(13f * scale, 6.5f, 32f);
        using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
        {
            SizeF app1Size = g.MeasureString(_app1Label, font);
            float textGap = 12 * scale;
            float textY = (this.Height - app1Size.Height) / 2f - (8 * scale);
            textY = Math.Max(4, textY);

            g.DrawString(_app1Label, font, Brushes.White, centerX - textGap - app1Size.Width, textY);
            g.DrawString(_app2Label, font, Brushes.White, centerX + textGap, textY);
        }

        // Use absolute margin from the sides instead of proportional scaling
        int absoluteSideMargin = 30;
        int maxBarWidthFromCenter = centerX - absoluteSideMargin - 12;
        if (maxBarWidthFromCenter < 20) maxBarWidthFromCenter = 20;

        int barWidth = maxBarWidthFromCenter;
        int barHeight = (int)(14 * scale);
        barHeight = Math.Clamp(barHeight, 8, 40);

        int barY = (int)(this.Height * 0.58f);
        barY = Math.Clamp(barY, 10, Math.Max(10, this.Height - barHeight - 6));

        Color unmutedColor = ColorTranslator.FromHtml(_settings.UnmutedColor);
        Color mutedColor = ColorTranslator.FromHtml(_settings.MutedColor);

        using Brush app1BarColor = new SolidBrush((_muteState == 2) ? mutedColor : unmutedColor);
        using Brush app2BarColor = new SolidBrush((_muteState == 1) ? mutedColor : unmutedColor);
        using Brush trackBgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));

        // Draw track backgrounds with safe rounded rectangle dimensions
        using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
        {
            if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
        }
        using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
        {
            if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
        }

        // Draw volume fill bars with safe rounded rectangle dimensions
        int app1FillWidth = (int)(barWidth * _app1Vol);
        if (app1FillWidth > 0)
        {
            using (GraphicsPath? app1FillPath = GetSafeRoundedBarPath(centerX - app1FillWidth, barY, app1FillWidth, barHeight, true, false))
            {
                if (app1FillPath != null) g.FillPath(app1BarColor, app1FillPath);
            }
        }

        int app2FillWidth = (int)(barWidth * _app2Vol);
        if (app2FillWidth > 0)
        {
            using (GraphicsPath? app2FillPath = GetSafeRoundedBarPath(centerX, barY, app2FillWidth, barHeight, false, true))
            {
                if (app2FillPath != null) g.FillPath(app2BarColor, app2FillPath);
            }
        }

        // Window border frame
        using (GraphicsPath borderPath = new GraphicsPath())
        {
            int radius = Math.Min(16, this.Height / 2);
            Rectangle borderRect = new Rectangle(0, 0, Math.Max(1, this.Width - 1), Math.Max(1, this.Height - 1));
            if (borderRect.Width > radius * 2 && borderRect.Height > radius * 2)
            {
                borderPath.AddArc(borderRect.X, borderRect.Y, radius, radius, 180, 90);
                borderPath.AddArc(borderRect.Right - radius, borderRect.Y, radius, radius, 270, 90);
                borderPath.AddArc(borderRect.Right - radius, borderRect.Bottom - radius, radius, radius, 0, 90);
                borderPath.AddArc(borderRect.X, borderRect.Bottom - radius, radius, radius, 90, 90);
                borderPath.CloseFigure();

                using (Pen pen = new Pen(Color.FromArgb(70, 70, 70), 1))
                {
                    g.DrawPath(pen, borderPath);
                }
            }
        }
    }

    private GraphicsPath? GetSafeRoundedBarPath(int x, int y, int width, int height, bool roundLeft, bool roundRight)
    {
        if (width <= 0 || height <= 0) return null;
        GraphicsPath path = new GraphicsPath();

        int cornerRadius = Math.Min(4, height / 2);
        if (width < cornerRadius * 2) cornerRadius = width / 2;
        if (cornerRadius < 1) cornerRadius = 1;

        int left = x;
        int right = x + width;
        int top = y;
        int bottom = y + height;
        int d = cornerRadius * 2;

        try
        {
            if (roundLeft && roundRight && width >= d && height >= d)
            {
                path.AddArc(left, top, d, d, 180, 90);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddArc(right - d, bottom - d, d, d, 0, 90);
                path.AddArc(left, bottom - d, d, d, 90, 90);
            }
            else if (roundLeft && width >= d && height >= d)
            {
                path.AddArc(left, top, d, d, 180, 90);
                path.AddLine(left + cornerRadius, top, right, top);
                path.AddLine(right, top, right, bottom);
                path.AddLine(right, bottom, left + cornerRadius, bottom);
                path.AddArc(left, bottom - d, d, d, 90, 90);
            }
            else if (roundRight && width >= d && height >= d)
            {
                path.AddLine(left, top, right - cornerRadius, top);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddArc(right - d, bottom - d, d, d, 0, 90);
                path.AddLine(right - cornerRadius, bottom, left, bottom);
                path.AddLine(left, bottom, left, top);
            }
            else
            {
                path.AddRectangle(new Rectangle(left, top, width, height));
            }
            path.CloseFigure();
            return path;
        }
        catch
        {
            path.Dispose();
            return null;
        }
    }
}
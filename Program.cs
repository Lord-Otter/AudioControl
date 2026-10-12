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
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

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

        _crossfaderWorker?.UpdateSettings(_settings, false);
    }

    public void ResetLiveVolumes()
    {
        _crossfaderWorker?.ResetVolumesToFullAndCenter();
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

class ProgramGroup
{
    public string GroupName { get; set; } = "";
    public List<string> Executables { get; set; } = new List<string>();
}

class AppSettings
{
    public string OperationMode { get; set; } = "Crossfade"; // "Crossfade" or "Volume Control"
    public string Source1 { get; set; } = "chrome.exe";
    public string Source2 { get; set; } = "firefox.exe";
    public List<ProgramGroup> Groups { get; set; } = new List<ProgramGroup>();
    public bool RunOnStartup { get; set; } = false;
    
    // Crossfade Keybinds
    public int CrossfadeKeyUp { get; set; } = 0x7E;    // VK_F15
    public int CrossfadeKeyMute { get; set; } = 0x7F;   // VK_F16
    public int CrossfadeKeyDown { get; set; } = 0x80;   // VK_F17

    // Volume Control Keybinds
    public int VolKeyUp { get; set; } = 0x26;    // Up Arrow
    public int VolKeyMute { get; set; } = 0x20;  // Space
    public int VolKeyDown { get; set; } = 0x28;  // Down Arrow

    public string UnmutedColor { get; set; } = "#00A8FF"; 
    public string MutedColor { get; set; } = "#FF4757";   
    public string OverlayPosition { get; set; } = "Bottom Center";
    public int PopupDurationMs { get; set; } = 2500;

    public int OverlayWidth { get; set; } = 340;
    public int OverlayHeight { get; set; } = 75;
    public bool EnableInterpolation { get; set; } = true;
    public int StepSize { get; set; } = 10; 
    public bool EnableEqualizationBuffer { get; set; } = true; 
}

class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private AppSettings _settings;
    private float _targetPosition;       
    private float _visualPosition;     
    private int _muteState = 0;
    private int _detentClicks = 0;            
    private int _detentDirection = 0;         
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private bool _running = false;

    public AudioCrossfaderWorker(AppSettings settings)
    {
        _settings = settings;
        _deviceEnumerator = new MMDeviceEnumerator();
        InitDefaultPosition();
    }

    private void InitDefaultPosition()
    {
        _targetPosition = 0.5f;
        _visualPosition = _targetPosition;
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

    public void UpdateSettings(AppSettings newSettings, bool resetVolumes)
    {
        _settings = newSettings;
        if (resetVolumes)
        {
            InitDefaultPosition();
            _muteState = 0;
            ResetVolumesToFull();
        }

        Stop();
        Start();
    }

    public void ResetVolumesToFullAndCenter()
    {
        InitDefaultPosition();
        _muteState = 0;
        ResetVolumesToFull();
        ApplyVolumes(true, true);
    }

    private void ResetVolumesToFull()
    {
        try
        {
            ApplyGroupVolume(_settings.Source1, 1.0f);
            ApplyGroupVolume(_settings.Source2, 1.0f);
        }
        catch { }
    }

    private void RunLoop()
    {
        string label1 = GetResolvedLabel(_settings.Source1);
        string label2 = GetResolvedLabel(_settings.Source2);

        Thread uiThread = new Thread(() =>
        {
            _overlay = new VolumeOverlayForm(label1, label2, _settings);
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

            if (_overlay != null && _overlay.IsFadedOut)
            {
                _detentClicks = 0;
                _detentDirection = 0;
            }

            bool isVolMode = (_settings.OperationMode == "Volume Control");
            int activeKeyUp = isVolMode ? _settings.VolKeyUp : _settings.CrossfadeKeyUp;
            int activeKeyDown = isVolMode ? _settings.VolKeyDown : _settings.CrossfadeKeyDown;
            int activeKeyMute = isVolMode ? _settings.VolKeyMute : _settings.CrossfadeKeyMute;

            int upSteps = 0;
            while ((GetAsyncKeyState(activeKeyUp) & 0x0001) != 0) { upSteps++; }

            int downSteps = 0;
            while ((GetAsyncKeyState(activeKeyDown) & 0x0001) != 0) { downSteps++; }

            bool currentMute = (GetAsyncKeyState(activeKeyMute) & 0x8000) != 0;
            if (currentMute && !lastMuteState) 
            { 
                CycleMuteState(); 
                physicalInputRegistered = true; 
            }
            lastMuteState = currentMute;

            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;

            if (upSteps > 0)
            {
                Step(stepIncrement * upSteps);
                physicalInputRegistered = true;
            }
            if (downSteps > 0)
            {
                Step(-stepIncrement * downSteps);
                physicalInputRegistered = true;
            }

            if (_settings.EnableInterpolation)
            {
                float diff = _targetPosition - _visualPosition;
                if (Math.Abs(diff) > 0.0005f)
                {
                    _visualPosition += diff * 0.6f;
                }
                else
                {
                    _visualPosition = _targetPosition;
                }
            }
            else
            {
                _visualPosition = _targetPosition;
            }

            if (_visualPosition <= 0.002f) _visualPosition = 0.0f;
            if (_visualPosition >= 0.998f) _visualPosition = 1.0f;

            ApplyVolumes(false, physicalInputRegistered);

            var now = DateTime.Now;
            if (physicalInputRegistered && (now - lastAudioApplyTime).TotalMilliseconds >= 15)
            {
                ApplyVolumes(true, false);
                lastAudioApplyTime = now;
            }

            Thread.Sleep(2);
        }
    }

    private string GetResolvedLabel(string sourceTarget)
    {
        if (string.IsNullOrEmpty(sourceTarget)) return "";
        if (sourceTarget.StartsWith("Group: "))
        {
            return sourceTarget.Substring(7).ToUpper();
        }
        return sourceTarget.Replace(".exe", "").ToUpper();
    }

    public void Step(float delta)
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        float minPos = 0.0f;
        float maxPos = 1.0f;

        if (isVolumeControl)
        {
            minPos = 0.0f;
            maxPos = 0.5f; 
        }

        if (isVolumeControl || !_settings.EnableEqualizationBuffer)
        {
            float newTarget = _targetPosition + delta;
            _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            return;
        }

        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            bool isFromRest = (_overlay == null || _overlay.IsFadedOut);
            int currentDeltaSign = Math.Sign(delta);

            if (isFromRest || (currentDeltaSign != 0 && currentDeltaSign != _detentDirection))
            {
                float newTarget = _targetPosition + delta;
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                _detentClicks = 0;
                _detentDirection = currentDeltaSign;
            }
            else
            {
                _detentClicks++;
                if (_detentClicks < 3)
                {
                    return;
                }
                else
                {
                    _detentClicks = 0;
                    float newTarget = _targetPosition + delta;
                    _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                    _detentDirection = currentDeltaSign;
                }
            }
        }
        else
        {
            float newTarget = _targetPosition + delta;
            bool crossingCenter = (_targetPosition < 0.5f && newTarget >= 0.5f) || (_targetPosition > 0.5f && newTarget <= 0.5f);
            if (crossingCenter)
            {
                _targetPosition = 0.5f;
                _detentClicks = 0;
                _detentDirection = Math.Sign(delta);
            }
            else
            {
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            }
        }
    }

    public void CycleMuteState()
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");
        if (isVolumeControl)
        {
            _muteState = (_muteState == 0) ? 1 : 0;
        }
        else
        {
            _muteState = (_muteState + 1) % 3;
        }
    }

    private void ApplyVolumes(bool updateAudioSessions, bool forceShowPopup)
    {
        float audioApp1Vol = 0.0f;
        float audioApp2Vol = 0.0f;

        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        if (isVolumeControl)
        {
            audioApp1Vol = _targetPosition * 2.0f;
        }
        else
        {
            if (_targetPosition >= 0.5f)
            {
                audioApp1Vol = 1.0f;
                audioApp2Vol = 1.0f - ((_targetPosition - 0.5f) * 2.0f);
            }
            else
            {
                audioApp2Vol = 1.0f;
                audioApp1Vol = _targetPosition * 2.0f;
            }
        }

        audioApp1Vol = Math.Clamp(audioApp1Vol, 0.0f, 1.0f);
        audioApp2Vol = Math.Clamp(audioApp2Vol, 0.0f, 1.0f);

        if (audioApp1Vol <= 0.01f) audioApp1Vol = 0.0f;
        if (audioApp1Vol >= 0.99f) audioApp1Vol = 1.0f;
        if (audioApp2Vol <= 0.01f) audioApp2Vol = 0.0f;
        if (audioApp2Vol >= 0.99f) audioApp2Vol = 1.0f;

        float actualApp1Vol = audioApp1Vol;
        float actualApp2Vol = audioApp2Vol;

        if (_muteState == 1 && !isVolumeControl) actualApp2Vol = 0.0f;
        else if (_muteState == 2 && !isVolumeControl) actualApp1Vol = 0.0f;
        else if (_muteState > 0 && isVolumeControl) actualApp1Vol = 0.0f;

        if (updateAudioSessions)
        {
            ApplyGroupVolume(_settings.Source1, actualApp1Vol);
            if (!isVolumeControl)
            {
                ApplyGroupVolume(_settings.Source2, actualApp2Vol);
            }
        }

        float visualApp1Vol = 0.0f;
        float visualApp2Vol = 0.0f;

        if (isVolumeControl)
        {
            visualApp1Vol = _visualPosition * 2.0f;
        }
        else
        {
            if (_visualPosition >= 0.5f)
            {
                visualApp1Vol = 1.0f;
                visualApp2Vol = 1.0f - ((_visualPosition - 0.5f) * 2.0f);
            }
            else
            {
                visualApp2Vol = 1.0f;
                visualApp1Vol = _visualPosition * 2.0f;
            }
        }

        visualApp1Vol = Math.Clamp(visualApp1Vol, 0.0f, 1.0f);
        visualApp2Vol = Math.Clamp(visualApp2Vol, 0.0f, 1.0f);

        _overlay?.UpdateVolumes(visualApp1Vol, visualApp2Vol, _muteState, forceShowPopup);
    }

    private void ApplyGroupVolume(string targetIdentifier, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetIdentifier)) return;

        List<string> targets = new List<string>();
        if (targetIdentifier.StartsWith("Group: "))
        {
            string groupName = targetIdentifier.Substring(7);
            var group = _settings.Groups.FirstOrDefault(g => g.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                targets.AddRange(group.Executables);
            }
        }
        else
        {
            targets.Add(targetIdentifier);
        }

        foreach (var t in targets)
        {
            SetApplicationVolume(t, volume);
        }
    }

    private void SetApplicationVolume(string targetApp, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetApp)) return;

        try
        {
            string targetKey = targetApp.ToLower().Replace(".exe", "").Trim();
            if (string.IsNullOrEmpty(targetKey)) return;

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
                            if (proc.ProcessName.ToLower().Equals(targetKey))
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

// Configuration Menu Window with Tabs & Dynamic Context UI
class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private TabControl _tabControl = null!;
    private ComboBox _cmbMode = null!;
    private ComboBox _cmbSource1 = null!;
    private ComboBox _cmbSource2 = null!;
    private Label _lblSource2 = null!;

    // Unified Dynamic Keybind Slot Controls & Labels
    private Label _lblKey1 = null!;
    private TextBox _txtKey1 = null!;
    private Button _btnRec1 = null!;

    private Label _lblKey2 = null!;
    private TextBox _txtKey2 = null!;
    private Button _btnRec2 = null!;

    private Label _lblKey3 = null!;
    private TextBox _txtKey3 = null!;
    private Button _btnRec3 = null!;

    private ComboBox _cmbPosition = null!;
    private TextBox _txtPopupDuration = null!;
    private TextBox _txtStepSize = null!;
    private TextBox _txtWidth = null!;
    private TextBox _txtHeight = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkInterpolation = null!;
    private CheckBox _chkBuffer = null!;
    
    private FlowLayoutPanel _groupsFlowPanel = null!;
    private TrayApplicationContext _context;
    private AppSettings _currentSettings;
    private AppSettings _savedSettingsSnapshot;
    private Label _lblStatus = null!;
    
    private string _currentUnmutedHex;
    private string _currentMutedHex;

    private bool _isRecording = false;
    private Button? _activeRecordButton = null;
    private TextBox? _activeTargetTextBox = null;
    private string _previousButtonText = "";
    private KeyCaptureFilter? _mouseFilter;
    private bool _isInitializing = true;

    public SettingsForm(AppSettings settings, TrayApplicationContext context)
    {
        _context = context;
        _currentSettings = settings;
        _savedSettingsSnapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        _currentUnmutedHex = settings.UnmutedColor;
        _currentMutedHex = settings.MutedColor;

        this.Text = "Audio Crossfader Settings";
        this.Size = new Size(460, 810);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.KeyPreview = true;

        this.BackColor = Color.FromArgb(32, 32, 32);
        this.ForeColor = Color.FromArgb(240, 240, 240);

        // Global Bottom Action Panel (persists across all tabs)
        Panel bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            BackColor = Color.FromArgb(32, 32, 32)
        };

        _lblStatus = new Label
        {
            Text = "",
            Left = 20,
            Top = 5,
            Width = 405,
            Height = 18,
            ForeColor = Color.FromArgb(240, 190, 80)
        };
        bottomPanel.Controls.Add(_lblStatus);

        Button btnResetVol = new Button 
        { 
            Text = "Reset Volumes", 
            Left = 20, 
            Top = 28, 
            Width = 110, 
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnResetVol.FlatAppearance.BorderSize = 0;
        btnResetVol.Click += (s, e) => _context.ResetLiveVolumes();
        bottomPanel.Controls.Add(btnResetVol);

        Button btnRevert = new Button 
        { 
            Text = "Revert Changes", 
            Left = 140, 
            Top = 28, 
            Width = 120, 
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRevert.FlatAppearance.BorderSize = 0;
        btnRevert.Click += (s, e) => RevertChanges();
        bottomPanel.Controls.Add(btnRevert);

        Button btnSave = new Button 
        { 
            Text = "Save & Apply", 
            Left = 270, 
            Top = 28, 
            Width = 140, 
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212), 
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => SaveChanges();
        bottomPanel.Controls.Add(btnSave);

        Controls.Add(bottomPanel);

        _tabControl = new TabControl { Dock = DockStyle.Fill };
        _tabControl.TabPages.Add(CreateGeneralTab());
        _tabControl.TabPages.Add(CreateGroupsTab());
        _tabControl.TabPages.Add(CreatePersonalizationTab());
        Controls.Add(_tabControl);

        _mouseFilter = new KeyCaptureFilter(CancelRecording);
        Application.AddMessageFilter(_mouseFilter);

        this.FormClosed += (s, e) =>
        {
            if (_mouseFilter != null) Application.RemoveMessageFilter(_mouseFilter);
        };

        this.KeyDown += SettingsForm_KeyDown;
        
        // Populate initial controls from loaded settings values
        _isInitializing = true;
        LoadSettingsIntoUI(_currentSettings);
        _isInitializing = false;

        UpdateContextUI(false);
        MarkClean();
    }

    private string GetKeyName(int vk)
    {
        if (vk == 0) return "None";
        Keys key = (Keys)vk;
        switch (key)
        {
            case Keys.Up: return "Up Arrow";
            case Keys.Down: return "Down Arrow";
            case Keys.Left: return "Left Arrow";
            case Keys.Right: return "Right Arrow";
            case Keys.Space: return "Space";
            case Keys.Return: return "Enter";
            case Keys.Back: return "Backspace";
            case Keys.Tab: return "Tab";
            case Keys.Escape: return "Escape";
            default: return key.ToString();
        }
    }

    private TabPage CreateGeneralTab()
    {
        TabPage page = new TabPage("General & Sources") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(panel);

        int y = 20;

        AddLabel(panel, "Operation Mode:", 20, ref y);
        _cmbMode = new ComboBox
        {
            Left = 20,
            Top = y,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbMode.Items.AddRange(new object[] { "Crossfade", "Volume Control" });
        _cmbMode.SelectedItem = _currentSettings.OperationMode;
        if (_cmbMode.SelectedIndex == -1) _cmbMode.SelectedIndex = 0;
        _cmbMode.SelectedIndexChanged += (s, e) => 
        { 
            if (!_isInitializing)
            {
                UpdateContextUI(true); 
                MarkDirty(); 
            }
        };
        panel.Controls.Add(_cmbMode);
        y += 35;

        List<string> options = GetAvailableSourceOptions();

        AddLabel(panel, "Source 1 / Audio Target:", 20, ref y);
        _cmbSource1 = AddComboBox(panel, options, _currentSettings.Source1, ref y);

        _lblSource2 = AddLabel(panel, "Source 2 / Crossfade Target:", 20, ref y);
        _cmbSource2 = AddComboBox(panel, options, _currentSettings.Source2, ref y);

        // Dynamic Keybind Slot Rows with Aligned Labels (+3px vertical adjustment)
        _lblKey1 = AddAlignedLabel(panel, "Key 1:", 20, ref y);
        var r1 = AddKeyBindControls(panel, ref y);
        _txtKey1 = r1.txt;
        _btnRec1 = r1.btn;

        _lblKey2 = AddAlignedLabel(panel, "Key 2:", 20, ref y);
        var r2 = AddKeyBindControls(panel, ref y);
        _txtKey2 = r2.txt;
        _btnRec2 = r2.btn;

        _lblKey3 = AddAlignedLabel(panel, "Key 3:", 20, ref y);
        var r3 = AddKeyBindControls(panel, ref y);
        _txtKey3 = r3.txt;
        _btnRec3 = r3.btn;

        y += 5;
        AddLabel(panel, "Step Size (%, 1 - 100):", 20, ref y);
        _txtStepSize = AddTextBox(panel, _currentSettings.StepSize.ToString(), ref y);

        _chkBuffer = new CheckBox { Text = "Enable 100% Equalization Buffer (Crossfade)", Checked = _currentSettings.EnableEqualizationBuffer, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        _chkBuffer.CheckedChanged += (s, e) => MarkDirty();
        panel.Controls.Add(_chkBuffer);
        y += 35;

        _chkStartup = new CheckBox { Text = "Run on Windows Startup", Checked = _currentSettings.RunOnStartup, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        _chkStartup.CheckedChanged += (s, e) => MarkDirty();
        panel.Controls.Add(_chkStartup);
        y += 20;

        return page;
    }

    private TabPage CreateGroupsTab()
    {
        TabPage page = new TabPage("Program Groups") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(container);

        Button btnAddGroup = new Button
        {
            Text = "+ Add New Group",
            Left = 20,
            Top = 20,
            Width = 160,
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnAddGroup.FlatAppearance.BorderSize = 0;
        btnAddGroup.Click += (s, e) =>
        {
            _currentSettings.Groups.Add(new ProgramGroup { GroupName = "NewGroup", Executables = new List<string> { "app.exe" } });
            RefreshGroupsTabUI();
            MarkDirty();
        };
        container.Controls.Add(btnAddGroup);

        _groupsFlowPanel = new FlowLayoutPanel
        {
            Left = 20,
            Top = 65,
            Width = 395,
            Height = 540,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        container.Controls.Add(_groupsFlowPanel);

        PopulateGroupsFlowContent();
        return page;
    }

    private TabPage CreatePersonalizationTab()
    {
        TabPage page = new TabPage("Personalization") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(panel);

        int y = 20;

        AddLabel(panel, "Overlay Colors:", 20, ref y);
        AddColorPickerRow(panel, "Unmuted Color:", _currentUnmutedHex, (hex) => { _currentUnmutedHex = hex; MarkDirty(); }, ref y);
        AddColorPickerRow(panel, "Muted Color:", _currentMutedHex, (hex) => { _currentMutedHex = hex; MarkDirty(); }, ref y);

        y += 5;
        AddLabel(panel, "Overlay Position:", 20, ref y);
        _cmbPosition = new ComboBox
        {
            Left = 160,
            Top = y - 3,
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbPosition.Items.AddRange(new object[] { "Bottom Center", "Top Center", "Bottom Right", "Bottom Left", "Top Right", "Top Left" });
        _cmbPosition.SelectedItem = _currentSettings.OverlayPosition;
        if (_cmbPosition.SelectedIndex == -1) _cmbPosition.SelectedIndex = 0;
        _cmbPosition.SelectedIndexChanged += (s, e) => MarkDirty();
        panel.Controls.Add(_cmbPosition);
        y += 35;

        AddLabel(panel, "Popup Duration (ms):", 20, ref y);
        _txtPopupDuration = AddTextBox(panel, _currentSettings.PopupDurationMs.ToString(), ref y);

        AddLabel(panel, "Overlay Size (Width / Height):", 20, ref y);
        _txtWidth = AddNumericRow(panel, "Width:", _currentSettings.OverlayWidth.ToString(), 20, ref y);
        _txtHeight = AddNumericRow(panel, "Height:", _currentSettings.OverlayHeight.ToString(), 20, ref y);

        _chkInterpolation = new CheckBox { Text = "Smooth Volume Bar Interpolation", Checked = _currentSettings.EnableInterpolation, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        _chkInterpolation.CheckedChanged += (s, e) => MarkDirty();
        panel.Controls.Add(_chkInterpolation);
        y += 20;

        return page;
    }

    private void MarkDirty()
    {
        if (_isInitializing) return;
        if (_lblStatus != null)
        {
            _lblStatus.ForeColor = Color.FromArgb(240, 190, 80);
            _lblStatus.Text = "You have unsaved changes!";
        }
    }

    private void MarkClean()
    {
        if (_lblStatus != null)
        {
            _lblStatus.ForeColor = Color.FromArgb(80, 200, 120);
            _lblStatus.Text = "Settings Saved!";
        }
    }

    private void LoadSettingsIntoUI(AppSettings settings)
    {
        _cmbMode.SelectedItem = settings.OperationMode;
        _cmbSource1.SelectedItem = settings.Source1;
        if (!_cmbSource1.Items.Contains(settings.Source1)) _cmbSource1.Text = settings.Source1;

        _cmbSource2.SelectedItem = settings.Source2;
        if (!_cmbSource2.Items.Contains(settings.Source2)) _cmbSource2.Text = settings.Source2;

        bool isVolumeControl = (settings.OperationMode == "Volume Control");
        int k1 = isVolumeControl ? settings.VolKeyUp : settings.CrossfadeKeyUp;
        int k2 = isVolumeControl ? settings.VolKeyDown : settings.CrossfadeKeyDown;
        int k3 = isVolumeControl ? settings.VolKeyMute : settings.CrossfadeKeyMute;

        _txtKey1.Text = GetKeyName(k1);
        _txtKey1.Tag = k1;

        _txtKey2.Text = GetKeyName(k2);
        _txtKey2.Tag = k2;

        _txtKey3.Text = GetKeyName(k3);
        _txtKey3.Tag = k3;

        _txtStepSize.Text = settings.StepSize.ToString();
        _chkBuffer.Checked = settings.EnableEqualizationBuffer;
        _chkStartup.Checked = settings.RunOnStartup;

        _currentUnmutedHex = settings.UnmutedColor;
        _currentMutedHex = settings.MutedColor;
        _cmbPosition.SelectedItem = settings.OverlayPosition;
        _txtPopupDuration.Text = settings.PopupDurationMs.ToString();
        _txtWidth.Text = settings.OverlayWidth.ToString();
        _txtHeight.Text = settings.OverlayHeight.ToString();
        _chkInterpolation.Checked = settings.EnableInterpolation;

        RefreshGroupsTabUI();
    }

    private void RevertChanges()
    {
        _isInitializing = true;
        _currentSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_savedSettingsSnapshot)) ?? new AppSettings();
        LoadSettingsIntoUI(_currentSettings);
        UpdateContextUI(false);
        _isInitializing = false;
        MarkClean();
    }

    private void RefreshGroupsTabUI()
    {
        PopulateGroupsFlowContent();
        UpdateSourceDropdownOptions();
    }

    private void PopulateGroupsFlowContent()
    {
        _groupsFlowPanel.Controls.Clear();
        foreach (var group in _currentSettings.Groups)
        {
            Panel card = new Panel
            {
                Width = 375,
                BackColor = Color.FromArgb(45, 45, 45),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            TextBox txtGroupName = new TextBox
            {
                Text = group.GroupName,
                Left = 10,
                Top = 10,
                Width = 220,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtGroupName.TextChanged += (s, e) => 
            { 
                group.GroupName = txtGroupName.Text.Trim(); 
                UpdateSourceDropdownOptions();
                MarkDirty();
            };
            card.Controls.Add(txtGroupName);

            Button btnRemoveGroup = new Button
            {
                Text = "Delete Group",
                Left = 245,
                Top = 9,
                Width = 115,
                Height = 24,
                BackColor = Color.FromArgb(180, 50, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRemoveGroup.FlatAppearance.BorderSize = 0;
            btnRemoveGroup.Click += (s, e) =>
            {
                _currentSettings.Groups.Remove(group);
                RefreshGroupsTabUI();
                MarkDirty();
            };
            card.Controls.Add(btnRemoveGroup);

            int innerY = 45;
            for (int i = 0; i < group.Executables.Count; i++)
            {
                int index = i;
                TextBox txtExe = new TextBox
                {
                    Text = group.Executables[index],
                    Left = 20,
                    Top = innerY,
                    Width = 240,
                    BackColor = Color.FromArgb(35, 35, 35),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                txtExe.TextChanged += (s, e) => { group.Executables[index] = txtExe.Text.Trim(); MarkDirty(); };
                card.Controls.Add(txtExe);

                Button btnRemoveExe = new Button
                {
                    Text = "-",
                    Left = 270,
                    Top = innerY - 1,
                    Width = 30,
                    Height = 24,
                    BackColor = Color.FromArgb(80, 80, 80),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnRemoveExe.FlatAppearance.BorderSize = 0;
                btnRemoveExe.Click += (s, e) =>
                {
                    group.Executables.RemoveAt(index);
                    RefreshGroupsTabUI();
                    MarkDirty();
                };
                card.Controls.Add(btnRemoveExe);

                innerY += 32;
            }

            Button btnAddExe = new Button
            {
                Text = "+ Add Source Executable",
                Left = 20,
                Top = innerY,
                Width = 180,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnAddExe.FlatAppearance.BorderSize = 0;
            btnAddExe.Click += (s, e) =>
            {
                group.Executables.Add("");
                RefreshGroupsTabUI();
                MarkDirty();
            };
            card.Controls.Add(btnAddExe);

            Panel paddingBottom = new Panel { Height = 10, Top = innerY + 30, Width = 375 };
            card.Controls.Add(paddingBottom);

            _groupsFlowPanel.Controls.Add(card);
        }
    }

    private List<string> GetAvailableSourceOptions()
    {
        List<string> options = new List<string>();
        foreach (var group in _currentSettings.Groups)
        {
            if (!string.IsNullOrEmpty(group.GroupName))
            {
                options.Add($"Group: {group.GroupName}");
            }
        }
        return options.Distinct().ToList();
    }

    private void UpdateSourceDropdownOptions()
    {
        if (_cmbSource1 == null || _cmbSource2 == null) return;
        List<string> options = GetAvailableSourceOptions();
        
        string cur1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text;
        string cur2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text;

        _cmbSource1.Items.Clear();
        _cmbSource1.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource1.SelectedItem = cur1;

        _cmbSource2.Items.Clear();
        _cmbSource2.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource2.SelectedItem = cur2;
    }

    private void UpdateContextUI(bool modeSwitched)
    {
        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");
        
        if (_cmbSource2 != null) _cmbSource2.Visible = !isVolumeControl;
        if (_lblSource2 != null) _lblSource2.Visible = !isVolumeControl;
        if (_chkBuffer != null) _chkBuffer.Visible = !isVolumeControl;

        if (modeSwitched)
        {
            // Cache current bindings before switching view target
            int currentKey1 = (int)(_txtKey1.Tag ?? 0);
            int currentKey2 = (int)(_txtKey2.Tag ?? 0);
            int currentKey3 = (int)(_txtKey3.Tag ?? 0);

            if (_currentSettings.OperationMode == "Volume Control")
            {
                _currentSettings.VolKeyUp = currentKey1;
                _currentSettings.VolKeyDown = currentKey2;
                _currentSettings.VolKeyMute = currentKey3;
            }
            else
            {
                _currentSettings.CrossfadeKeyUp = currentKey1;
                _currentSettings.CrossfadeKeyDown = currentKey2;
                _currentSettings.CrossfadeKeyMute = currentKey3;
            }

            _currentSettings.OperationMode = isVolumeControl ? "Volume Control" : "Crossfade";

            int targetKey1 = isVolumeControl ? _currentSettings.VolKeyUp : _currentSettings.CrossfadeKeyUp;
            int targetKey2 = isVolumeControl ? _currentSettings.VolKeyDown : _currentSettings.CrossfadeKeyDown;
            int targetKey3 = isVolumeControl ? _currentSettings.VolKeyMute : _currentSettings.CrossfadeKeyMute;

            _txtKey1.Text = GetKeyName(targetKey1);
            _txtKey1.Tag = targetKey1;

            _txtKey2.Text = GetKeyName(targetKey2);
            _txtKey2.Tag = targetKey2;

            _txtKey3.Text = GetKeyName(targetKey3);
            _txtKey3.Tag = targetKey3;
        }

        if (isVolumeControl)
        {
            _lblKey1.Text = "Volume Up Key:";
            _lblKey2.Text = "Volume Down Key:";
            _lblKey3.Text = "Mute / Unmute Key:";
        }
        else
        {
            _lblKey1.Text = "Up / Right Key:";
            _lblKey2.Text = "Down / Left Key:";
            _lblKey3.Text = "Cycle Mute Key:";
        }
    }

    private ComboBox AddComboBox(Panel panel, List<string> items, string selectedValue, ref int top)
    {
        ComboBox cmb = new ComboBox
        {
            Left = 20,
            Top = top,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        cmb.Items.AddRange(items.Cast<object>().ToArray());
        cmb.SelectedItem = selectedValue;
        if (cmb.SelectedIndex == -1) cmb.Text = selectedValue;
        cmb.SelectedIndexChanged += (s, e) => MarkDirty();
        cmb.TextChanged += (s, e) => MarkDirty();
        panel.Controls.Add(cmb);
        top += 35;
        return cmb;
    }

    private TextBox AddTextBox(Panel panel, string text, ref int top)
    {
        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = 20, 
            Top = top, 
            Width = 390, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        txt.TextChanged += (s, e) => MarkDirty();
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private TextBox AddNumericRow(Panel panel, string labelText, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = left, Top = top + 3, Width = 50, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = left + 55, 
            Top = top, 
            Width = 120, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        txt.TextChanged += (s, e) => MarkDirty();
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private Label AddAlignedLabel(Panel panel, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = text, Left = left, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);
        return lbl;
    }

    private (TextBox txt, Button btn) AddKeyBindControls(Panel panel, ref int top)
    {
        TextBox txt = new TextBox 
        { 
            Left = 170, 
            Top = top, 
            Width = 90, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };
        panel.Controls.Add(txt);

        Button btnRecord = new Button
        {
            Text = "Record",
            Left = 270,
            Top = top - 1,
            Width = 140,
            Height = 24,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRecord.FlatAppearance.BorderSize = 0;
        btnRecord.Click += (s, e) => StartRecording(btnRecord, txt);
        panel.Controls.Add(btnRecord);

        top += 32;
        return (txt, btnRecord);
    }

    private void AddColorPickerRow(Panel panel, string labelText, string initialHex, Action<string> onColorChanged, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        Panel colorBox = new Panel 
        { 
            Left = 170, 
            Top = top, 
            Width = 240, 
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

        panel.Controls.Add(colorBox);
        top += 32;
    }

    private Label AddLabel(Panel panel, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = text, Left = left, Top = top, Width = 390, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);
        top += 20;
        return lbl;
    }

    private void SettingsForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;
        if (e.KeyCode == Keys.Escape) { CancelRecording(); e.Handled = true; return; }
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;

        int vk = (int)e.KeyCode;
        string keyName = GetKeyName(vk);

        if ((_activeTargetTextBox != _txtKey1 && (int)(_txtKey1.Tag ?? 0) == vk) ||
            (_activeTargetTextBox != _txtKey2 && (int)(_txtKey2.Tag ?? 0) == vk) ||
            (_activeTargetTextBox != _txtKey3 && (int)(_txtKey3.Tag ?? 0) == vk))
        {
            MessageBox.Show($"The key '{keyName}' is already bound to another function in this mode.", "Keybind Conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            StopRecording();
            e.Handled = true;
            return;
        }

        if (_activeTargetTextBox != null)
        {
            _activeTargetTextBox.Text = keyName;
            _activeTargetTextBox.Tag = vk;
            MarkDirty();
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
            if (m.Msg == 0x0201 || m.Msg == 0x0204) _onMouseClick();
            return false;
        }
    }

    private void SaveChanges()
    {
        string s1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text.Trim();
        string s2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text.Trim();
        string mode = _cmbMode.SelectedItem?.ToString() ?? "Crossfade";

        int key1Val = (int)(_txtKey1.Tag ?? 0);
        int key2Val = (int)(_txtKey2.Tag ?? 0);
        int key3Val = (int)(_txtKey3.Tag ?? 0);

        int cfUp = _currentSettings.CrossfadeKeyUp;
        int cfDown = _currentSettings.CrossfadeKeyDown;
        int cfMute = _currentSettings.CrossfadeKeyMute;

        int volUp = _currentSettings.VolKeyUp;
        int volDown = _currentSettings.VolKeyDown;
        int volMute = _currentSettings.VolKeyMute;

        if (mode == "Volume Control")
        {
            volUp = key1Val;
            volDown = key2Val;
            volMute = key3Val;

            if (string.IsNullOrEmpty(s1))
            {
                MessageBox.Show("Volume Control mode requires a source or group selection.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            cfUp = key1Val;
            cfDown = key2Val;
            cfMute = key3Val;

            if (!string.IsNullOrEmpty(s1) && !string.IsNullOrEmpty(s2) && s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Source 1 and Source 2 cannot be the exact same program or group.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                MessageBox.Show("Crossfade mode requires both Source 1 and Source 2 to be selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        int ParseInt(string text, int defaultVal) => int.TryParse(text, out int val) ? val : defaultVal;

        var newSettings = new AppSettings
        {
            OperationMode = mode,
            Source1 = s1,
            Source2 = s2,
            Groups = _currentSettings.Groups,
            RunOnStartup = _chkStartup.Checked,
            CrossfadeKeyUp = cfUp,
            CrossfadeKeyDown = cfDown,
            CrossfadeKeyMute = cfMute,
            VolKeyUp = volUp,
            VolKeyDown = volDown,
            VolKeyMute = volMute,
            UnmutedColor = _currentUnmutedHex,
            MutedColor = _currentMutedHex,
            OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center",
            PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000),
            StepSize = Math.Clamp(ParseInt(_txtStepSize.Text, 10), 1, 100),
            OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 340), 100, 2000),
            OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 75), 50, 1000),
            EnableInterpolation = _chkInterpolation.Checked,
            EnableEqualizationBuffer = _chkBuffer.Checked
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (newSettings.RunOnStartup) key?.SetValue("AudioCrossfader", $"\"{exePath}\"");
            else key?.DeleteValue("AudioCrossfader", false);
        }
        catch { }

        _currentSettings = newSettings;
        _savedSettingsSnapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(newSettings)) ?? new AppSettings();
        _context.SaveSettings(newSettings);
        MarkClean();
    }
}

// On-Screen Overlay Popup (Optimized 60 FPS Smooth Render)
class VolumeOverlayForm : Form
{
    private readonly string _app1Label;
    private readonly string _app2Label;
    private readonly AppSettings _settings;
    private readonly bool _isSingleSource;
    private readonly string _singleLabel;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private int _totalDurationMs = 2500;
    private bool _isFadingOut = false;

    public bool IsFadedOut => this.Opacity <= 0.0;

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = FormatLabel(app1);
        _app2Label = FormatLabel(app2);
        _settings = settings;

        _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));
        _singleLabel = string.IsNullOrEmpty(_app1Label) ? _app2Label : _app1Label;

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ClientSize = new Size(Math.Max(100, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

        _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int x = workingArea.Left + (workingArea.Width - this.Width) / 2;
        int y = workingArea.Bottom - this.Height - 65;

        switch (_settings.OverlayPosition)
        {
            case "Top Center":
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Top + 40; break;
            case "Bottom Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Bottom Left":
                x = workingArea.Left + 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Top Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Top + 40; break;
            case "Top Left":
                x = workingArea.Left + 40; y = workingArea.Top + 40; break;
            case "Bottom Center":
            default:
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Bottom - this.Height - 65; break;
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

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _fadeTimer.Tick += (s, e) =>
        {
            if (!_isFadingOut)
            {
                if (this.Opacity < 1.0) this.Opacity = Math.Min(1.0, this.Opacity + 0.2);
                if (_displayCountdownMs > 0)
                {
                    _displayCountdownMs -= 16;
                    if (_displayCountdownMs <= 0) _isFadingOut = true;
                }
            }
            else
            {
                if (this.Opacity > 0.0) this.Opacity = Math.Max(0.0, this.Opacity - 0.12);
                else { this.Opacity = 0.0; _fadeTimer.Stop(); }
            }
            this.Invalidate();
        };
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

    private string FormatLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("Group: ")) return raw.Substring(7).ToUpper();
        return raw.Replace(".exe", "").ToUpper();
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

        if (!this.IsDisposed && this.IsHandleCreated && forceShow && _fadeTimer != null && !_fadeTimer.Enabled)
        {
            this.Invoke(new Action(() => _fadeTimer.Start()));
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
        float scale = Math.Clamp((float)this.Height / 75f, 0.5f, 5.0f);
        float fontSize = Math.Clamp(13f * scale, 6.5f, 32f);

        if (_isSingleSource)
        {
            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF labelSize = g.MeasureString(_singleLabel, font);
                float textY = (this.Height - labelSize.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);
                g.DrawString(_singleLabel, font, Brushes.White, centerX - (labelSize.Width / 2f), textY);
            }

            int absoluteSideMargin = 30;
            int barWidth = this.Width - (absoluteSideMargin * 2);
            if (barWidth < 20) barWidth = 20;

            int barHeight = (int)(14 * scale);
            barHeight = Math.Clamp(barHeight, 8, 40);

            int barY = (int)(this.Height * 0.58f);
            barY = Math.Clamp(barY, 10, Math.Max(10, this.Height - barHeight - 6));

            Color unmutedColor = ColorTranslator.FromHtml(_settings.UnmutedColor);
            Color mutedColor = ColorTranslator.FromHtml(_settings.MutedColor);

            bool isMuted = (_muteState > 0);
            using Brush barColor = new SolidBrush(isMuted ? mutedColor : unmutedColor);
            using Brush trackBgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));

            using (GraphicsPath? trackPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, barWidth, barHeight, true, true))
            {
                if (trackPath != null) g.FillPath(trackBgBrush, trackPath);
            }

            int fillWidth = (int)(barWidth * _app1Vol);
            if (fillWidth > 0)
            {
                using (GraphicsPath? fillPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, fillWidth, barHeight, true, true))
                {
                    if (fillPath != null) g.FillPath(barColor, fillPath);
                }
            }
        }
        else
        {
            int centerLineTop = (int)(10 * scale);
            int centerLineHeight = Math.Max(10, this.Height - (centerLineTop * 2));
            g.FillRectangle(Brushes.DimGray, centerX - 1, centerLineTop, 2, centerLineHeight);

            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF app1Size = g.MeasureString(_app1Label, font);
                float textGap = 12 * scale;
                float textY = (this.Height - app1Size.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);

                g.DrawString(_app1Label, font, Brushes.White, centerX - textGap - app1Size.Width, textY);
                g.DrawString(_app2Label, font, Brushes.White, centerX + textGap, textY);
            }

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

            using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
            {
                if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
            }
            using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
            {
                if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
            }

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
        }

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

                using (Pen pen = new Pen(Color.FromArgb(70, 70, 70), 1)) g.DrawPath(pen, borderPath);
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

/*using System.Diagnostics;
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
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

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

    public void SaveSettings(AppSettings newSettings, bool resetVolumes = false)
    {
        _settings = newSettings;
        try
        {
            string json = JsonSerializer.Serialize(_settings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }

        _crossfaderWorker?.UpdateSettings(_settings, resetVolumes);
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

class ProgramGroup
{
    public string GroupName { get; set; } = "";
    public List<string> Executables { get; set; } = new List<string>();
}

class AppSettings
{
    public string OperationMode { get; set; } = "Crossfade"; // "Crossfade" or "Volume Control"
    public string Source1 { get; set; } = "chrome.exe";
    public string Source2 { get; set; } = "firefox.exe";
    public List<ProgramGroup> Groups { get; set; } = new List<ProgramGroup>();
    public bool RunOnStartup { get; set; } = false;
    
    // Crossfade Keybinds
    public int CrossfadeKeyUp { get; set; } = 0x7E;    // VK_F15
    public int CrossfadeKeyMute { get; set; } = 0x7F;   // VK_F16
    public int CrossfadeKeyDown { get; set; } = 0x80;   // VK_F17

    // Volume Control Keybinds
    public int VolKeyUp { get; set; } = 0x26;    // Up Arrow
    public int VolKeyMute { get; set; } = 0x20;  // Space
    public int VolKeyDown { get; set; } = 0x28;  // Down Arrow

    public string UnmutedColor { get; set; } = "#00A8FF"; 
    public string MutedColor { get; set; } = "#FF4757";   
    public string OverlayPosition { get; set; } = "Bottom Center";
    public int PopupDurationMs { get; set; } = 2500;

    public int OverlayWidth { get; set; } = 340;
    public int OverlayHeight { get; set; } = 75;
    public bool EnableInterpolation { get; set; } = true;
    public int StepSize { get; set; } = 10; 
    public bool EnableEqualizationBuffer { get; set; } = true; 
}

class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private AppSettings _settings;
    private float _targetPosition;       
    private float _visualPosition;     
    private int _muteState = 0;
    private int _detentClicks = 0;            
    private int _detentDirection = 0;         
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private bool _running = false;

    public AudioCrossfaderWorker(AppSettings settings)
    {
        _settings = settings;
        _deviceEnumerator = new MMDeviceEnumerator();
        InitDefaultPosition();
    }

    private void InitDefaultPosition()
    {
        if (_settings.OperationMode == "Volume Control")
        {
            _targetPosition = 0.5f; 
        }
        else
        {
            _targetPosition = 0.5f; 
        }
        _visualPosition = _targetPosition;
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

    public void UpdateSettings(AppSettings newSettings, bool resetVolumes)
    {
        _settings = newSettings;
        if (resetVolumes)
        {
            InitDefaultPosition();
            _muteState = 0;
            ResetVolumesToFull();
        }

        Stop();
        Start();
    }

    private void ResetVolumesToFull()
    {
        try
        {
            ApplyGroupVolume(_settings.Source1, 1.0f);
            ApplyGroupVolume(_settings.Source2, 1.0f);
        }
        catch { }
    }

    private void RunLoop()
    {
        string label1 = GetResolvedLabel(_settings.Source1);
        string label2 = GetResolvedLabel(_settings.Source2);

        Thread uiThread = new Thread(() =>
        {
            _overlay = new VolumeOverlayForm(label1, label2, _settings);
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

            if (_overlay != null && _overlay.IsFadedOut)
            {
                _detentClicks = 0;
                _detentDirection = 0;
            }

            bool isVolMode = (_settings.OperationMode == "Volume Control");
            int activeKeyUp = isVolMode ? _settings.VolKeyUp : _settings.CrossfadeKeyUp;
            int activeKeyDown = isVolMode ? _settings.VolKeyDown : _settings.CrossfadeKeyDown;
            int activeKeyMute = isVolMode ? _settings.VolKeyMute : _settings.CrossfadeKeyMute;

            int upSteps = 0;
            while ((GetAsyncKeyState(activeKeyUp) & 0x0001) != 0) { upSteps++; }

            int downSteps = 0;
            while ((GetAsyncKeyState(activeKeyDown) & 0x0001) != 0) { downSteps++; }

            bool currentMute = (GetAsyncKeyState(activeKeyMute) & 0x8000) != 0;
            if (currentMute && !lastMuteState) 
            { 
                CycleMuteState(); 
                physicalInputRegistered = true; 
            }
            lastMuteState = currentMute;

            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;

            if (upSteps > 0)
            {
                Step(stepIncrement * upSteps);
                physicalInputRegistered = true;
            }
            if (downSteps > 0)
            {
                Step(-stepIncrement * downSteps);
                physicalInputRegistered = true;
            }

            if (_settings.EnableInterpolation)
            {
                float diff = _targetPosition - _visualPosition;
                if (Math.Abs(diff) > 0.0005f)
                {
                    _visualPosition += diff * 0.45f;
                }
                else
                {
                    _visualPosition = _targetPosition;
                }
            }
            else
            {
                _visualPosition = _targetPosition;
            }

            if (_visualPosition <= 0.002f) _visualPosition = 0.0f;
            if (_visualPosition >= 0.998f) _visualPosition = 1.0f;

            ApplyVolumes(false, physicalInputRegistered);

            var now = DateTime.Now;
            if (physicalInputRegistered && (now - lastAudioApplyTime).TotalMilliseconds >= 15)
            {
                ApplyVolumes(true, false);
                lastAudioApplyTime = now;
            }

            Thread.Sleep(2);
        }
    }

    private string GetResolvedLabel(string sourceTarget)
    {
        if (string.IsNullOrEmpty(sourceTarget)) return "";
        if (sourceTarget.StartsWith("Group: "))
        {
            return sourceTarget.Substring(7).ToUpper();
        }
        return sourceTarget.Replace(".exe", "").ToUpper();
    }

    public void Step(float delta)
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        float minPos = 0.0f;
        float maxPos = 1.0f;

        if (isVolumeControl)
        {
            minPos = 0.0f;
            maxPos = 0.5f; 
        }

        if (isVolumeControl || !_settings.EnableEqualizationBuffer)
        {
            float newTarget = _targetPosition + delta;
            _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            return;
        }

        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            bool isFromRest = (_overlay == null || _overlay.IsFadedOut);
            int currentDeltaSign = Math.Sign(delta);

            if (isFromRest || (currentDeltaSign != 0 && currentDeltaSign != _detentDirection))
            {
                float newTarget = _targetPosition + delta;
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                _detentClicks = 0;
                _detentDirection = currentDeltaSign;
            }
            else
            {
                _detentClicks++;
                if (_detentClicks < 3)
                {
                    return;
                }
                else
                {
                    _detentClicks = 0;
                    float newTarget = _targetPosition + delta;
                    _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                    _detentDirection = currentDeltaSign;
                }
            }
        }
        else
        {
            float newTarget = _targetPosition + delta;
            bool crossingCenter = (_targetPosition < 0.5f && newTarget >= 0.5f) || (_targetPosition > 0.5f && newTarget <= 0.5f);
            if (crossingCenter)
            {
                _targetPosition = 0.5f;
                _detentClicks = 0;
                _detentDirection = Math.Sign(delta);
            }
            else
            {
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            }
        }
    }

    public void CycleMuteState()
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");
        if (isVolumeControl)
        {
            _muteState = (_muteState == 0) ? 1 : 0;
        }
        else
        {
            _muteState = (_muteState + 1) % 3;
        }
    }

    private void ApplyVolumes(bool updateAudioSessions, bool forceShowPopup)
    {
        float audioApp1Vol = 0.0f;
        float audioApp2Vol = 0.0f;

        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        if (isVolumeControl)
        {
            audioApp1Vol = _targetPosition * 2.0f;
        }
        else
        {
            if (_targetPosition >= 0.5f)
            {
                audioApp1Vol = 1.0f;
                audioApp2Vol = 1.0f - ((_targetPosition - 0.5f) * 2.0f);
            }
            else
            {
                audioApp2Vol = 1.0f;
                audioApp1Vol = _targetPosition * 2.0f;
            }
        }

        audioApp1Vol = Math.Clamp(audioApp1Vol, 0.0f, 1.0f);
        audioApp2Vol = Math.Clamp(audioApp2Vol, 0.0f, 1.0f);

        if (audioApp1Vol <= 0.01f) audioApp1Vol = 0.0f;
        if (audioApp1Vol >= 0.99f) audioApp1Vol = 1.0f;
        if (audioApp2Vol <= 0.01f) audioApp2Vol = 0.0f;
        if (audioApp2Vol >= 0.99f) audioApp2Vol = 1.0f;

        float actualApp1Vol = audioApp1Vol;
        float actualApp2Vol = audioApp2Vol;

        if (_muteState == 1 && !isVolumeControl) actualApp2Vol = 0.0f;
        else if (_muteState == 2 && !isVolumeControl) actualApp1Vol = 0.0f;
        else if (_muteState > 0 && isVolumeControl) actualApp1Vol = 0.0f;

        if (updateAudioSessions)
        {
            ApplyGroupVolume(_settings.Source1, actualApp1Vol);
            if (!isVolumeControl)
            {
                ApplyGroupVolume(_settings.Source2, actualApp2Vol);
            }
        }

        float visualApp1Vol = 0.0f;
        float visualApp2Vol = 0.0f;

        if (isVolumeControl)
        {
            visualApp1Vol = _visualPosition * 2.0f;
        }
        else
        {
            if (_visualPosition >= 0.5f)
            {
                visualApp1Vol = 1.0f;
                visualApp2Vol = 1.0f - ((_visualPosition - 0.5f) * 2.0f);
            }
            else
            {
                visualApp2Vol = 1.0f;
                visualApp1Vol = _visualPosition * 2.0f;
            }
        }

        visualApp1Vol = Math.Clamp(visualApp1Vol, 0.0f, 1.0f);
        visualApp2Vol = Math.Clamp(visualApp2Vol, 0.0f, 1.0f);

        _overlay?.UpdateVolumes(visualApp1Vol, visualApp2Vol, _muteState, forceShowPopup);
    }

    private void ApplyGroupVolume(string targetIdentifier, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetIdentifier)) return;

        List<string> targets = new List<string>();
        if (targetIdentifier.StartsWith("Group: "))
        {
            string groupName = targetIdentifier.Substring(7);
            var group = _settings.Groups.FirstOrDefault(g => g.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                targets.AddRange(group.Executables);
            }
        }
        else
        {
            targets.Add(targetIdentifier);
        }

        foreach (var t in targets)
        {
            SetApplicationVolume(t, volume);
        }
    }

    private void SetApplicationVolume(string targetApp, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetApp)) return;

        try
        {
            string targetKey = targetApp.ToLower().Replace(".exe", "").Trim();
            if (string.IsNullOrEmpty(targetKey)) return;

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
                            if (proc.ProcessName.ToLower().Equals(targetKey))
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

// Configuration Menu Window with Tabs
class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private TabControl _tabControl = null!;
    private ComboBox _cmbMode = null!;
    private ComboBox _cmbSource1 = null!;
    private ComboBox _cmbSource2 = null!;
    private Label _lblSource2 = null!;

    // Crossfade Keybind Boxes
    private TextBox _txtCrossfadeUp = null!;
    private TextBox _txtCrossfadeMute = null!;
    private TextBox _txtCrossfadeDown = null!;

    // Volume Control Keybind Boxes
    private TextBox _txtVolUp = null!;
    private TextBox _txtVolMute = null!;
    private TextBox _txtVolDown = null!;

    private ComboBox _cmbPosition = null!;
    private TextBox _txtPopupDuration = null!;
    private TextBox _txtStepSize = null!;
    private TextBox _txtWidth = null!;
    private TextBox _txtHeight = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkInterpolation = null!;
    private CheckBox _chkBuffer = null!;
    
    private FlowLayoutPanel _groupsFlowPanel = null!;
    private TrayApplicationContext _context;
    private AppSettings _currentSettings;
    
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
        _currentSettings = settings;
        _currentUnmutedHex = settings.UnmutedColor;
        _currentMutedHex = settings.MutedColor;

        this.Text = "Audio Crossfader Settings";
        this.Size = new Size(460, 780);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.KeyPreview = true;

        this.BackColor = Color.FromArgb(32, 32, 32);
        this.ForeColor = Color.FromArgb(240, 240, 240);

        _tabControl = new TabControl { Dock = DockStyle.Fill };
        _tabControl.TabPages.Add(CreateGeneralTab());
        _tabControl.TabPages.Add(CreateGroupsTab());
        _tabControl.TabPages.Add(CreatePersonalizationTab());
        Controls.Add(_tabControl);

        _mouseFilter = new KeyCaptureFilter(CancelRecording);
        Application.AddMessageFilter(_mouseFilter);

        this.FormClosed += (s, e) =>
        {
            if (_mouseFilter != null) Application.RemoveMessageFilter(_mouseFilter);
        };

        this.KeyDown += SettingsForm_KeyDown;
    }

    private TabPage CreateGeneralTab()
    {
        TabPage page = new TabPage("General & Sources") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(panel);

        int y = 20;

        AddLabel(panel, "Operation Mode:", 20, ref y);
        _cmbMode = new ComboBox
        {
            Left = 20,
            Top = y,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbMode.Items.AddRange(new object[] { "Crossfade", "Volume Control" });
        _cmbMode.SelectedItem = _currentSettings.OperationMode;
        if (_cmbMode.SelectedIndex == -1) _cmbMode.SelectedIndex = 0;
        _cmbMode.SelectedIndexChanged += (s, e) => UpdateSourceVisibility();
        panel.Controls.Add(_cmbMode);
        y += 35;

        List<string> options = GetAvailableSourceOptions();

        AddLabel(panel, "Source 1 / Audio Target:", 20, ref y);
        _cmbSource1 = AddComboBox(panel, options, _currentSettings.Source1, ref y);

        _lblSource2 = AddLabel(panel, "Source 2 / Crossfade Target:", 20, ref y);
        _cmbSource2 = AddComboBox(panel, options, _currentSettings.Source2, ref y);

        UpdateSourceVisibility();

        AddLabel(panel, "Crossfade Mode Keybinds:", 20, ref y);
        _txtCrossfadeUp = AddKeyBindRow(panel, "Up / Right Key:", $"0x{_currentSettings.CrossfadeKeyUp:X}", ref y);
        _txtCrossfadeDown = AddKeyBindRow(panel, "Down / Left Key:", $"0x{_currentSettings.CrossfadeKeyDown:X}", ref y);
        _txtCrossfadeMute = AddKeyBindRow(panel, "Cycle Mute Key:", $"0x{_currentSettings.CrossfadeKeyMute:X}", ref y);

        y += 5;
        AddLabel(panel, "Volume Control Mode Keybinds:", 20, ref y);
        _txtVolUp = AddKeyBindRow(panel, "Volume Up Key:", $"0x{_currentSettings.VolKeyUp:X}", ref y);
        _txtVolDown = AddKeyBindRow(panel, "Volume Down Key:", $"0x{_currentSettings.VolKeyDown:X}", ref y);
        _txtVolMute = AddKeyBindRow(panel, "Mute / Unmute Key:", $"0x{_currentSettings.VolKeyMute:X}", ref y);

        y += 5;
        AddLabel(panel, "Step Size (%, 1 - 100):", 20, ref y);
        _txtStepSize = AddTextBox(panel, _currentSettings.StepSize.ToString(), ref y);

        _chkBuffer = new CheckBox { Text = "Enable 100% Equalization Buffer (Crossfade)", Checked = _currentSettings.EnableEqualizationBuffer, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkBuffer);
        y += 35;

        _chkStartup = new CheckBox { Text = "Run on Windows Startup", Checked = _currentSettings.RunOnStartup, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkStartup);
        y += 45;

        AddBottomButtons(panel, ref y);
        return page;
    }

    private TabPage CreateGroupsTab()
    {
        TabPage page = new TabPage("Program Groups") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(container);

        Button btnAddGroup = new Button
        {
            Text = "+ Add New Group",
            Left = 20,
            Top = 20,
            Width = 160,
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnAddGroup.FlatAppearance.BorderSize = 0;
        btnAddGroup.Click += (s, e) =>
        {
            _currentSettings.Groups.Add(new ProgramGroup { GroupName = "NewGroup", Executables = new List<string> { "app.exe" } });
            RefreshGroupsTabUI();
        };
        container.Controls.Add(btnAddGroup);

        _groupsFlowPanel = new FlowLayoutPanel
        {
            Left = 20,
            Top = 65,
            Width = 395,
            Height = 600,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        container.Controls.Add(_groupsFlowPanel);

        PopulateGroupsFlowContent();
        return page;
    }

    private TabPage CreatePersonalizationTab()
    {
        TabPage page = new TabPage("Personalization") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(panel);

        int y = 20;

        AddLabel(panel, "Overlay Colors:", 20, ref y);
        AddColorPickerRow(panel, "Unmuted Color:", _currentUnmutedHex, (hex) => _currentUnmutedHex = hex, ref y);
        AddColorPickerRow(panel, "Muted Color:", _currentMutedHex, (hex) => _currentMutedHex = hex, ref y);

        y += 5;
        AddLabel(panel, "Overlay Position:", 20, ref y);
        _cmbPosition = new ComboBox
        {
            Left = 160,
            Top = y - 3,
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbPosition.Items.AddRange(new object[] { "Bottom Center", "Top Center", "Bottom Right", "Bottom Left", "Top Right", "Top Left" });
        _cmbPosition.SelectedItem = _currentSettings.OverlayPosition;
        if (_cmbPosition.SelectedIndex == -1) _cmbPosition.SelectedIndex = 0;
        panel.Controls.Add(_cmbPosition);
        y += 35;

        AddLabel(panel, "Popup Duration (ms):", 20, ref y);
        _txtPopupDuration = AddTextBox(panel, _currentSettings.PopupDurationMs.ToString(), ref y);

        AddLabel(panel, "Overlay Size (Width / Height):", 20, ref y);
        _txtWidth = AddNumericRow(panel, "Width:", _currentSettings.OverlayWidth.ToString(), 20, ref y);
        _txtHeight = AddNumericRow(panel, "Height:", _currentSettings.OverlayHeight.ToString(), 20, ref y);

        _chkInterpolation = new CheckBox { Text = "Smooth Volume Bar Interpolation", Checked = _currentSettings.EnableInterpolation, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkInterpolation);
        y += 50;

        AddBottomButtons(panel, ref y);
        return page;
    }

    private void AddBottomButtons(Panel panel, ref int y)
    {
        Button btnResetVol = new Button 
        { 
            Text = "Reset Volumes", 
            Left = 20, 
            Top = y, 
            Width = 130, 
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnResetVol.FlatAppearance.BorderSize = 0;
        btnResetVol.Click += (s, e) => SaveChanges(true);
        panel.Controls.Add(btnResetVol);

        Button btnSave = new Button 
        { 
            Text = "Save & Apply", 
            Left = 280, 
            Top = y, 
            Width = 130, 
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => SaveChanges(false);
        panel.Controls.Add(btnSave);
    }

    private void RefreshGroupsTabUI()
    {
        PopulateGroupsFlowContent();
        UpdateSourceDropdownOptions();
    }

    private void PopulateGroupsFlowContent()
    {
        _groupsFlowPanel.Controls.Clear();
        foreach (var group in _currentSettings.Groups)
        {
            Panel card = new Panel
            {
                Width = 375,
                BackColor = Color.FromArgb(45, 45, 45),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            TextBox txtGroupName = new TextBox
            {
                Text = group.GroupName,
                Left = 10,
                Top = 10,
                Width = 220,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtGroupName.TextChanged += (s, e) => 
            { 
                group.GroupName = txtGroupName.Text.Trim(); 
                UpdateSourceDropdownOptions();
            };
            card.Controls.Add(txtGroupName);

            Button btnRemoveGroup = new Button
            {
                Text = "Delete Group",
                Left = 245,
                Top = 9,
                Width = 115,
                Height = 24,
                BackColor = Color.FromArgb(180, 50, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRemoveGroup.FlatAppearance.BorderSize = 0;
            btnRemoveGroup.Click += (s, e) =>
            {
                _currentSettings.Groups.Remove(group);
                RefreshGroupsTabUI();
            };
            card.Controls.Add(btnRemoveGroup);

            int innerY = 45;
            for (int i = 0; i < group.Executables.Count; i++)
            {
                int index = i;
                TextBox txtExe = new TextBox
                {
                    Text = group.Executables[index],
                    Left = 20,
                    Top = innerY,
                    Width = 240,
                    BackColor = Color.FromArgb(35, 35, 35),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                txtExe.TextChanged += (s, e) => { group.Executables[index] = txtExe.Text.Trim(); };
                card.Controls.Add(txtExe);

                Button btnRemoveExe = new Button
                {
                    Text = "-",
                    Left = 270,
                    Top = innerY - 1,
                    Width = 30,
                    Height = 24,
                    BackColor = Color.FromArgb(80, 80, 80),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnRemoveExe.FlatAppearance.BorderSize = 0;
                btnRemoveExe.Click += (s, e) =>
                {
                    group.Executables.RemoveAt(index);
                    RefreshGroupsTabUI();
                };
                card.Controls.Add(btnRemoveExe);

                innerY += 32;
            }

            Button btnAddExe = new Button
            {
                Text = "+ Add Source Executable",
                Left = 20,
                Top = innerY,
                Width = 180,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnAddExe.FlatAppearance.BorderSize = 0;
            btnAddExe.Click += (s, e) =>
            {
                group.Executables.Add("process.exe");
                RefreshGroupsTabUI();
            };
            card.Controls.Add(btnAddExe);

            Panel paddingBottom = new Panel { Height = 10, Top = innerY + 30, Width = 375 };
            card.Controls.Add(paddingBottom);

            _groupsFlowPanel.Controls.Add(card);
        }
    }

    private List<string> GetAvailableSourceOptions()
    {
        List<string> options = new List<string>();
        foreach (var group in _currentSettings.Groups)
        {
            if (!string.IsNullOrEmpty(group.GroupName))
            {
                options.Add($"Group: {group.GroupName}");
            }
        }
        options.Add("chrome.exe");
        options.Add("firefox.exe");
        options.Add("spotify.exe");
        options.Add("discord.exe");
        options.Add("vlc.exe");
        return options.Distinct().ToList();
    }

    private void UpdateSourceDropdownOptions()
    {
        if (_cmbSource1 == null || _cmbSource2 == null) return;
        List<string> options = GetAvailableSourceOptions();
        
        string cur1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text;
        string cur2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text;

        _cmbSource1.Items.Clear();
        _cmbSource1.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource1.SelectedItem = cur1;

        _cmbSource2.Items.Clear();
        _cmbSource2.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource2.SelectedItem = cur2;
    }

    private void UpdateSourceVisibility()
    {
        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");
        _cmbSource2.Visible = !isVolumeControl;
        _lblSource2.Visible = !isVolumeControl;
    }

    private ComboBox AddComboBox(Panel panel, List<string> items, string selectedValue, ref int top)
    {
        ComboBox cmb = new ComboBox
        {
            Left = 20,
            Top = top,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        cmb.Items.AddRange(items.Cast<object>().ToArray());
        cmb.SelectedItem = selectedValue;
        if (cmb.SelectedIndex == -1) cmb.Text = selectedValue;
        panel.Controls.Add(cmb);
        top += 35;
        return cmb;
    }

    private TextBox AddTextBox(Panel panel, string text, ref int top)
    {
        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = 20, 
            Top = top, 
            Width = 390, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private TextBox AddNumericRow(Panel panel, string labelText, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = left, Top = top + 3, Width = 50, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = left + 55, 
            Top = top, 
            Width = 120, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private TextBox AddKeyBindRow(Panel panel, string labelText, string initialHex, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = initialHex, 
            Left = 170, 
            Top = top, 
            Width = 90, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };
        panel.Controls.Add(txt);

        Button btnRecord = new Button
        {
            Text = "Record",
            Left = 270,
            Top = top - 1,
            Width = 140,
            Height = 24,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRecord.FlatAppearance.BorderSize = 0;
        btnRecord.Click += (s, e) => StartRecording(btnRecord, txt);
        panel.Controls.Add(btnRecord);

        top += 32;
        return txt;
    }

    private void AddColorPickerRow(Panel panel, string labelText, string initialHex, Action<string> onColorChanged, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        Panel colorBox = new Panel 
        { 
            Left = 170, 
            Top = top, 
            Width = 240, 
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

        panel.Controls.Add(colorBox);
        top += 32;
    }

    private Label AddLabel(Panel panel, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = text, Left = left, Top = top, Width = 390, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);
        top += 20;
        return lbl;
    }

    private void SettingsForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;
        if (e.KeyCode == Keys.Escape) { CancelRecording(); e.Handled = true; return; }
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;

        int vk = (int)e.KeyCode;
        if (_activeTargetTextBox != null) _activeTargetTextBox.Text = $"0x{vk:X}";

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
            if (m.Msg == 0x0201 || m.Msg == 0x0204) _onMouseClick();
            return false;
        }
    }

    private void SaveChanges(bool resetVolumes)
    {
        string s1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text.Trim();
        string s2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text.Trim();
        string mode = _cmbMode.SelectedItem?.ToString() ?? "Crossfade";

        int ParseHexKey(string text, int defaultVal)
        {
            try
            {
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt32(text, 16);
                return int.Parse(text);
            }
            catch { return defaultVal; }
        }

        int cfUp = ParseHexKey(_txtCrossfadeUp.Text, 0x7E);
        int cfDown = ParseHexKey(_txtCrossfadeDown.Text, 0x80);
        int cfMute = ParseHexKey(_txtCrossfadeMute.Text, 0x7F);

        int volUp = ParseHexKey(_txtVolUp.Text, 0x26);
        int volDown = ParseHexKey(_txtVolDown.Text, 0x28);
        int volMute = ParseHexKey(_txtVolMute.Text, 0x20);

        // Check for duplicate keybindings within the same option set
        if (cfUp == cfDown || cfUp == cfMute || cfDown == cfMute)
        {
            MessageBox.Show("Crossfade mode cannot have duplicate keybindings assigned to different functions.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (volUp == volDown || volUp == volMute || volDown == volMute)
        {
            MessageBox.Show("Volume Control mode cannot have duplicate keybindings assigned to different functions.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (mode == "Crossfade")
        {
            if (!string.IsNullOrEmpty(s1) && !string.IsNullOrEmpty(s2) && s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Source 1 and Source 2 cannot be the exact same program or group.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                MessageBox.Show("Crossfade mode requires both Source 1 and Source 2 to be selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(s1))
            {
                MessageBox.Show("Volume Control mode requires a source or group selection.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        int ParseInt(string text, int defaultVal) => int.TryParse(text, out int val) ? val : defaultVal;

        var newSettings = new AppSettings
        {
            OperationMode = mode,
            Source1 = s1,
            Source2 = s2,
            Groups = _currentSettings.Groups,
            RunOnStartup = _chkStartup.Checked,
            CrossfadeKeyUp = cfUp,
            CrossfadeKeyDown = cfDown,
            CrossfadeKeyMute = cfMute,
            VolKeyUp = volUp,
            VolKeyDown = volDown,
            VolKeyMute = volMute,
            UnmutedColor = _currentUnmutedHex,
            MutedColor = _currentMutedHex,
            OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center",
            PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000),
            StepSize = Math.Clamp(ParseInt(_txtStepSize.Text, 10), 1, 100),
            OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 340), 100, 2000),
            OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 75), 50, 1000),
            EnableInterpolation = _chkInterpolation.Checked,
            EnableEqualizationBuffer = _chkBuffer.Checked
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (newSettings.RunOnStartup) key?.SetValue("AudioCrossfader", $"\"{exePath}\"");
            else key?.DeleteValue("AudioCrossfader", false);
        }
        catch { }

        _context.SaveSettings(newSettings, resetVolumes);
        MessageBox.Show("Settings saved successfully!", "Audio Crossfader", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

// On-Screen Overlay Popup
class VolumeOverlayForm : Form
{
    private readonly string _app1Label;
    private readonly string _app2Label;
    private readonly AppSettings _settings;
    private readonly bool _isSingleSource;
    private readonly string _singleLabel;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private int _totalDurationMs = 2500;
    private bool _isFadingOut = false;

    public bool IsFadedOut => this.Opacity <= 0.0;

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = FormatLabel(app1);
        _app2Label = FormatLabel(app2);
        _settings = settings;

        _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));
        _singleLabel = string.IsNullOrEmpty(_app1Label) ? _app2Label : _app1Label;

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ClientSize = new Size(Math.Max(100, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

        _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int x = workingArea.Left + (workingArea.Width - this.Width) / 2;
        int y = workingArea.Bottom - this.Height - 65;

        switch (_settings.OverlayPosition)
        {
            case "Top Center":
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Top + 40; break;
            case "Bottom Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Bottom Left":
                x = workingArea.Left + 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Top Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Top + 40; break;
            case "Top Left":
                x = workingArea.Left + 40; y = workingArea.Top + 40; break;
            case "Bottom Center":
            default:
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Bottom - this.Height - 65; break;
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

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _fadeTimer.Tick += (s, e) =>
        {
            if (!_isFadingOut)
            {
                if (this.Opacity < 1.0) this.Opacity = Math.Min(1.0, this.Opacity + 0.15);
                if (_displayCountdownMs > 0)
                {
                    _displayCountdownMs -= 15;
                    if (_displayCountdownMs <= 0) _isFadingOut = true;
                }
            }
            else
            {
                if (this.Opacity > 0.0) this.Opacity = Math.Max(0.0, this.Opacity - 0.08);
                else { this.Opacity = 0.0; _fadeTimer.Stop(); }
            }
        };
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

    private string FormatLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("Group: ")) return raw.Substring(7).ToUpper();
        return raw.Replace(".exe", "").ToUpper();
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
                if (forceShow && _fadeTimer != null && !_fadeTimer.Enabled) _fadeTimer.Start();
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
        float scale = Math.Clamp((float)this.Height / 75f, 0.5f, 5.0f);
        float fontSize = Math.Clamp(13f * scale, 6.5f, 32f);

        if (_isSingleSource)
        {
            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF labelSize = g.MeasureString(_singleLabel, font);
                float textY = (this.Height - labelSize.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);
                g.DrawString(_singleLabel, font, Brushes.White, centerX - (labelSize.Width / 2f), textY);
            }

            int absoluteSideMargin = 30;
            int barWidth = this.Width - (absoluteSideMargin * 2);
            if (barWidth < 20) barWidth = 20;

            int barHeight = (int)(14 * scale);
            barHeight = Math.Clamp(barHeight, 8, 40);

            int barY = (int)(this.Height * 0.58f);
            barY = Math.Clamp(barY, 10, Math.Max(10, this.Height - barHeight - 6));

            Color unmutedColor = ColorTranslator.FromHtml(_settings.UnmutedColor);
            Color mutedColor = ColorTranslator.FromHtml(_settings.MutedColor);

            bool isMuted = (_muteState > 0);
            using Brush barColor = new SolidBrush(isMuted ? mutedColor : unmutedColor);
            using Brush trackBgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));

            using (GraphicsPath? trackPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, barWidth, barHeight, true, true))
            {
                if (trackPath != null) g.FillPath(trackBgBrush, trackPath);
            }

            int fillWidth = (int)(barWidth * _app1Vol);
            if (fillWidth > 0)
            {
                using (GraphicsPath? fillPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, fillWidth, barHeight, true, true))
                {
                    if (fillPath != null) g.FillPath(barColor, fillPath);
                }
            }
        }
        else
        {
            int centerLineTop = (int)(10 * scale);
            int centerLineHeight = Math.Max(10, this.Height - (centerLineTop * 2));
            g.FillRectangle(Brushes.DimGray, centerX - 1, centerLineTop, 2, centerLineHeight);

            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF app1Size = g.MeasureString(_app1Label, font);
                float textGap = 12 * scale;
                float textY = (this.Height - app1Size.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);

                g.DrawString(_app1Label, font, Brushes.White, centerX - textGap - app1Size.Width, textY);
                g.DrawString(_app2Label, font, Brushes.White, centerX + textGap, textY);
            }

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

            using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
            {
                if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
            }
            using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
            {
                if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
            }

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
        }

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

                using (Pen pen = new Pen(Color.FromArgb(70, 70, 70), 1)) g.DrawPath(pen, borderPath);
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
}*/

/*using System.Diagnostics;
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
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

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

    public void SaveSettings(AppSettings newSettings, bool resetVolumes = false)
    {
        _settings = newSettings;
        try
        {
            string json = JsonSerializer.Serialize(_settings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }

        _crossfaderWorker?.UpdateSettings(_settings, resetVolumes);
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

class ProgramGroup
{
    public string GroupName { get; set; } = "";
    public List<string> Executables { get; set; } = new List<string>();
}

class AppSettings
{
    public string OperationMode { get; set; } = "Crossfade"; // "Crossfade" or "Volume Control"
    public string Source1 { get; set; } = "chrome.exe";
    public string Source2 { get; set; } = "firefox.exe";
    public List<ProgramGroup> Groups { get; set; } = new List<ProgramGroup>();
    public bool RunOnStartup { get; set; } = false;
    
    public int KeyUp { get; set; } = 0x7E;    // VK_F15
    public int KeyMute { get; set; } = 0x7F;   // VK_F16
    public int KeyDown { get; set; } = 0x80;   // VK_F17

    public string UnmutedColor { get; set; } = "#00A8FF"; 
    public string MutedColor { get; set; } = "#FF4757";   
    public string OverlayPosition { get; set; } = "Bottom Center";
    public int PopupDurationMs { get; set; } = 2500;

    public int OverlayWidth { get; set; } = 340;
    public int OverlayHeight { get; set; } = 75;
    public bool EnableInterpolation { get; set; } = true;
    public int StepSize { get; set; } = 10; 
    public bool EnableEqualizationBuffer { get; set; } = true; 
}

class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private AppSettings _settings;
    private float _targetPosition;       
    private float _visualPosition;     
    private int _muteState = 0;
    private int _detentClicks = 0;            
    private int _detentDirection = 0;         
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private bool _running = false;

    public AudioCrossfaderWorker(AppSettings settings)
    {
        _settings = settings;
        _deviceEnumerator = new MMDeviceEnumerator();
        InitDefaultPosition();
    }

    private void InitDefaultPosition()
    {
        if (_settings.OperationMode == "Volume Control")
        {
            _targetPosition = 0.5f; // 100% volume state for single control mode
        }
        else
        {
            _targetPosition = 0.5f; // 50/50 crossfade center
        }
        _visualPosition = _targetPosition;
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

    public void UpdateSettings(AppSettings newSettings, bool resetVolumes)
    {
        _settings = newSettings;
        if (resetVolumes)
        {
            InitDefaultPosition();
            _muteState = 0;
            ResetVolumesToFull();
        }

        Stop();
        Start();
    }

    private void ResetVolumesToFull()
    {
        try
        {
            ApplyGroupVolume(_settings.Source1, 1.0f);
            ApplyGroupVolume(_settings.Source2, 1.0f);
        }
        catch { }
    }

    private void RunLoop()
    {
        string label1 = GetResolvedLabel(_settings.Source1);
        string label2 = GetResolvedLabel(_settings.Source2);

        Thread uiThread = new Thread(() =>
        {
            _overlay = new VolumeOverlayForm(label1, label2, _settings);
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

            if (_overlay != null && _overlay.IsFadedOut)
            {
                _detentClicks = 0;
                _detentDirection = 0;
            }

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

            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;

            if (upSteps > 0)
            {
                Step(stepIncrement * upSteps);
                physicalInputRegistered = true;
            }
            if (downSteps > 0)
            {
                Step(-stepIncrement * downSteps);
                physicalInputRegistered = true;
            }

            if (_settings.EnableInterpolation)
            {
                float diff = _targetPosition - _visualPosition;
                if (Math.Abs(diff) > 0.0005f)
                {
                    _visualPosition += diff * 0.45f;
                }
                else
                {
                    _visualPosition = _targetPosition;
                }
            }
            else
            {
                _visualPosition = _targetPosition;
            }

            if (_visualPosition <= 0.002f) _visualPosition = 0.0f;
            if (_visualPosition >= 0.998f) _visualPosition = 1.0f;

            ApplyVolumes(false, physicalInputRegistered);

            var now = DateTime.Now;
            if (physicalInputRegistered && (now - lastAudioApplyTime).TotalMilliseconds >= 15)
            {
                ApplyVolumes(true, false);
                lastAudioApplyTime = now;
            }

            Thread.Sleep(2);
        }
    }

    private string GetResolvedLabel(string sourceTarget)
    {
        if (string.IsNullOrEmpty(sourceTarget)) return "";
        if (sourceTarget.StartsWith("Group: "))
        {
            return sourceTarget.Substring(7).ToUpper();
        }
        return sourceTarget.Replace(".exe", "").ToUpper();
    }

    public void Step(float delta)
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        float minPos = 0.0f;
        float maxPos = 1.0f;

        if (isVolumeControl)
        {
            minPos = 0.0f;
            maxPos = 0.5f; // Scaled range for single volume slider
        }

        if (isVolumeControl || !_settings.EnableEqualizationBuffer)
        {
            float newTarget = _targetPosition + delta;
            _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            return;
        }

        // Dual source crossfader detent buffer logic
        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            bool isFromRest = (_overlay == null || _overlay.IsFadedOut);
            int currentDeltaSign = Math.Sign(delta);

            if (isFromRest || (currentDeltaSign != 0 && currentDeltaSign != _detentDirection))
            {
                float newTarget = _targetPosition + delta;
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                _detentClicks = 0;
                _detentDirection = currentDeltaSign;
            }
            else
            {
                _detentClicks++;
                if (_detentClicks < 3)
                {
                    return;
                }
                else
                {
                    _detentClicks = 0;
                    float newTarget = _targetPosition + delta;
                    _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
                    _detentDirection = currentDeltaSign;
                }
            }
        }
        else
        {
            float newTarget = _targetPosition + delta;
            bool crossingCenter = (_targetPosition < 0.5f && newTarget >= 0.5f) || (_targetPosition > 0.5f && newTarget <= 0.5f);
            if (crossingCenter)
            {
                _targetPosition = 0.5f;
                _detentClicks = 0;
                _detentDirection = Math.Sign(delta);
            }
            else
            {
                _targetPosition = Math.Clamp(newTarget, minPos, maxPos);
            }
        }
    }

    public void CycleMuteState()
    {
        _muteState = (_muteState + 1) % 3;
    }

    private void ApplyVolumes(bool updateAudioSessions, bool forceShowPopup)
    {
        float audioApp1Vol = 0.0f;
        float audioApp2Vol = 0.0f;

        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        if (isVolumeControl)
        {
            audioApp1Vol = _targetPosition * 2.0f;
        }
        else
        {
            if (_targetPosition >= 0.5f)
            {
                audioApp1Vol = 1.0f;
                audioApp2Vol = 1.0f - ((_targetPosition - 0.5f) * 2.0f);
            }
            else
            {
                audioApp2Vol = 1.0f;
                audioApp1Vol = _targetPosition * 2.0f;
            }
        }

        audioApp1Vol = Math.Clamp(audioApp1Vol, 0.0f, 1.0f);
        audioApp2Vol = Math.Clamp(audioApp2Vol, 0.0f, 1.0f);

        if (audioApp1Vol <= 0.01f) audioApp1Vol = 0.0f;
        if (audioApp1Vol >= 0.99f) audioApp1Vol = 1.0f;
        if (audioApp2Vol <= 0.01f) audioApp2Vol = 0.0f;
        if (audioApp2Vol >= 0.99f) audioApp2Vol = 1.0f;

        float actualApp1Vol = audioApp1Vol;
        float actualApp2Vol = audioApp2Vol;

        if (_muteState == 1 && !isVolumeControl) actualApp2Vol = 0.0f;
        else if (_muteState == 2 && !isVolumeControl) actualApp1Vol = 0.0f;
        else if (_muteState > 0 && isVolumeControl) actualApp1Vol = 0.0f;

        if (updateAudioSessions)
        {
            ApplyGroupVolume(_settings.Source1, actualApp1Vol);
            if (!isVolumeControl)
            {
                ApplyGroupVolume(_settings.Source2, actualApp2Vol);
            }
        }

        // Calculate visual volumes
        float visualApp1Vol = 0.0f;
        float visualApp2Vol = 0.0f;

        if (isVolumeControl)
        {
            visualApp1Vol = _visualPosition * 2.0f;
        }
        else
        {
            if (_visualPosition >= 0.5f)
            {
                visualApp1Vol = 1.0f;
                visualApp2Vol = 1.0f - ((_visualPosition - 0.5f) * 2.0f);
            }
            else
            {
                visualApp2Vol = 1.0f;
                visualApp1Vol = _visualPosition * 2.0f;
            }
        }

        visualApp1Vol = Math.Clamp(visualApp1Vol, 0.0f, 1.0f);
        visualApp2Vol = Math.Clamp(visualApp2Vol, 0.0f, 1.0f);

        _overlay?.UpdateVolumes(visualApp1Vol, visualApp2Vol, _muteState, forceShowPopup);
    }

    private void ApplyGroupVolume(string targetIdentifier, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetIdentifier)) return;

        List<string> targets = new List<string>();
        if (targetIdentifier.StartsWith("Group: "))
        {
            string groupName = targetIdentifier.Substring(7);
            var group = _settings.Groups.FirstOrDefault(g => g.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                targets.AddRange(group.Executables);
            }
        }
        else
        {
            targets.Add(targetIdentifier);
        }

        foreach (var t in targets)
        {
            SetApplicationVolume(t, volume);
        }
    }

    private void SetApplicationVolume(string targetApp, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetApp)) return;

        try
        {
            string targetKey = targetApp.ToLower().Replace(".exe", "").Trim();
            if (string.IsNullOrEmpty(targetKey)) return;

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
                            if (proc.ProcessName.ToLower().Equals(targetKey))
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

// Configuration Menu Window with Groups Tab and Mode Selection
class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private TabControl _tabControl = null!;
    private ComboBox _cmbMode = null!;
    private ComboBox _cmbSource1 = null!;
    private ComboBox _cmbSource2 = null!;
    private Label _lblSource2 = null!;
    private TextBox _txtKeyUp = null!;
    private TextBox _txtKeyMute = null!;
    private TextBox _txtKeyDown = null!;
    private ComboBox _cmbPosition = null!;
    private TextBox _txtPopupDuration = null!;
    private TextBox _txtStepSize = null!;
    private TextBox _txtWidth = null!;
    private TextBox _txtHeight = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkInterpolation = null!;
    private CheckBox _chkBuffer = null!;
    
    private FlowLayoutPanel _groupsFlowPanel = null!;
    private TrayApplicationContext _context;
    private AppSettings _currentSettings;
    
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
        _currentSettings = settings;
        _currentUnmutedHex = settings.UnmutedColor;
        _currentMutedHex = settings.MutedColor;

        this.Text = "Audio Crossfader Settings";
        this.Size = new Size(460, 750);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.KeyPreview = true;

        this.BackColor = Color.FromArgb(32, 32, 32);
        this.ForeColor = Color.FromArgb(240, 240, 240);

        _tabControl = new TabControl { Dock = DockStyle.Fill, BackgroundImage = null };
        _tabControl.TabPages.Add(CreateGeneralTab());
        _tabControl.TabPages.Add(CreateGroupsTab());
        Controls.Add(_tabControl);

        _mouseFilter = new KeyCaptureFilter(CancelRecording);
        Application.AddMessageFilter(_mouseFilter);

        this.FormClosed += (s, e) =>
        {
            if (_mouseFilter != null) Application.RemoveMessageFilter(_mouseFilter);
        };

        this.KeyDown += SettingsForm_KeyDown;
    }

    private TabPage CreateGeneralTab()
    {
        TabPage page = new TabPage("General & Sources") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(panel);

        int y = 20;

        AddLabel(panel, "Operation Mode:", 20, ref y);
        _cmbMode = new ComboBox
        {
            Left = 20,
            Top = y,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbMode.Items.AddRange(new object[] { "Crossfade", "Volume Control" });
        _cmbMode.SelectedItem = _currentSettings.OperationMode;
        if (_cmbMode.SelectedIndex == -1) _cmbMode.SelectedIndex = 0;
        _cmbMode.SelectedIndexChanged += (s, e) => UpdateSourceVisibility();
        panel.Controls.Add(_cmbMode);
        y += 35;

        List<string> options = GetAvailableSourceOptions();

        AddLabel(panel, "Source 1 / Audio Target:", 20, ref y);
        _cmbSource1 = AddComboBox(panel, options, _currentSettings.Source1, ref y);

        _lblSource2 = AddLabel(panel, "Source 2 / Crossfade Target:", 20, ref y);
        _cmbSource2 = AddComboBox(panel, options, _currentSettings.Source2, ref y);

        UpdateSourceVisibility();

        AddLabel(panel, "Keybinds:", 20, ref y);
        _txtKeyUp = AddKeyBindRow(panel, "Up / Right Key:", $"0x{_currentSettings.KeyUp:X}", ref y);
        _txtKeyDown = AddKeyBindRow(panel, "Down / Left Key:", $"0x{_currentSettings.KeyDown:X}", ref y);
        _txtKeyMute = AddKeyBindRow(panel, "Cycle Mute Key:", $"0x{_currentSettings.KeyMute:X}", ref y);

        y += 5;
        AddLabel(panel, "Overlay Colors:", 20, ref y);
        AddColorPickerRow(panel, "Unmuted Color:", _currentUnmutedHex, (hex) => _currentUnmutedHex = hex, ref y);
        AddColorPickerRow(panel, "Muted Color:", _currentMutedHex, (hex) => _currentMutedHex = hex, ref y);

        y += 5;
        AddLabel(panel, "Overlay Position:", 20, ref y);
        _cmbPosition = new ComboBox
        {
            Left = 160,
            Top = y - 3,
            Width = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        _cmbPosition.Items.AddRange(new object[] { "Bottom Center", "Top Center", "Bottom Right", "Bottom Left", "Top Right", "Top Left" });
        _cmbPosition.SelectedItem = _currentSettings.OverlayPosition;
        if (_cmbPosition.SelectedIndex == -1) _cmbPosition.SelectedIndex = 0;
        panel.Controls.Add(_cmbPosition);
        y += 30;

        AddLabel(panel, "Step Size (%, 1 - 100):", 20, ref y);
        _txtStepSize = AddTextBox(panel, _currentSettings.StepSize.ToString(), ref y);

        AddLabel(panel, "Popup Duration (ms):", 20, ref y);
        _txtPopupDuration = AddTextBox(panel, _currentSettings.PopupDurationMs.ToString(), ref y);

        AddLabel(panel, "Overlay Size (Width / Height):", 20, ref y);
        _txtWidth = AddNumericRow(panel, "Width:", _currentSettings.OverlayWidth.ToString(), 20, ref y);
        _txtHeight = AddNumericRow(panel, "Height:", _currentSettings.OverlayHeight.ToString(), 20, ref y);

        _chkInterpolation = new CheckBox { Text = "Smooth Volume Bar Interpolation", Checked = _currentSettings.EnableInterpolation, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkInterpolation);
        y += 30;

        _chkBuffer = new CheckBox { Text = "Enable 100% Equalization Buffer", Checked = _currentSettings.EnableEqualizationBuffer, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkBuffer);
        y += 30;

        _chkStartup = new CheckBox { Text = "Run on Windows Startup", Checked = _currentSettings.RunOnStartup, Left = 20, Top = y, Width = 390, ForeColor = Color.White };
        panel.Controls.Add(_chkStartup);
        y += 40;

        Button btnResetVol = new Button 
        { 
            Text = "Reset Volumes", 
            Left = 20, 
            Top = y, 
            Width = 130, 
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnResetVol.FlatAppearance.BorderSize = 0;
        btnResetVol.Click += (s, e) => SaveChanges(true);
        panel.Controls.Add(btnResetVol);

        Button btnSave = new Button 
        { 
            Text = "Save & Apply", 
            Left = 280, 
            Top = y, 
            Width = 130, 
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => SaveChanges(false);
        panel.Controls.Add(btnSave);

        // Pad bottom scroll space
        Panel spacer = new Panel { Height = 40, Dock = DockStyle.Bottom };
        panel.Controls.Add(spacer);

        return page;
    }

    private TabPage CreateGroupsTab()
    {
        TabPage page = new TabPage("Program Groups") { BackColor = Color.FromArgb(32, 32, 32) };
        Panel container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(32, 32, 32) };
        page.Controls.Add(container);

        Button btnAddGroup = new Button
        {
            Text = "+ Add New Group",
            Left = 20,
            Top = 20,
            Width = 160,
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnAddGroup.FlatAppearance.BorderSize = 0;
        btnAddGroup.Click += (s, e) =>
        {
            _currentSettings.Groups.Add(new ProgramGroup { GroupName = "NewGroup", Executables = new List<string> { "app.exe" } });
            RefreshGroupsTabUI();
        };
        container.Controls.Add(btnAddGroup);

        _groupsFlowPanel = new FlowLayoutPanel
        {
            Left = 20,
            Top = 65,
            Width = 395,
            Height = 580,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        container.Controls.Add(_groupsFlowPanel);

        PopulateGroupsFlowContent();
        return page;
    }

    private void RefreshGroupsTabUI()
    {
        PopulateGroupsFlowContent();
        UpdateSourceDropdownOptions();
    }

    private void PopulateGroupsFlowContent()
    {
        _groupsFlowPanel.Controls.Clear();
        foreach (var group in _currentSettings.Groups)
        {
            Panel card = new Panel
            {
                Width = 375,
                BackColor = Color.FromArgb(45, 45, 45),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };

            TextBox txtGroupName = new TextBox
            {
                Text = group.GroupName,
                Left = 10,
                Top = 10,
                Width = 220,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtGroupName.TextChanged += (s, e) => { group.GroupName = txtGroupName.Text.Trim(); };
            card.Controls.Add(txtGroupName);

            Button btnRemoveGroup = new Button
            {
                Text = "Delete Group",
                Left = 245,
                Top = 9,
                Width = 115,
                Height = 24,
                BackColor = Color.FromArgb(180, 50, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRemoveGroup.FlatAppearance.BorderSize = 0;
            btnRemoveGroup.Click += (s, e) =>
            {
                _currentSettings.Groups.Remove(group);
                RefreshGroupsTabUI();
            };
            card.Controls.Add(btnRemoveGroup);

            int innerY = 45;
            for (int i = 0; i < group.Executables.Count; i++)
            {
                int index = i;
                TextBox txtExe = new TextBox
                {
                    Text = group.Executables[index],
                    Left = 20,
                    Top = innerY,
                    Width = 240,
                    BackColor = Color.FromArgb(35, 35, 35),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                txtExe.TextChanged += (s, e) => { group.Executables[index] = txtExe.Text.Trim(); };
                card.Controls.Add(txtExe);

                Button btnRemoveExe = new Button
                {
                    Text = "-",
                    Left = 270,
                    Top = innerY - 1,
                    Width = 30,
                    Height = 24,
                    BackColor = Color.FromArgb(80, 80, 80),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnRemoveExe.FlatAppearance.BorderSize = 0;
                btnRemoveExe.Click += (s, e) =>
                {
                    group.Executables.RemoveAt(index);
                    RefreshGroupsTabUI();
                };
                card.Controls.Add(btnRemoveExe);

                innerY += 32;
            }

            Button btnAddExe = new Button
            {
                Text = "+ Add Source Executable",
                Left = 20,
                Top = innerY,
                Width = 180,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnAddExe.FlatAppearance.BorderSize = 0;
            btnAddExe.Click += (s, e) =>
            {
                group.Executables.Add("process.exe");
                RefreshGroupsTabUI();
            };
            card.Controls.Add(btnAddExe);

            Panel paddingBottom = new Panel { Height = 10, Top = innerY + 30, Width = 375 };
            card.Controls.Add(paddingBottom);

            _groupsFlowPanel.Controls.Add(card);
        }
    }

    private List<string> GetAvailableSourceOptions()
    {
        List<string> options = new List<string> { "" };
        foreach (var group in _currentSettings.Groups)
        {
            if (!string.IsNullOrEmpty(group.GroupName))
            {
                options.Add($"Group: {group.GroupName}");
            }
        }
        return options.Distinct().ToList();
    }

    private void UpdateSourceDropdownOptions()
    {
        List<string> options = GetAvailableSourceOptions();
        
        string cur1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text;
        string cur2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text;

        _cmbSource1.Items.Clear();
        _cmbSource1.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource1.SelectedItem = cur1;

        _cmbSource2.Items.Clear();
        _cmbSource2.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource2.SelectedItem = cur2;
    }

    private void UpdateSourceVisibility()
    {
        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");
        _cmbSource2.Visible = !isVolumeControl;
        _lblSource2.Visible = !isVolumeControl;
    }

    private ComboBox AddComboBox(Panel panel, List<string> items, string selectedValue, ref int top)
    {
        ComboBox cmb = new ComboBox
        {
            Left = 20,
            Top = top,
            Width = 390,
            DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White
        };
        cmb.Items.AddRange(items.Cast<object>().ToArray());
        cmb.SelectedItem = selectedValue;
        if (cmb.SelectedIndex == -1) cmb.Text = selectedValue;
        panel.Controls.Add(cmb);
        top += 35;
        return cmb;
    }

    private TextBox AddTextBox(Panel panel, string text, ref int top)
    {
        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = 20, 
            Top = top, 
            Width = 390, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private TextBox AddNumericRow(Panel panel, string labelText, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = left, Top = top + 3, Width = 50, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = text, 
            Left = left + 55, 
            Top = top, 
            Width = 120, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(txt);
        top += 35;
        return txt;
    }

    private TextBox AddKeyBindRow(Panel panel, string labelText, string initialHex, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        TextBox txt = new TextBox 
        { 
            Text = initialHex, 
            Left = 170, 
            Top = top, 
            Width = 90, 
            BackColor = Color.FromArgb(45, 45, 45), 
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };
        panel.Controls.Add(txt);

        Button btnRecord = new Button
        {
            Text = "Record",
            Left = 270,
            Top = top - 1,
            Width = 140,
            Height = 24,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRecord.FlatAppearance.BorderSize = 0;
        btnRecord.Click += (s, e) => StartRecording(btnRecord, txt);
        panel.Controls.Add(btnRecord);

        top += 32;
        return txt;
    }

    private void AddColorPickerRow(Panel panel, string labelText, string initialHex, Action<string> onColorChanged, ref int top)
    {
        Label lbl = new Label { Text = labelText, Left = 20, Top = top + 3, Width = 140, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);

        Panel colorBox = new Panel 
        { 
            Left = 170, 
            Top = top, 
            Width = 240, 
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

        panel.Controls.Add(colorBox);
        top += 32;
    }

    private Label AddLabel(Panel panel, string text, int left, ref int top)
    {
        Label lbl = new Label { Text = text, Left = left, Top = top, Width = 390, ForeColor = Color.FromArgb(200, 200, 200) };
        panel.Controls.Add(lbl);
        top += 20;
        return lbl;
    }

    private void SettingsForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;
        if (e.KeyCode == Keys.Escape) { CancelRecording(); e.Handled = true; return; }
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;

        int vk = (int)e.KeyCode;
        if (_activeTargetTextBox != null) _activeTargetTextBox.Text = $"0x{vk:X}";

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
            if (m.Msg == 0x0201 || m.Msg == 0x0204) _onMouseClick();
            return false;
        }
    }

    private void SaveChanges(bool resetVolumes)
    {
        string s1 = _cmbSource1.SelectedItem?.ToString() ?? _cmbSource1.Text.Trim();
        string s2 = _cmbSource2.SelectedItem?.ToString() ?? _cmbSource2.Text.Trim();
        string mode = _cmbMode.SelectedItem?.ToString() ?? "Crossfade";

        if (mode == "Crossfade")
        {
            if (!string.IsNullOrEmpty(s1) && !string.IsNullOrEmpty(s2) && s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Source 1 and Source 2 cannot be the exact same program or group.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                MessageBox.Show("Crossfade mode requires both Source 1 and Source 2 to be selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(s1))
            {
                MessageBox.Show("Volume Control mode requires a source or group selection.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        int ParseHexKey(string text, int defaultVal)
        {
            try
            {
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt32(text, 16);
                return int.Parse(text);
            }
            catch { return defaultVal; }
        }

        int ParseInt(string text, int defaultVal) => int.TryParse(text, out int val) ? val : defaultVal;

        var newSettings = new AppSettings
        {
            OperationMode = mode,
            Source1 = s1,
            Source2 = s2,
            Groups = _currentSettings.Groups,
            RunOnStartup = _chkStartup.Checked,
            KeyUp = ParseHexKey(_txtKeyUp.Text, 0x7E),
            KeyMute = ParseHexKey(_txtKeyMute.Text, 0x7F),
            KeyDown = ParseHexKey(_txtKeyDown.Text, 0x80),
            UnmutedColor = _currentUnmutedHex,
            MutedColor = _currentMutedHex,
            OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center",
            PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000),
            StepSize = Math.Clamp(ParseInt(_txtStepSize.Text, 10), 1, 100),
            OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 340), 100, 2000),
            OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 75), 50, 1000),
            EnableInterpolation = _chkInterpolation.Checked,
            EnableEqualizationBuffer = _chkBuffer.Checked
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (newSettings.RunOnStartup) key?.SetValue("AudioCrossfader", $"\"{exePath}\"");
            else key?.DeleteValue("AudioCrossfader", false);
        }
        catch { }

        _context.SaveSettings(newSettings, resetVolumes);
        this.Close();
    }
}

// On-Screen Overlay Popup
class VolumeOverlayForm : Form
{
    private readonly string _app1Label;
    private readonly string _app2Label;
    private readonly AppSettings _settings;
    private readonly bool _isSingleSource;
    private readonly string _singleLabel;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private int _totalDurationMs = 2500;
    private bool _isFadingOut = false;

    public bool IsFadedOut => this.Opacity <= 0.0;

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = FormatLabel(app1);
        _app2Label = FormatLabel(app2);
        _settings = settings;

        _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));
        _singleLabel = string.IsNullOrEmpty(_app1Label) ? _app2Label : _app1Label;

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ClientSize = new Size(Math.Max(100, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

        _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int x = workingArea.Left + (workingArea.Width - this.Width) / 2;
        int y = workingArea.Bottom - this.Height - 65;

        switch (_settings.OverlayPosition)
        {
            case "Top Center":
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Top + 40; break;
            case "Bottom Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Bottom Left":
                x = workingArea.Left + 40; y = workingArea.Bottom - this.Height - 65; break;
            case "Top Right":
                x = workingArea.Right - this.Width - 40; y = workingArea.Top + 40; break;
            case "Top Left":
                x = workingArea.Left + 40; y = workingArea.Top + 40; break;
            case "Bottom Center":
            default:
                x = workingArea.Left + (workingArea.Width - this.Width) / 2; y = workingArea.Bottom - this.Height - 65; break;
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

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _fadeTimer.Tick += (s, e) =>
        {
            if (!_isFadingOut)
            {
                if (this.Opacity < 1.0) this.Opacity = Math.Min(1.0, this.Opacity + 0.15);
                if (_displayCountdownMs > 0)
                {
                    _displayCountdownMs -= 15;
                    if (_displayCountdownMs <= 0) _isFadingOut = true;
                }
            }
            else
            {
                if (this.Opacity > 0.0) this.Opacity = Math.Max(0.0, this.Opacity - 0.08);
                else { this.Opacity = 0.0; _fadeTimer.Stop(); }
            }
        };
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

    private string FormatLabel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("Group: ")) return raw.Substring(7).ToUpper();
        return raw.Replace(".exe", "").ToUpper();
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
                if (forceShow && _fadeTimer != null && !_fadeTimer.Enabled) _fadeTimer.Start();
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
        float scale = Math.Clamp((float)this.Height / 75f, 0.5f, 5.0f);
        float fontSize = Math.Clamp(13f * scale, 6.5f, 32f);

        if (_isSingleSource)
        {
            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF labelSize = g.MeasureString(_singleLabel, font);
                float textY = (this.Height - labelSize.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);
                g.DrawString(_singleLabel, font, Brushes.White, centerX - (labelSize.Width / 2f), textY);
            }

            int absoluteSideMargin = 30;
            int barWidth = this.Width - (absoluteSideMargin * 2);
            if (barWidth < 20) barWidth = 20;

            int barHeight = (int)(14 * scale);
            barHeight = Math.Clamp(barHeight, 8, 40);

            int barY = (int)(this.Height * 0.58f);
            barY = Math.Clamp(barY, 10, Math.Max(10, this.Height - barHeight - 6));

            Color unmutedColor = ColorTranslator.FromHtml(_settings.UnmutedColor);
            Color mutedColor = ColorTranslator.FromHtml(_settings.MutedColor);

            bool isMuted = (_muteState > 0);
            using Brush barColor = new SolidBrush(isMuted ? mutedColor : unmutedColor);
            using Brush trackBgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));

            using (GraphicsPath? trackPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, barWidth, barHeight, true, true))
            {
                if (trackPath != null) g.FillPath(trackBgBrush, trackPath);
            }

            int fillWidth = (int)(barWidth * _app1Vol);
            if (fillWidth > 0)
            {
                using (GraphicsPath? fillPath = GetSafeRoundedBarPath(absoluteSideMargin, barY, fillWidth, barHeight, true, true))
                {
                    if (fillPath != null) g.FillPath(barColor, fillPath);
                }
            }
        }
        else
        {
            int centerLineTop = (int)(10 * scale);
            int centerLineHeight = Math.Max(10, this.Height - (centerLineTop * 2));
            g.FillRectangle(Brushes.DimGray, centerX - 1, centerLineTop, 2, centerLineHeight);

            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF app1Size = g.MeasureString(_app1Label, font);
                float textGap = 12 * scale;
                float textY = (this.Height - app1Size.Height) / 2f - (8 * scale);
                textY = Math.Max(4, textY);

                g.DrawString(_app1Label, font, Brushes.White, centerX - textGap - app1Size.Width, textY);
                g.DrawString(_app2Label, font, Brushes.White, centerX + textGap, textY);
            }

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

            using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
            {
                if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
            }
            using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
            {
                if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
            }

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
        }

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

                using (Pen pen = new Pen(Color.FromArgb(70, 70, 70), 1)) g.DrawPath(pen, borderPath);
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
}*/

/*using System.Diagnostics;
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
        // Fix working directory when launched via Windows Startup registry key
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

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

    public void SaveSettings(AppSettings newSettings, bool resetVolumes = false)
    {
        _settings = newSettings;
        try
        {
            string json = JsonSerializer.Serialize(_settings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }

        _crossfaderWorker?.UpdateSettings(_settings, resetVolumes);
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
    public int StepSize { get; set; } = 10; // Step size percentage (1 - 100)
    public bool EnableEqualizationBuffer { get; set; } = true; // Toggle for 100% detent buffer
}

class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private AppSettings _settings;
    private float _targetPosition = 0.5f;       // The discrete stepped position for audio
    private float _visualPosition = 0.5f;     // The smoothly gliding position for the visual bar
    private int _muteState = 0;
    private int _detentClicks = 0;            // Tracks extra clicks at the 0.5 equalization detent
    private int _detentDirection = 0;         // Tracks the travel direction entering 0.5
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private bool _running = false;

    public AudioCrossfaderWorker(AppSettings settings)
    {
        _settings = settings;
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

    public void UpdateSettings(AppSettings newSettings, bool resetVolumes)
    {
        _settings = newSettings;
        if (resetVolumes)
        {
            _targetPosition = 0.5f;
            _visualPosition = 0.5f;
            _muteState = 0;
            ResetVolumesToFull();
        }

        // Restart worker UI labels cleanly
        Stop();
        Start();
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

            // Reset detent state when the popup window has fully faded out
            if (_overlay != null && _overlay.IsFadedOut)
            {
                _detentClicks = 0;
                _detentDirection = 0;
            }

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

            // User step size divided by 2 for ultra-fine increment granularity
            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;

            if (upSteps > 0)
            {
                Step(stepIncrement * upSteps);
                physicalInputRegistered = true;
            }
            if (downSteps > 0)
            {
                Step(-stepIncrement * downSteps);
                physicalInputRegistered = true;
            }

            // Handle smooth visual interpolation independently from the audio steps
            if (_settings.EnableInterpolation)
            {
                float diff = _targetPosition - _visualPosition;
                if (Math.Abs(diff) > 0.0005f)
                {
                    _visualPosition += diff * 0.45f;
                }
                else
                {
                    _visualPosition = _targetPosition;
                }
            }
            else
            {
                _visualPosition = _targetPosition;
            }

            if (_visualPosition <= 0.002f) _visualPosition = 0.0f;
            if (_visualPosition >= 0.998f) _visualPosition = 1.0f;

            // Update UI/Overlay every frame using the smooth visual position
            ApplyVolumes(false, physicalInputRegistered);

            // Throttle Windows Audio API session updates to immediate discrete steps
            var now = DateTime.Now;
            if (physicalInputRegistered && (now - lastAudioApplyTime).TotalMilliseconds >= 15)
            {
                ApplyVolumes(true, false);
                lastAudioApplyTime = now;
            }

            Thread.Sleep(2);
        }
    }

    public void Step(float delta)
    {
        // If the equalization buffer toggle is disabled, step normally without any detent resistance
        if (!_settings.EnableEqualizationBuffer)
        {
            float newTarget = _targetPosition + delta;
            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;
            if (newTarget < stepIncrement * 0.8f) newTarget = 0.0f;
            else if (newTarget > 1.0f - (stepIncrement * 0.8f)) newTarget = 1.0f;
            _targetPosition = Math.Clamp(newTarget, 0.0f, 1.0f);
            return;
        }

        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            bool isFromRest = (_overlay == null || _overlay.IsFadedOut);
            int currentDeltaSign = Math.Sign(delta);

            if (isFromRest || (currentDeltaSign != 0 && currentDeltaSign != _detentDirection))
            {
                float newTarget = _targetPosition + delta;
                _targetPosition = Math.Clamp(newTarget, 0.0f, 1.0f);
                _detentClicks = 0;
                _detentDirection = currentDeltaSign;
            }
            else
            {
                _detentClicks++;
                if (_detentClicks < 3)
                {
                    return;
                }
                else
                {
                    _detentClicks = 0;
                    float newTarget = _targetPosition + delta;
                    _targetPosition = Math.Clamp(newTarget, 0.0f, 1.0f);
                    _detentDirection = currentDeltaSign;
                }
            }
        }
        else
        {
            float newTarget = _targetPosition + delta;

            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;
            if (newTarget < stepIncrement * 0.8f)
            {
                newTarget = 0.0f;
            }
            else if (newTarget > 1.0f - (stepIncrement * 0.8f))
            {
                newTarget = 1.0f;
            }

            bool crossingCenter = (_targetPosition < 0.5f && newTarget >= 0.5f) || (_targetPosition > 0.5f && newTarget <= 0.5f);
            if (crossingCenter)
            {
                _targetPosition = 0.5f;
                _detentClicks = 0;
                _detentDirection = Math.Sign(delta);
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
        // Calculate audio volumes using the discrete stepped target position
        float audioApp1Vol;
        float audioApp2Vol;

        if (_targetPosition >= 0.5f)
        {
            audioApp1Vol = 1.0f;
            audioApp2Vol = 1.0f - ((_targetPosition - 0.5f) * 2.0f);
        }
        else
        {
            audioApp2Vol = 1.0f;
            audioApp1Vol = _targetPosition * 2.0f;
        }

        audioApp1Vol = Math.Clamp(audioApp1Vol, 0.0f, 1.0f);
        audioApp2Vol = Math.Clamp(audioApp2Vol, 0.0f, 1.0f);

        if (audioApp1Vol <= 0.01f) audioApp1Vol = 0.0f;
        if (audioApp1Vol >= 0.99f) audioApp1Vol = 1.0f;
        if (audioApp2Vol <= 0.01f) audioApp2Vol = 0.0f;
        if (audioApp2Vol >= 0.99f) audioApp2Vol = 1.0f;

        float actualApp1Vol = audioApp1Vol;
        float actualApp2Vol = audioApp2Vol;

        if (_muteState == 1) actualApp2Vol = 0.0f;
        else if (_muteState == 2) actualApp1Vol = 0.0f;

        if (updateAudioSessions)
        {
            SetApplicationVolume(_settings.Source1, actualApp1Vol);
            SetApplicationVolume(_settings.Source2, actualApp2Vol);
        }

        // Calculate visual volumes using the smooth gliding visual position
        float visualApp1Vol;
        float visualApp2Vol;

        if (_visualPosition >= 0.5f)
        {
            visualApp1Vol = 1.0f;
            visualApp2Vol = 1.0f - ((_visualPosition - 0.5f) * 2.0f);
        }
        else
        {
            visualApp2Vol = 1.0f;
            visualApp1Vol = _visualPosition * 2.0f;
        }

        visualApp1Vol = Math.Clamp(visualApp1Vol, 0.0f, 1.0f);
        visualApp2Vol = Math.Clamp(visualApp2Vol, 0.0f, 1.0f);

        _overlay?.UpdateVolumes(visualApp1Vol, visualApp2Vol, _muteState, forceShowPopup);
    }

    private void SetApplicationVolume(string targetApp, float volume)
    {
        if (string.IsNullOrWhiteSpace(targetApp)) return;

        try
        {
            string targetKey = targetApp.ToLower().Replace(".exe", "").Trim();
            if (string.IsNullOrEmpty(targetKey)) return;

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
                            if (proc.ProcessName.ToLower().Equals(targetKey))
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
    private TextBox _txtStepSize;
    private TextBox _txtWidth;
    private TextBox _txtHeight;
    private CheckBox _chkStartup;
    private CheckBox _chkInterpolation;
    private CheckBox _chkBuffer;
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
        this.Size = new Size(380, 790);
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

        AddLabel("Step Size (%, 1 - 100):", 20, ref y);
        _txtStepSize = AddTextBox(settings.StepSize.ToString(), 20, ref y);

        AddLabel("Popup Duration (ms, 500 - 10000):", 20, ref y);
        _txtPopupDuration = AddTextBox(settings.PopupDurationMs.ToString(), 20, ref y);

        AddLabel("Overlay Size (Width: 100-2000, Height: 50-1000):", 20, ref y);
        _txtWidth = AddNumericRow("Width:", settings.OverlayWidth.ToString(), 20, ref y);
        _txtHeight = AddNumericRow("Height:", settings.OverlayHeight.ToString(), 20, ref y);

        _chkInterpolation = new CheckBox { Text = "Smooth Volume Bar Interpolation", Checked = settings.EnableInterpolation, Left = 20, Top = y, Width = 320, ForeColor = Color.White };
        Controls.Add(_chkInterpolation);
        y += 30;

        _chkBuffer = new CheckBox { Text = "Enable 100% Equalization Buffer", Checked = settings.EnableEqualizationBuffer, Left = 20, Top = y, Width = 320, ForeColor = Color.White };
        Controls.Add(_chkBuffer);
        y += 30;

        _chkStartup = new CheckBox { Text = "Run on Windows Startup", Checked = settings.RunOnStartup, Left = 20, Top = y, Width = 320, ForeColor = Color.White };
        Controls.Add(_chkStartup);

        y += 35;

        Button btnResetVol = new Button 
        { 
            Text = "Reset Volumes to 100%", 
            Left = 20, 
            Top = y, 
            Width = 180, 
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnResetVol.FlatAppearance.BorderSize = 0;
        btnResetVol.Click += (s, e) => SaveChanges(true);
        Controls.Add(btnResetVol);

        Button btnSave = new Button 
        { 
            Text = "Save & Apply", 
            Left = 210, 
            Top = y, 
            Width = 130, 
            Height = 30,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => SaveChanges(false);
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

    private void SaveChanges(bool resetVolumes)
    {
        string s1 = _txtSource1.Text.Trim();
        string s2 = _txtSource2.Text.Trim();

        if (!string.IsNullOrEmpty(s1) && !string.IsNullOrEmpty(s2) && s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Source 1 and Source 2 cannot be the exact same program.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
            Source1 = s1,
            Source2 = s2,
            RunOnStartup = _chkStartup.Checked,
            KeyUp = ParseHexKey(_txtKeyUp.Text, 0x7E),
            KeyMute = ParseHexKey(_txtKeyMute.Text, 0x7F),
            KeyDown = ParseHexKey(_txtKeyDown.Text, 0x80),
            UnmutedColor = _currentUnmutedHex,
            MutedColor = _currentMutedHex,
            OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center",
            PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000),
            StepSize = Math.Clamp(ParseInt(_txtStepSize.Text, 10), 1, 100),
            OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 340), 100, 2000),
            OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 75), 50, 1000),
            EnableInterpolation = _chkInterpolation.Checked,
            EnableEqualizationBuffer = _chkBuffer.Checked
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

        _context.SaveSettings(newSettings, resetVolumes);
        this.Close();
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

    public bool IsFadedOut => this.Opacity <= 0.0;

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = string.IsNullOrEmpty(app1) ? "NONE" : app1.Replace(".exe", "").ToUpper();
        _app2Label = string.IsNullOrEmpty(app2) ? "NONE" : app2.Replace(".exe", "").ToUpper();
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

        // Draw track backgrounds safely handling nullable paths
        using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
        {
            if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
        }
        using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
        {
            if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
        }

        // Draw volume fill bars safely handling nullable paths
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
}*/
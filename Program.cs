using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

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

public class ProgramGroup
{
    public string GroupName { get; set; } = "";
    public bool IsFavorite { get; set; } = false;
    public List<string> Executables { get; set; } = new List<string>();

    public ProgramGroup Clone()
    {
        return new ProgramGroup
        {
            GroupName = this.GroupName,
            IsFavorite = this.IsFavorite,
            Executables = new List<string>(this.Executables)
        };
    }
}

public class VolumeControlSlot
{
    public string Source { get; set; } = "";
    public int KeyUp { get; set; } = 0;
    public int KeyDown { get; set; } = 0;
    public int KeyMute { get; set; } = 0;

    public VolumeControlSlot Clone()
    {
        return new VolumeControlSlot
        {
            Source = this.Source,
            KeyUp = this.KeyUp,
            KeyDown = this.KeyDown,
            KeyMute = this.KeyMute
        };
    }
}

// Extracts and caches 32x32 application icons and dedicated group icons in memory
public static class AppIconCache
{
    private static readonly ConcurrentDictionary<string, Bitmap?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _knownExePaths = new(StringComparer.OrdinalIgnoreCase);
    private static Bitmap? _groupBadgeBitmap;

    public static void RegisterKnownPath(string exeName, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(exeName) || string.IsNullOrWhiteSpace(fullPath)) return;
        string normalized = AppSettings.NormalizeExeName(exeName);
        if (!string.IsNullOrEmpty(normalized) && File.Exists(fullPath))
        {
            _knownExePaths[normalized] = fullPath;
        }
    }

    public static void LoadPathsFromSettings(AppSettings settings)
    {
        foreach (var kvp in settings.KnownExecutablePaths)
        {
            RegisterKnownPath(kvp.Key, kvp.Value);
        }
    }

    public static Bitmap? GetIconForSource(string sourceIdentifier, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(sourceIdentifier)) return null;

        if (sourceIdentifier.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
        {
            return GetDefaultGroupBitmap();
        }

        return GetIconForExecutable(sourceIdentifier);
    }

    public static Bitmap? GetIconForExecutable(string exeName)
    {
        string norm = AppSettings.NormalizeExeName(exeName);
        if (string.IsNullOrEmpty(norm)) return null;

        return _iconCache.GetOrAdd(norm, key =>
        {
            string? path = ResolveExecutablePath(key);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using Icon? ico = Icon.ExtractAssociatedIcon(path);
                    if (ico != null)
                    {
                        return new Bitmap(ico.ToBitmap(), new Size(32, 32));
                    }
                }
                catch { }
            }
            return CreateFallbackMonogramIcon(key);
        });
    }

    private static string? ResolveExecutablePath(string normalizedExeName)
    {
        if (_knownExePaths.TryGetValue(normalizedExeName, out string? cachedPath) && File.Exists(cachedPath))
            return cachedPath;

        string procKey = AppSettings.NormalizeProcessKey(normalizedExeName);
        try
        {
            var procs = Process.GetProcessesByName(procKey);
            foreach (var p in procs)
            {
                try
                {
                    string? fileName = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                    {
                        _knownExePaths[normalizedExeName] = fileName;
                        return fileName;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        try
        {
            using var regKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{normalizedExeName}");
            string? regPath = regKey?.GetValue(null)?.ToString()?.Trim('"');
            if (!string.IsNullOrEmpty(regPath) && File.Exists(regPath))
            {
                _knownExePaths[normalizedExeName] = regPath;
                return regPath;
            }
        }
        catch { }

        try
        {
            using var regKey = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{normalizedExeName}");
            string? regPath = regKey?.GetValue(null)?.ToString()?.Trim('"');
            if (!string.IsNullOrEmpty(regPath) && File.Exists(regPath))
            {
                _knownExePaths[normalizedExeName] = regPath;
                return regPath;
            }
        }
        catch { }

        return null;
    }

    private static Bitmap CreateFallbackMonogramIcon(string exeName)
    {
        Bitmap bmp = new Bitmap(32, 32);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = new GraphicsPath();
        path.AddEllipse(2, 2, 28, 28);
        using SolidBrush bg = new SolidBrush(Color.FromArgb(0, 110, 195));
        g.FillPath(bg, path);

        string letter = string.IsNullOrEmpty(exeName) ? "?" : exeName.Substring(0, 1).ToUpperInvariant();
        using Font f = new Font("Segoe UI", 13f, FontStyle.Bold);
        using StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter, f, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        return bmp;
    }

    private static Bitmap GetDefaultGroupBitmap()
    {
        if (_groupBadgeBitmap != null) return _groupBadgeBitmap;
        Bitmap bmp = new Bitmap(32, 32);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath badgePath = new GraphicsPath())
        {
            int r = 10;
            badgePath.AddArc(2, 2, r, r, 180, 90);
            badgePath.AddArc(30 - r, 2, r, r, 270, 90);
            badgePath.AddArc(30 - r, 30 - r, r, r, 0, 90);
            badgePath.AddArc(2, 30 - r, r, r, 90, 90);
            badgePath.CloseFigure();

            using LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(2, 2, 28, 28),
                Color.FromArgb(155, 105, 245),
                Color.FromArgb(105, 65, 195),
                LinearGradientMode.ForwardDiagonal);
            g.FillPath(bg, badgePath);
        }

        using (SolidBrush backCardBrush = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
        {
            g.FillRectangle(backCardBrush, 12, 7, 12, 10);
        }

        using (SolidBrush frontCardBrush = new SolidBrush(Color.White))
        using (Pen separatorPen = new Pen(Color.FromArgb(115, 75, 205), 1.5f))
        {
            Rectangle frontRect = new Rectangle(7, 13, 14, 11);
            g.FillRectangle(frontCardBrush, frontRect);
            g.DrawRectangle(separatorPen, frontRect);
        }

        _groupBadgeBitmap = bmp;
        return bmp;
    }
}

public class DetectedAudioPrograms
{
    public HashSet<string> PlayingAudio { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ActiveAudioSessions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RunningWindowedApps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public static class AudioProcessDetector
{
    private static readonly HashSet<string> IgnoredSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "searchhost.exe", "startmenuexperiencehost.exe",
        "textinputhost.exe", "applicationframehost.exe", "systemsettings.exe", "taskmgr.exe",
        "svchost.exe", "dwm.exe", "audiodg.exe"
    };

    public static DetectedAudioPrograms Scan()
    {
        var result = new DetectedAudioPrograms();
        string currentProcName = AppSettings.NormalizeExeName(Process.GetCurrentProcess().ProcessName);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid == 0) continue;

                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            string exeName = AppSettings.NormalizeExeName(proc.ProcessName);
                            if (string.IsNullOrEmpty(exeName) || exeName.Equals(currentProcName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            try
                            {
                                AppIconCache.RegisterKnownPath(exeName, proc.MainModule?.FileName);
                            }
                            catch { }

                            bool isPlaying = false;
                            try
                            {
                                float peak = session.AudioMeterInformation.MasterPeakValue;
                                isPlaying = (peak > 0.0005f) || (session.State == AudioSessionState.AudioSessionStateActive);
                            }
                            catch
                            {
                                isPlaying = (session.State == AudioSessionState.AudioSessionStateActive);
                            }

                            if (isPlaying)
                                result.PlayingAudio.Add(exeName);
                            else
                                result.ActiveAudioSessions.Add(exeName);
                        }
                        catch { }
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch { }

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                    {
                        string exeName = AppSettings.NormalizeExeName(proc.ProcessName);
                        if (!string.IsNullOrEmpty(exeName) &&
                            !exeName.Equals(currentProcName, StringComparison.OrdinalIgnoreCase) &&
                            !IgnoredSystemProcesses.Contains(exeName))
                        {
                            try { AppIconCache.RegisterKnownPath(exeName, proc.MainModule?.FileName); } catch { }

                            if (!result.PlayingAudio.Contains(exeName) && !result.ActiveAudioSessions.Contains(exeName))
                            {
                                result.RunningWindowedApps.Add(exeName);
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }

        return result;
    }
}

public static class KeybindHelper
{
    public const int VkMask = 0x0000FFFF;

    public static int CreateBinding(Keys keyCode, Keys modifiers)
    {
        return (int)(keyCode & Keys.KeyCode) | (int)(modifiers & Keys.Modifiers);
    }

    public static int GetPrimaryVk(int binding) => binding & VkMask;

    public static bool HasCtrl(int binding) => ((Keys)binding & Keys.Control) == Keys.Control;
    public static bool HasAlt(int binding) => ((Keys)binding & Keys.Alt) == Keys.Alt;
    public static bool HasShift(int binding) => ((Keys)binding & Keys.Shift) == Keys.Shift;

    public static string FormatKeybind(int binding)
    {
        int vk = GetPrimaryVk(binding);
        if (vk == 0) return "None";

        List<string> parts = new List<string>();
        if (HasCtrl(binding)) parts.Add("Ctrl");
        if (HasAlt(binding)) parts.Add("Alt");
        if (HasShift(binding)) parts.Add("Shift");

        Keys key = (Keys)vk;
        string keyLabel = key switch
        {
            Keys.Up => "Up Arrow",
            Keys.Down => "Down Arrow",
            Keys.Left => "Left Arrow",
            Keys.Right => "Right Arrow",
            Keys.Space => "Space",
            Keys.Return => "Enter",
            Keys.Back => "Backspace",
            Keys.Tab => "Tab",
            Keys.Escape => "Escape",
            Keys.Prior => "Page Up",
            Keys.Next => "Page Down",
            Keys.Oemplus => "Plus (+)",
            Keys.OemMinus => "Minus (-)",
            _ => key.ToString()
        };

        parts.Add(keyLabel);
        return string.Join(" + ", parts);
    }

    public static string FormatLiveModifiers(Keys modifiers)
    {
        List<string> parts = new List<string>();
        if ((modifiers & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((modifiers & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        if ((modifiers & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if (parts.Count == 0) return "Press keys...";
        return string.Join(" + ", parts) + " + ...";
    }
}

public class AppSettings
{
    public const int MaxHistoryCount = 10;

    public string OperationMode { get; set; } = "Crossfade"; // "Crossfade" or "Volume Control"
    public string Source1 { get; set; } = "chrome.exe";
    public string Source2 { get; set; } = "firefox.exe";
    public List<ProgramGroup> Groups { get; set; } = new List<ProgramGroup>();
    public List<VolumeControlSlot> AdditionalVolumeSlots { get; set; } = new List<VolumeControlSlot>();
    public List<string> RecentPrograms { get; set; } = new List<string> { "chrome.exe", "firefox.exe" };
    public Dictionary<string, string> KnownExecutablePaths { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool RunOnStartup { get; set; } = false;

    // Crossfade Keybinds
    public int CrossfadeKeyUp { get; set; } = 0x7E;    // VK_F15
    public int CrossfadeKeyMute { get; set; } = 0x7F;  // VK_F16
    public int CrossfadeKeyDown { get; set; } = 0x80;  // VK_F17

    // Primary Volume Control Keybinds
    public int VolKeyUp { get; set; } = 0x26;          // Up Arrow
    public int VolKeyMute { get; set; } = 0x20;        // Space
    public int VolKeyDown { get; set; } = 0x28;        // Down Arrow

    // Quick-Swap / Cycle Favorite Group Keybind (0 = unbound)
    public int QuickSwapKey { get; set; } = 0;

    // Personalization & Display Modes
    public string UnmutedColor { get; set; } = "#00A8FF";
    public string MutedColor { get; set; } = "#FF4757";
    public string OverlayPosition { get; set; } = "Bottom Center";
    public int PopupDurationMs { get; set; } = 2500;

    public int OverlayWidth { get; set; } = 360;
    public int OverlayHeight { get; set; } = 80;
    public bool EnableInterpolation { get; set; } = true;
    public int InterpolationSpeed { get; set; } = 45;  // 5 (slow/smooth) to 100 (instant)
    public int StepSize { get; set; } = 10;
    public bool EnableEqualizationBuffer { get; set; } = true;

    public string OverlayIdentityMode { get; set; } = "Both"; // "Name", "Icon", "Both"
    public string OverlayLevelMode { get; set; } = "Both";    // "Bar", "Percentage", "Both"
    public bool ShowIconsInSettings { get; set; } = true;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            OperationMode = this.OperationMode,
            Source1 = this.Source1,
            Source2 = this.Source2,
            Groups = this.Groups.Select(g => g.Clone()).ToList(),
            AdditionalVolumeSlots = this.AdditionalVolumeSlots.Select(s => s.Clone()).ToList(),
            RecentPrograms = new List<string>(this.RecentPrograms),
            KnownExecutablePaths = new Dictionary<string, string>(this.KnownExecutablePaths, StringComparer.OrdinalIgnoreCase),
            RunOnStartup = this.RunOnStartup,
            CrossfadeKeyUp = this.CrossfadeKeyUp,
            CrossfadeKeyMute = this.CrossfadeKeyMute,
            CrossfadeKeyDown = this.CrossfadeKeyDown,
            VolKeyUp = this.VolKeyUp,
            VolKeyMute = this.VolKeyMute,
            VolKeyDown = this.VolKeyDown,
            QuickSwapKey = this.QuickSwapKey,
            UnmutedColor = this.UnmutedColor,
            MutedColor = this.MutedColor,
            OverlayPosition = this.OverlayPosition,
            PopupDurationMs = this.PopupDurationMs,
            OverlayWidth = this.OverlayWidth,
            OverlayHeight = this.OverlayHeight,
            EnableInterpolation = this.EnableInterpolation,
            InterpolationSpeed = this.InterpolationSpeed,
            StepSize = this.StepSize,
            EnableEqualizationBuffer = this.EnableEqualizationBuffer,
            OverlayIdentityMode = this.OverlayIdentityMode,
            OverlayLevelMode = this.OverlayLevelMode,
            ShowIconsInSettings = this.ShowIconsInSettings
        };
    }

    public static string NormalizeExeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase)) return trimmed;

        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            trimmed = Path.GetFileName(trimmed);
        }

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToLowerInvariant()
            : $"{trimmed.ToLowerInvariant()}.exe";
    }

    public static string NormalizeProcessKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = NormalizeExeName(raw);
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
        }
        return trimmed.Trim();
    }

    public void RecordProgramHistory(string? candidate, string? fullPath = null)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        string trimmed = candidate.Trim();
        if (trimmed.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase)) return;

        string normalized = NormalizeExeName(trimmed);
        if (string.IsNullOrEmpty(normalized)) return;

        if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
        {
            KnownExecutablePaths[normalized] = fullPath;
            AppIconCache.RegisterKnownPath(normalized, fullPath);
        }

        RecentPrograms.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        RecentPrograms.Insert(0, normalized);

        if (RecentPrograms.Count > MaxHistoryCount)
        {
            RecentPrograms.RemoveRange(MaxHistoryCount, RecentPrograms.Count - MaxHistoryCount);
        }
    }

    public List<string> ResolveExecutables(string targetIdentifier)
    {
        List<string> results = new List<string>();
        if (string.IsNullOrWhiteSpace(targetIdentifier)) return results;

        if (targetIdentifier.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
        {
            string groupName = targetIdentifier.Substring(7).Trim();
            var group = Groups.FirstOrDefault(g => g.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            if (group != null)
            {
                foreach (var exe in group.Executables)
                {
                    string norm = NormalizeExeName(exe);
                    if (!string.IsNullOrEmpty(norm)) results.Add(norm);
                }
            }
        }
        else
        {
            string norm = NormalizeExeName(targetIdentifier);
            if (!string.IsNullOrEmpty(norm)) results.Add(norm);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> GetOverlappingExecutables(string source1, string source2)
    {
        var list1 = ResolveExecutables(source1);
        var list2 = ResolveExecutables(source2);

        var set1Keys = new HashSet<string>(list1.Select(NormalizeProcessKey), StringComparer.OrdinalIgnoreCase);

        return list2
            .Where(exe => set1Keys.Contains(NormalizeProcessKey(exe)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// High-Performance CoreAudio Session Cache
public class AudioSessionCache : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new MMDeviceEnumerator();
    private readonly Dictionary<string, List<SimpleAudioVolume>> _sessionsByProcKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _pidToProcKey = new();
    private readonly List<MMDevice> _activeDevices = new();
    private readonly List<AudioSessionControl> _activeSessions = new();
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private DateTime _lastMissingKeyProbeTime = DateTime.MinValue;
    private const double RefreshIntervalMs = 2000.0;

    public void SetVolumeForProcessKey(string procKey, float volume)
    {
        if (string.IsNullOrEmpty(procKey)) return;

        var now = DateTime.UtcNow;
        bool regularRefreshDue = (now - _lastRefreshTime).TotalMilliseconds >= RefreshIntervalMs;
        bool missingKeyProbeDue = !_sessionsByProcKey.ContainsKey(procKey) && (now - _lastMissingKeyProbeTime).TotalMilliseconds >= 350.0;

        if (regularRefreshDue || missingKeyProbeDue)
        {
            _lastMissingKeyProbeTime = now;
            RefreshSessions();
        }

        if (_sessionsByProcKey.TryGetValue(procKey, out var volumes))
        {
            bool faulted = false;
            for (int i = 0; i < volumes.Count; i++)
            {
                try
                {
                    volumes[i].Volume = volume;
                }
                catch
                {
                    faulted = true;
                }
            }

            if (faulted)
            {
                RefreshSessions();
                if (_sessionsByProcKey.TryGetValue(procKey, out var retryVolumes))
                {
                    for (int i = 0; i < retryVolumes.Count; i++)
                    {
                        try { retryVolumes[i].Volume = volume; } catch { }
                    }
                }
            }
        }
    }

    public void ForceRefresh() => RefreshSessions();

    private void RefreshSessions()
    {
        _lastRefreshTime = DateTime.UtcNow;
        _sessionsByProcKey.Clear();
        _pidToProcKey.Clear();
        _activeSessions.Clear();

        foreach (var oldDev in _activeDevices)
        {
            try { oldDev.Dispose(); } catch { }
        }
        _activeDevices.Clear();

        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                _activeDevices.Add(device);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    uint pid = session.GetProcessID;
                    if (pid == 0) continue;

                    if (!_pidToProcKey.TryGetValue(pid, out string? procKey))
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            procKey = proc.ProcessName.ToLowerInvariant().Trim();
                            _pidToProcKey[pid] = procKey;
                            try
                            {
                                AppIconCache.RegisterKnownPath($"{procKey}.exe", proc.MainModule?.FileName);
                            }
                            catch { }
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(procKey))
                    {
                        if (!_sessionsByProcKey.TryGetValue(procKey, out var list))
                        {
                            list = new List<SimpleAudioVolume>();
                            _sessionsByProcKey[procKey] = list;
                        }
                        _activeSessions.Add(session);
                        list.Add(session.SimpleAudioVolume);
                    }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _activeSessions.Clear();
        foreach (var dev in _activeDevices)
        {
            try { dev.Dispose(); } catch { }
        }
        _activeDevices.Clear();
        try { _enumerator.Dispose(); } catch { }
    }
}

public class AudioCrossfaderWorker
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    // Guarantees 1 step per physical key press or rotary encoder notch
    private struct KeyPressTracker
    {
        public int PollSteps(int vk, bool modifiersMatch)
        {
            if (vk == 0 || !modifiersMatch) return 0;

            int steps = 0;
            while ((GetAsyncKeyState(vk) & 0x0001) != 0)
            {
                steps++;
            }
            return steps;
        }
    }

    private class RuntimeVolChannel
    {
        public string Source = "";
        public int KeyUp;
        public int KeyDown;
        public int KeyMute;
        public float TargetVol = 1.0f;
        public float VisualVol = 1.0f;
        public bool IsMuted = false;
        public bool LastMuteDown = false;
        public KeyPressTracker UpTracker;
        public KeyPressTracker DownTracker;
    }

    private AppSettings _settings;
    private readonly Action<AppSettings>? _onRuntimeSettingsMutated;
    private float _targetPosition = 0.5f;
    private float _visualPosition = 0.5f;
    private int _muteState = 0;
    private int _detentClicks = 0;
    private int _detentDirection = 0;

    private List<RuntimeVolChannel> _volChannels = new();
    private int _activeVolChannelIndex = 0;

    private VolumeOverlayForm? _overlay;
    private Thread? _workerThread;
    private CancellationTokenSource? _cts;
    private readonly object _stateLock = new();
    private volatile bool _forceResetAudioFlag = false;

    public AudioCrossfaderWorker(AppSettings settings, Action<AppSettings>? onRuntimeSettingsMutated = null)
    {
        _settings = settings.Clone();
        _onRuntimeSettingsMutated = onRuntimeSettingsMutated;
        AppIconCache.LoadPathsFromSettings(_settings);
        RebuildVolumeChannels();
    }

    private void RebuildVolumeChannels()
    {
        var newList = new List<RuntimeVolChannel>
        {
            new RuntimeVolChannel
            {
                Source = _settings.Source1,
                KeyUp = _settings.VolKeyUp,
                KeyDown = _settings.VolKeyDown,
                KeyMute = _settings.VolKeyMute,
                TargetVol = 1.0f,
                VisualVol = 1.0f
            }
        };

        foreach (var slot in _settings.AdditionalVolumeSlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Source))
            {
                newList.Add(new RuntimeVolChannel
                {
                    Source = slot.Source,
                    KeyUp = slot.KeyUp,
                    KeyDown = slot.KeyDown,
                    KeyMute = slot.KeyMute,
                    TargetVol = 1.0f,
                    VisualVol = 1.0f
                });
            }
        }

        _volChannels = newList;
        _activeVolChannelIndex = 0;
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _workerThread = new Thread(() => RunLoop(token));
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.IsBackground = true;
        _workerThread.Start();
    }

    public void Stop()
    {
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch { }
        }

        if (_workerThread != null && _workerThread.IsAlive)
        {
            try { _workerThread.Join(1500); } catch { }
        }
        _workerThread = null;

        try
        {
            if (_overlay != null && !_overlay.IsDisposed && _overlay.IsHandleCreated)
            {
                _overlay.Invoke(new Action(() => _overlay.Close()));
            }
        }
        catch { }
        _overlay = null;

        if (_cts != null)
        {
            try { _cts.Dispose(); } catch { }
            _cts = null;
        }
    }

    public void UpdateSettings(AppSettings newSettings, bool resetVolumes)
    {
        // 1. Stop and wait for the old worker thread to finish cleanup on old sources first
        Stop();

        // 2. Apply new settings and reset mute state so the mute cycle always starts cleanly
        lock (_stateLock)
        {
            _settings = newSettings.Clone();
            AppIconCache.LoadPathsFromSettings(_settings);
            RebuildVolumeChannels();
            _muteState = 0;

            if (resetVolumes)
            {
                _targetPosition = 0.5f;
                _visualPosition = 0.5f;
            }
        }

        // 3. Start a single clean worker thread
        Start();
    }

    public void ResetVolumesToFullAndCenter()
    {
        lock (_stateLock)
        {
            _targetPosition = 0.5f;
            _visualPosition = 0.5f;
            _muteState = 0;
            foreach (var ch in _volChannels)
            {
                ch.TargetVol = 1.0f;
                ch.VisualVol = 1.0f;
                ch.IsMuted = false;
            }
        }
        _forceResetAudioFlag = true;
    }

    private static bool ModifiersMatch(int binding, bool ctrlDown, bool altDown, bool shiftDown)
    {
        return KeybindHelper.HasCtrl(binding) == ctrlDown &&
               KeybindHelper.HasAlt(binding) == altDown &&
               KeybindHelper.HasShift(binding) == shiftDown;
    }

    private static string GetResolvedLabel(string sourceTarget)
    {
        if (string.IsNullOrEmpty(sourceTarget)) return "";
        if (sourceTarget.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
        {
            return sourceTarget.Substring(7).ToUpperInvariant();
        }
        return sourceTarget.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    }

    private void ResetAllConfiguredExecutables(AudioSessionCache sessionCache)
    {
        sessionCache.ForceRefresh();
        foreach (var exe in _settings.ResolveExecutables(_settings.Source1))
            sessionCache.SetVolumeForProcessKey(AppSettings.NormalizeProcessKey(exe), 1.0f);
        foreach (var exe in _settings.ResolveExecutables(_settings.Source2))
            sessionCache.SetVolumeForProcessKey(AppSettings.NormalizeProcessKey(exe), 1.0f);
        foreach (var slot in _settings.AdditionalVolumeSlots)
        {
            foreach (var exe in _settings.ResolveExecutables(slot.Source))
                sessionCache.SetVolumeForProcessKey(AppSettings.NormalizeProcessKey(exe), 1.0f);
        }
    }

    private void PerformQuickSwapOrCycle(AudioSessionCache sessionCache)
    {
        lock (_stateLock)
        {
            ResetAllConfiguredExecutables(sessionCache);

            var favGroups = _settings.Groups
                .Where(g => g.IsFavorite && !string.IsNullOrWhiteSpace(g.GroupName))
                .Select(g => $"Group: {g.GroupName}")
                .ToList();

            bool isVolMode = (_settings.OperationMode == "Volume Control");

            if (isVolMode)
            {
                if (favGroups.Count > 0 && _volChannels.Count > 0)
                {
                    string current = _volChannels[0].Source;
                    int idx = favGroups.FindIndex(g => g.Equals(current, StringComparison.OrdinalIgnoreCase));
                    string nextFav = favGroups[(idx + 1) % favGroups.Count];
                    _volChannels[0].Source = nextFav;
                    _settings.Source1 = nextFav;
                    _activeVolChannelIndex = 0;
                    _onRuntimeSettingsMutated?.Invoke(_settings.Clone());
                }
                else if (_volChannels.Count > 1)
                {
                    _activeVolChannelIndex = (_activeVolChannelIndex + 1) % _volChannels.Count;
                }
            }
            else
            {
                if (favGroups.Count >= 2)
                {
                    int idx = favGroups.FindIndex(g => g.Equals(_settings.Source1, StringComparison.OrdinalIgnoreCase));
                    for (int offset = 1; offset <= favGroups.Count; offset++)
                    {
                        string candidate = favGroups[(idx + offset + favGroups.Count) % favGroups.Count];
                        if (!candidate.Equals(_settings.Source2, StringComparison.OrdinalIgnoreCase) &&
                            _settings.GetOverlappingExecutables(candidate, _settings.Source2).Count == 0)
                        {
                            _settings.Source1 = candidate;
                            break;
                        }
                    }
                    _onRuntimeSettingsMutated?.Invoke(_settings.Clone());
                }
                else if (favGroups.Count == 1)
                {
                    string fav = favGroups[0];
                    if (!_settings.Source1.Equals(fav, StringComparison.OrdinalIgnoreCase) &&
                        !_settings.Source2.Equals(fav, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.Source1 = fav;
                    }
                    else
                    {
                        (_settings.Source1, _settings.Source2) = (_settings.Source2, _settings.Source1);
                    }
                    _onRuntimeSettingsMutated?.Invoke(_settings.Clone());
                }
                else
                {
                    (_settings.Source1, _settings.Source2) = (_settings.Source2, _settings.Source1);
                    _onRuntimeSettingsMutated?.Invoke(_settings.Clone());
                }
            }
        }
    }

    private void RunLoop(CancellationToken token)
    {
        using AudioSessionCache sessionCache = new AudioSessionCache();
        sessionCache.ForceRefresh();

        AppSettings initialOverlaySettings = _settings.Clone();
        string initLabel1 = GetResolvedLabel(initialOverlaySettings.Source1);
        string initLabel2 = GetResolvedLabel(initialOverlaySettings.Source2);

        Thread uiThread = new Thread(() =>
        {
            _overlay = new VolumeOverlayForm(initLabel1, initLabel2, initialOverlaySettings);
            Application.Run(_overlay);
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Start();

        Thread.Sleep(150);
        ApplyVolumes(sessionCache, true, true);

        DateTime lastAudioApplyTime = DateTime.MinValue;
        DateTime lastVisualPushTime = DateTime.MinValue;
        bool lastCrossfadeMuteDown = false;
        bool lastQuickSwapDown = false;
        bool audioUpdatePending = false;

        KeyPressTracker cfUpTracker = default;
        KeyPressTracker cfDownTracker = default;

        while (!token.IsCancellationRequested)
        {
            if (_forceResetAudioFlag)
            {
                _forceResetAudioFlag = false;
                ResetAllConfiguredExecutables(sessionCache);
                ApplyVolumes(sessionCache, true, true);
            }

            bool physicalInputRegistered = false;
            bool immediateAudioUpdate = false;

            if (_overlay != null && _overlay.IsFadedOut)
            {
                _detentClicks = 0;
                _detentDirection = 0;
            }

            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

            int swapBinding = _settings.QuickSwapKey;
            int swapVk = KeybindHelper.GetPrimaryVk(swapBinding);
            bool swapDown = swapVk != 0 && (GetAsyncKeyState(swapVk) & 0x8000) != 0 && ModifiersMatch(swapBinding, ctrlDown, altDown, shiftDown);
            if (swapDown && !lastQuickSwapDown)
            {
                PerformQuickSwapOrCycle(sessionCache);
                physicalInputRegistered = true;
                immediateAudioUpdate = true;
            }
            lastQuickSwapDown = swapDown;

            bool isVolMode = (_settings.OperationMode == "Volume Control");
            float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;
            float lerpFactor = Math.Clamp(_settings.InterpolationSpeed, 5, 100) / 100.0f;
            bool isAnimating = false;

            var now = DateTime.UtcNow;
            bool frameTickReady = (now - lastVisualPushTime).TotalMilliseconds >= 15;

            if (isVolMode)
            {
                float volDeltaPerStep = stepIncrement * 2.0f;

                lock (_stateLock)
                {
                    for (int i = 0; i < _volChannels.Count; i++)
                    {
                        var ch = _volChannels[i];
                        int upVk = KeybindHelper.GetPrimaryVk(ch.KeyUp);
                        int downVk = KeybindHelper.GetPrimaryVk(ch.KeyDown);
                        int muteVk = KeybindHelper.GetPrimaryVk(ch.KeyMute);

                        bool upModsOk = ModifiersMatch(ch.KeyUp, ctrlDown, altDown, shiftDown);
                        bool downModsOk = ModifiersMatch(ch.KeyDown, ctrlDown, altDown, shiftDown);
                        bool muteModsOk = ModifiersMatch(ch.KeyMute, ctrlDown, altDown, shiftDown);

                        int upSteps = ch.UpTracker.PollSteps(upVk, upModsOk);
                        int downSteps = ch.DownTracker.PollSteps(downVk, downModsOk);

                        bool muteDown = muteVk != 0 && (GetAsyncKeyState(muteVk) & 0x8000) != 0 && muteModsOk;
                        if (muteDown && !ch.LastMuteDown)
                        {
                            ch.IsMuted = !ch.IsMuted;
                            _activeVolChannelIndex = i;
                            physicalInputRegistered = true;
                            immediateAudioUpdate = true;
                        }
                        ch.LastMuteDown = muteDown;

                        if (upSteps > 0)
                        {
                            ch.TargetVol = Math.Clamp(ch.TargetVol + volDeltaPerStep * upSteps, 0.0f, 1.0f);
                            _activeVolChannelIndex = i;
                            physicalInputRegistered = true;
                        }
                        if (downSteps > 0)
                        {
                            ch.TargetVol = Math.Clamp(ch.TargetVol - volDeltaPerStep * downSteps, 0.0f, 1.0f);
                            _activeVolChannelIndex = i;
                            physicalInputRegistered = true;
                        }

                        if (_settings.EnableInterpolation)
                        {
                            float diff = ch.TargetVol - ch.VisualVol;
                            if (Math.Abs(diff) > 0.001f)
                            {
                                if (frameTickReady) ch.VisualVol += diff * lerpFactor;
                                isAnimating = true;
                            }
                            else
                            {
                                ch.VisualVol = ch.TargetVol;
                            }
                        }
                        else
                        {
                            ch.VisualVol = ch.TargetVol;
                        }
                    }
                }
            }
            else
            {
                int activeKeyUp = _settings.CrossfadeKeyUp;
                int activeKeyDown = _settings.CrossfadeKeyDown;
                int activeKeyMute = _settings.CrossfadeKeyMute;

                int upVk = KeybindHelper.GetPrimaryVk(activeKeyUp);
                int downVk = KeybindHelper.GetPrimaryVk(activeKeyDown);
                int muteVk = KeybindHelper.GetPrimaryVk(activeKeyMute);

                bool upModsOk = ModifiersMatch(activeKeyUp, ctrlDown, altDown, shiftDown);
                bool downModsOk = ModifiersMatch(activeKeyDown, ctrlDown, altDown, shiftDown);
                bool muteModsOk = ModifiersMatch(activeKeyMute, ctrlDown, altDown, shiftDown);

                int upSteps = cfUpTracker.PollSteps(upVk, upModsOk);
                int downSteps = cfDownTracker.PollSteps(downVk, downModsOk);

                bool currentMuteComboDown = muteVk != 0 && (GetAsyncKeyState(muteVk) & 0x8000) != 0 && muteModsOk;
                if (currentMuteComboDown && !lastCrossfadeMuteDown)
                {
                    // Cycle order: State 1 (Source 2 Muted) -> State 2 (Source 1 Muted) -> State 0 (Unmuted)
                    _muteState = _muteState switch
                    {
                        1 => 2,
                        2 => 0,
                        _ => 1
                    };
                    physicalInputRegistered = true;
                    immediateAudioUpdate = true;
                }
                lastCrossfadeMuteDown = currentMuteComboDown;

                if (upSteps > 0)
                {
                    StepCrossfade(stepIncrement * upSteps);
                    physicalInputRegistered = true;
                }
                if (downSteps > 0)
                {
                    StepCrossfade(-stepIncrement * downSteps);
                    physicalInputRegistered = true;
                }

                if (_settings.EnableInterpolation)
                {
                    float diff = _targetPosition - _visualPosition;
                    if (Math.Abs(diff) > 0.0005f)
                    {
                        if (frameTickReady) _visualPosition += diff * lerpFactor;
                        isAnimating = true;
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
            }

            if (physicalInputRegistered)
            {
                audioUpdatePending = true;
            }

            // Never drop a pending audio update; apply immediately on mute/swap or once 12ms have elapsed
            bool shouldUpdateAudio = audioUpdatePending && (immediateAudioUpdate || (now - lastAudioApplyTime).TotalMilliseconds >= 12);
            if (shouldUpdateAudio)
            {
                if (immediateAudioUpdate)
                {
                    sessionCache.ForceRefresh();
                }
                lastAudioApplyTime = now;
                audioUpdatePending = false;
            }

            if (physicalInputRegistered || shouldUpdateAudio || (isAnimating && frameTickReady))
            {
                if (frameTickReady || physicalInputRegistered) lastVisualPushTime = now;
                ApplyVolumes(sessionCache, shouldUpdateAudio, physicalInputRegistered);
            }

            Thread.Sleep(2);
        }

        ResetAllConfiguredExecutables(sessionCache);
    }

    private void StepCrossfade(float delta)
    {
        float stepIncrement = (Math.Clamp(_settings.StepSize, 1, 100) / 2.0f) / 100.0f;
        float snapEpsilon = Math.Min(0.015f, stepIncrement * 0.45f);

        float SnapEndpoints(float val)
        {
            if (val <= snapEpsilon) return 0.0f;
            if (val >= 1.0f - snapEpsilon) return 1.0f;
            return Math.Clamp(val, 0.0f, 1.0f);
        }

        if (!_settings.EnableEqualizationBuffer)
        {
            _targetPosition = SnapEndpoints(_targetPosition + delta);
            return;
        }

        if (Math.Abs(_targetPosition - 0.5f) < 0.001f)
        {
            bool isFromRest = (_overlay == null || _overlay.IsFadedOut);
            int currentDeltaSign = Math.Sign(delta);

            if (isFromRest || (currentDeltaSign != 0 && currentDeltaSign != _detentDirection))
            {
                _targetPosition = SnapEndpoints(_targetPosition + delta);
                _detentClicks = 0;
                _detentDirection = currentDeltaSign;
            }
            else
            {
                _detentClicks++;
                if (_detentClicks >= 3)
                {
                    _detentClicks = 0;
                    _targetPosition = SnapEndpoints(_targetPosition + delta);
                    _detentDirection = currentDeltaSign;
                }
            }
        }
        else
        {
            float newTarget = _targetPosition + delta;
            bool crossingCenter = (_targetPosition < 0.5f && newTarget >= 0.5f - snapEpsilon) ||
                                  (_targetPosition > 0.5f && newTarget <= 0.5f + snapEpsilon);
            if (crossingCenter)
            {
                _targetPosition = 0.5f;
                _detentClicks = 0;
                _detentDirection = Math.Sign(delta);
            }
            else
            {
                _targetPosition = SnapEndpoints(newTarget);
            }
        }
    }

    private void ApplyVolumes(AudioSessionCache sessionCache, bool updateAudioSessions, bool forceShowPopup)
    {
        bool isVolumeControl = (_settings.OperationMode == "Volume Control");

        if (isVolumeControl)
        {
            string activeLabel = "";
            string activeRawSource = "";
            float activeVisVol = 1.0f;
            int activeMuteState = 0;

            lock (_stateLock)
            {
                if (updateAudioSessions)
                {
                    foreach (var ch in _volChannels)
                    {
                        float outVol = ch.IsMuted ? 0.0f : ch.TargetVol;
                        if (outVol <= 0.01f) outVol = 0.0f;
                        if (outVol >= 0.99f) outVol = 1.0f;

                        foreach (var exe in _settings.ResolveExecutables(ch.Source))
                        {
                            sessionCache.SetVolumeForProcessKey(AppSettings.NormalizeProcessKey(exe), outVol);
                        }
                    }
                }

                if (_volChannels.Count > 0)
                {
                    int idx = Math.Clamp(_activeVolChannelIndex, 0, _volChannels.Count - 1);
                    var activeCh = _volChannels[idx];
                    activeRawSource = activeCh.Source;
                    activeLabel = GetResolvedLabel(activeCh.Source);
                    activeVisVol = Math.Clamp(activeCh.VisualVol, 0.0f, 1.0f);
                    activeMuteState = activeCh.IsMuted ? 1 : 0;
                }
            }

            _overlay?.UpdateDynamicOverlay(
                true,
                activeLabel,
                "",
                activeRawSource,
                "",
                activeVisVol,
                0.0f,
                activeMuteState,
                forceShowPopup);
        }
        else
        {
            float audioApp1Vol = (_targetPosition >= 0.5f) ? 1.0f : (_targetPosition * 2.0f);
            float audioApp2Vol = (_targetPosition >= 0.5f) ? (1.0f - ((_targetPosition - 0.5f) * 2.0f)) : 1.0f;

            audioApp1Vol = Math.Clamp(audioApp1Vol, 0.0f, 1.0f);
            audioApp2Vol = Math.Clamp(audioApp2Vol, 0.0f, 1.0f);

            if (audioApp1Vol <= 0.01f) audioApp1Vol = 0.0f;
            if (audioApp1Vol >= 0.99f) audioApp1Vol = 1.0f;
            if (audioApp2Vol <= 0.01f) audioApp2Vol = 0.0f;
            if (audioApp2Vol >= 0.99f) audioApp2Vol = 1.0f;

            float actualApp1Vol = (_muteState == 2) ? 0.0f : audioApp1Vol;
            float actualApp2Vol = (_muteState == 1) ? 0.0f : audioApp2Vol;

            string s1 = _settings.Source1;
            string s2 = _settings.Source2;

            if (updateAudioSessions)
            {
                var source1Targets = _settings.ResolveExecutables(s1);
                var source1Keys = new HashSet<string>(source1Targets.Select(AppSettings.NormalizeProcessKey), StringComparer.OrdinalIgnoreCase);

                foreach (var exe in source1Targets)
                {
                    sessionCache.SetVolumeForProcessKey(AppSettings.NormalizeProcessKey(exe), actualApp1Vol);
                }

                var source2Targets = _settings.ResolveExecutables(s2);
                foreach (var exe in source2Targets)
                {
                    string key = AppSettings.NormalizeProcessKey(exe);
                    if (!source1Keys.Contains(key))
                    {
                        sessionCache.SetVolumeForProcessKey(key, actualApp2Vol);
                    }
                }
            }

            float visualApp1Vol = (_visualPosition >= 0.5f) ? 1.0f : (_visualPosition * 2.0f);
            float visualApp2Vol = (_visualPosition >= 0.5f) ? (1.0f - ((_visualPosition - 0.5f) * 2.0f)) : 1.0f;

            visualApp1Vol = Math.Clamp(visualApp1Vol, 0.0f, 1.0f);
            visualApp2Vol = Math.Clamp(visualApp2Vol, 0.0f, 1.0f);

            _overlay?.UpdateDynamicOverlay(
                false,
                GetResolvedLabel(s1),
                GetResolvedLabel(s2),
                s1,
                s2,
                visualApp1Vol,
                visualApp2Vol,
                _muteState,
                forceShowPopup);
        }
    }
}
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

public readonly struct BadgeInfo
{
    public string Text { get; }
    public Color Color { get; }

    public BadgeInfo(string text, Color color)
    {
        Text = text;
        Color = color;
    }
}

// Custom Dark-Mode Renderer for System Tray ContextMenuStrip
public class DarkContextMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(32, 32, 32);
    public override Color ImageMarginGradientBegin => Color.FromArgb(32, 32, 32);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(32, 32, 32);
    public override Color ImageMarginGradientEnd => Color.FromArgb(32, 32, 32);
    public override Color MenuBorder => Color.FromArgb(62, 62, 62);
    public override Color MenuItemBorder => Color.FromArgb(75, 75, 75);
    public override Color MenuItemSelected => Color.FromArgb(52, 52, 52);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(52, 52, 52);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(52, 52, 52);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(45, 45, 45);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(45, 45, 45);
    public override Color SeparatorDark => Color.FromArgb(55, 55, 55);
    public override Color SeparatorLight => Color.FromArgb(55, 55, 55);
    public override Color CheckBackground => Color.FromArgb(0, 120, 212);
    public override Color CheckSelectedBackground => Color.FromArgb(20, 138, 230);
    public override Color CheckPressedBackground => Color.FromArgb(0, 100, 180);
}

public class DarkContextMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkContextMenuRenderer() : base(new DarkContextMenuColorTable())
    {
        this.RoundedEdges = true;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!e.Item.Enabled)
        {
            e.TextColor = Color.FromArgb(140, 140, 140);
        }
        else if (e.TextColor == SystemColors.ControlText || e.TextColor == Color.Black)
        {
            e.TextColor = Color.FromArgb(240, 240, 240);
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Color.FromArgb(220, 220, 220);
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
        using SolidBrush b = new SolidBrush(Color.FromArgb(0, 120, 212));
        using GraphicsPath path = Win11CardPanel.CreateRoundRect(r, 3);
        e.Graphics.FillPath(b, path);

        using Pen checkPen = new Pen(Color.White, 1.8f);
        e.Graphics.DrawLines(checkPen, new[]
        {
            new Point(r.Left + 3, r.Top + r.Height / 2),
            new Point(r.Left + r.Width / 2 - 1, r.Bottom - 3),
            new Point(r.Right - 2, r.Top + 3)
        });
    }
}

// Manages System Tray Quick-Actions, Cycle Lists, and Background Worker
public class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon? _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    private AppSettings _activeSettings;
    private AudioCrossfaderWorker? _crossfaderWorker;
    private SettingsForm? _settingsForm;
    private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public TrayApplicationContext()
    {
        _activeSettings = LoadSettings();
        AppIconCache.LoadPathsFromSettings(_activeSettings);

        _contextMenu = new ContextMenuStrip
        {
            Renderer = new DarkContextMenuRenderer(),
            BackColor = Color.FromArgb(32, 32, 32),
            ForeColor = Color.FromArgb(240, 240, 240),
            ShowImageMargin = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
        _contextMenu.Opening += (s, e) => PopulateTrayMenu();
        PopulateTrayMenu();

        Icon trayIconAsset = SystemIcons.Application;
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                trayIconAsset = new Icon(iconPath);
            }
            else
            {
                trayIconAsset = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
        }
        catch { }

        _trayIcon = new NotifyIcon
        {
            Icon = trayIconAsset,
            ContextMenuStrip = _contextMenu,
            Visible = true,
            Text = "Audio Crossfader"
        };
        _trayIcon.DoubleClick += (s, e) => ShowSettings();

        StartWorker();
    }

    private void PopulateTrayMenu()
    {
        _contextMenu.Items.Clear();

        bool isVolMode = (_activeSettings.OperationMode == "Volume Control");
        var modeItem = new ToolStripMenuItem($"Mode: {_activeSettings.OperationMode} (Switch)")
        {
            ForeColor = Color.FromArgb(96, 205, 255)
        };
        modeItem.Click += (s, e) =>
        {
            var updated = _activeSettings.Clone();
            updated.OperationMode = isVolMode ? "Crossfade" : "Volume Control";
            SaveSettings(updated);
        };
        _contextMenu.Items.Add(modeItem);

        if (!isVolMode)
        {
            var swapItem = new ToolStripMenuItem($"Swap Sources ({_activeSettings.Source1} ↔ {_activeSettings.Source2})");
            swapItem.Click += (s, e) =>
            {
                var updated = _activeSettings.Clone();
                (updated.Source1, updated.Source2) = (updated.Source2, updated.Source1);
                (updated.Source1Color, updated.Source2Color) = (updated.Source2Color, updated.Source1Color);
                SaveSettings(updated);
            };
            _contextMenu.Items.Add(swapItem);
        }

        var resetItem = new ToolStripMenuItem("Reset Volumes to 100%");
        resetItem.Click += (s, e) => ResetLiveVolumes();
        _contextMenu.Items.Add(resetItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        if (isVolMode)
        {
            var validEntries = _activeSettings.VolumeCycleList
                .Where(v => !string.IsNullOrWhiteSpace(v.Source))
                .ToList();

            if (validEntries.Count > 0)
            {
                var header = new ToolStripMenuItem("Volume Source Cycle List") { Enabled = false };
                _contextMenu.Items.Add(header);

                foreach (var entry in validEntries)
                {
                    var item = new ToolStripMenuItem(entry.Source)
                    {
                        Checked = _activeSettings.Source1.Equals(entry.Source, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += (s, e) =>
                    {
                        var updated = _activeSettings.Clone();
                        updated.Source1 = entry.Source;
                        updated.Source1Color = entry.ColorHex;
                        SaveSettings(updated);
                    };
                    _contextMenu.Items.Add(item);
                }
            }
            else
            {
                var emptyItem = new ToolStripMenuItem("No Volume Cycle Sources Configured") { Enabled = false };
                _contextMenu.Items.Add(emptyItem);
            }
        }
        else
        {
            var validPairs = _activeSettings.CrossfadeCycleList
                .Where(p => !string.IsNullOrWhiteSpace(p.Source1) && !string.IsNullOrWhiteSpace(p.Source2))
                .ToList();

            if (validPairs.Count > 0)
            {
                var header = new ToolStripMenuItem("Crossfade Source Pairs") { Enabled = false };
                _contextMenu.Items.Add(header);

                foreach (var pair in validPairs)
                {
                    var item = new ToolStripMenuItem($"{pair.Source1}  ⇄  {pair.Source2}")
                    {
                        Checked = _activeSettings.Source1.Equals(pair.Source1, StringComparison.OrdinalIgnoreCase) &&
                                  _activeSettings.Source2.Equals(pair.Source2, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += (s, e) =>
                    {
                        var updated = _activeSettings.Clone();
                        updated.Source1 = pair.Source1;
                        updated.Source1Color = pair.Color1Hex;
                        updated.Source2 = pair.Source2;
                        updated.Source2Color = pair.Color2Hex;
                        SaveSettings(updated);
                    };
                    _contextMenu.Items.Add(item);
                }
            }
            else
            {
                var emptyItem = new ToolStripMenuItem("No Crossfade Pairs Configured") { Enabled = false };
                _contextMenu.Items.Add(emptyItem);
            }
        }

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Settings...", null, (s, e) => ShowSettings());
        _contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                if (loaded.CycleNextKey == 0 && loaded.QuickSwapKey != 0)
                {
                    loaded.CycleNextKey = loaded.QuickSwapKey;
                }
                loaded.RecordProgramHistory(loaded.Source1);
                loaded.RecordProgramHistory(loaded.Source2);
                return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings newSettings)
    {
        _activeSettings = newSettings.Clone();
        PersistSettingsToDisk(_activeSettings);
        _crossfaderWorker?.UpdateSettings(_activeSettings, false);
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _crossfaderWorker?.SetSettingsWindowOpen(true, _activeSettings);
        }
    }

    public void TriggerOverlayPreview(AppSettings draftSettings)
    {
        _crossfaderWorker?.PreviewOverlay(draftSettings);
    }

    public void NotifySettingsWindowState(bool isOpen, AppSettings? draftSettings = null)
    {
        _crossfaderWorker?.SetSettingsWindowOpen(isOpen, draftSettings);
    }

    private void PersistSettingsToDisk(AppSettings settingsToSave)
    {
        try
        {
            string json = JsonSerializer.Serialize(settingsToSave, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public void ResetLiveVolumes()
    {
        _crossfaderWorker?.ResetVolumesToFullAndCenter();
    }

    private void StartWorker()
    {
        _crossfaderWorker = new AudioCrossfaderWorker(_activeSettings, mutatedSettings =>
        {
            _activeSettings = mutatedSettings;
            PersistSettingsToDisk(_activeSettings);
        });
        _crossfaderWorker.Start();
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_activeSettings.Clone(), this);
            _settingsForm.FormClosed += (s, e) => NotifySettingsWindowState(false);
            _settingsForm.Show();
            NotifySettingsWindowState(true, _activeSettings);
        }
        else
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            _settingsForm.BringToFront();
        }
    }

    private void ExitApp()
    {
        _crossfaderWorker?.Stop();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        Application.Exit();
    }
}

// Custom Dark-Mode Scrollable Container
public class DarkScrollPanel : Panel
{
    private readonly Panel _content;
    private int _scrollOffset = 0;
    private int _contentHeight = 0;
    private bool _draggingThumb = false;
    private bool _hoveringThumb = false;
    private int _dragStartY = 0;
    private int _dragStartScroll = 0;
    private const int ScrollBarWidth = 8;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control.ControlCollection InnerControls => _content.Controls;

    public DarkScrollPanel()
    {
        this.SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.BackColor = Color.FromArgb(28, 28, 28);

        _content = new Panel
        {
            Left = 0,
            Top = 0,
            Width = this.ClientSize.Width - ScrollBarWidth - 2,
            Height = 10,
            BackColor = Color.FromArgb(28, 28, 28)
        };
        this.Controls.Add(_content);
    }

    public void RecalculateScroll()
    {
        int maxBottom = 0;
        foreach (Control c in _content.Controls)
        {
            if (c.Visible && c.Bottom > maxBottom)
            {
                maxBottom = c.Bottom;
            }
        }

        _contentHeight = maxBottom + 10;
        _content.Width = Math.Max(50, this.ClientSize.Width - ScrollBarWidth - 4);
        _content.Height = Math.Max(this.ClientSize.Height, _contentHeight);

        ClampAndApplyScroll();
        Invalidate();
    }

    private void ClampAndApplyScroll()
    {
        int maxScroll = Math.Max(0, _contentHeight - this.ClientSize.Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
        _content.Top = -_scrollOffset;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_content != null)
        {
            RecalculateScroll();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_contentHeight <= this.ClientSize.Height) return;

        int step = 36;
        _scrollOffset -= Math.Sign(e.Delta) * step;
        ClampAndApplyScroll();
        Invalidate();
    }

    private Rectangle GetThumbRect()
    {
        if (_contentHeight <= this.ClientSize.Height || this.ClientSize.Height <= 0)
            return Rectangle.Empty;

        int trackHeight = this.ClientSize.Height - 8;
        int thumbHeight = Math.Max(28, (int)((float)this.ClientSize.Height / _contentHeight * trackHeight));
        int maxScroll = _contentHeight - this.ClientSize.Height;
        int maxThumbTop = trackHeight - thumbHeight;
        int thumbTop = 4 + (maxScroll > 0 ? (int)((float)_scrollOffset / maxScroll * maxThumbTop) : 0);

        return new Rectangle(this.ClientSize.Width - ScrollBarWidth - 2, thumbTop, 6, thumbHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Rectangle thumb = GetThumbRect();
        if (!thumb.IsEmpty && thumb.Contains(e.Location))
        {
            _draggingThumb = true;
            _dragStartY = e.Y;
            _dragStartScroll = _scrollOffset;
            this.Capture = true;
        }
        else if (!thumb.IsEmpty && e.X >= this.ClientSize.Width - ScrollBarWidth - 6)
        {
            _scrollOffset += (e.Y < thumb.Top) ? -120 : 120;
            ClampAndApplyScroll();
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingThumb)
        {
            int trackHeight = this.ClientSize.Height - 8;
            Rectangle thumb = GetThumbRect();
            int maxThumbTravel = Math.Max(1, trackHeight - thumb.Height);
            int maxScroll = Math.Max(0, _contentHeight - this.ClientSize.Height);

            int deltaY = e.Y - _dragStartY;
            _scrollOffset = _dragStartScroll + (int)((float)deltaY / maxThumbTravel * maxScroll);
            ClampAndApplyScroll();
            Invalidate();
        }
        else
        {
            Rectangle thumb = GetThumbRect();
            bool over = !thumb.IsEmpty && thumb.Contains(e.Location);
            if (over != _hoveringThumb)
            {
                _hoveringThumb = over;
                Invalidate();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingThumb)
        {
            _draggingThumb = false;
            this.Capture = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle thumb = GetThumbRect();
        if (!thumb.IsEmpty)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color thumbColor = (_draggingThumb || _hoveringThumb)
                ? Color.FromArgb(135, 135, 135)
                : Color.FromArgb(85, 85, 85);

            using GraphicsPath path = Win11CardPanel.CreateRoundRect(thumb, 3);
            using SolidBrush brush = new SolidBrush(thumbColor);
            e.Graphics.FillPath(brush, path);
        }
    }
}

// Custom Windows 11 Rounded Surface Card
public class Win11CardPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(39, 39, 39);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(55, 55, 55);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 8;

    public Win11CardPanel()
    {
        this.SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.BackColor = Color.Transparent;
        this.Padding = new Padding(12);
    }

    public void FitHeightToContent(int bottomPadding = 12)
    {
        int maxBottom = 0;
        foreach (Control c in this.Controls)
        {
            if (c.Visible && c.Bottom > maxBottom)
            {
                maxBottom = c.Bottom;
            }
        }
        if (maxBottom > 0)
        {
            this.Height = maxBottom + bottomPadding;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using GraphicsPath path = CreateRoundRect(rect, CornerRadius);
        using SolidBrush fillBrush = new SolidBrush(FillColor);
        using Pen borderPen = new Pen(BorderColor, 1f);

        e.Graphics.FillPath(fillBrush, path);
        e.Graphics.DrawPath(borderPen, path);
    }

    public static GraphicsPath CreateRoundRect(Rectangle r, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Custom Borderless Dark-Mode ComboBox
public class ModernComboBox : ComboBox
{
    private const int WM_PAINT = 0x000F;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, BadgeInfo>? BadgeResolver { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, Bitmap?>? IconResolver { get; set; }

    public ModernComboBox()
    {
        this.BackColor = Color.FromArgb(48, 48, 48);
        this.ForeColor = Color.FromArgb(242, 242, 242);
        this.FlatStyle = FlatStyle.Flat;
        this.DrawMode = DrawMode.OwnerDrawFixed;
        this.ItemHeight = 22;
        this.Font = new Font("Segoe UI", 9.25f, FontStyle.Regular);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Color bg = isSelected ? Color.FromArgb(0, 103, 192) : Color.FromArgb(45, 45, 45);

        using (SolidBrush bgBrush = new SolidBrush(bg))
        {
            e.Graphics.FillRectangle(bgBrush, e.Bounds);
        }

        string itemText = Items[e.Index]?.ToString() ?? "";
        BadgeInfo badge = BadgeResolver != null ? BadgeResolver(itemText) : new BadgeInfo("", Color.Transparent);
        Bitmap? icon = IconResolver?.Invoke(itemText);

        int textLeft = e.Bounds.X + 6;
        if (icon != null)
        {
            int iconSize = 16;
            int iconY = e.Bounds.Y + (e.Bounds.Height - iconSize) / 2;
            e.Graphics.DrawImage(icon, new Rectangle(textLeft, iconY, iconSize, iconSize));
            textLeft += iconSize + 6;
        }

        Rectangle textRect = new Rectangle(textLeft, e.Bounds.Y, e.Bounds.Right - textLeft - 6, e.Bounds.Height);
        if (!string.IsNullOrEmpty(badge.Text))
        {
            using Font badgeFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            SizeF badgeSize = e.Graphics.MeasureString(badge.Text, badgeFont);
            int pillWidth = (int)badgeSize.Width + 12;
            int pillHeight = Math.Max(14, e.Bounds.Height - 6);
            Rectangle pillRect = new Rectangle(e.Bounds.Right - pillWidth - 6, e.Bounds.Y + (e.Bounds.Height - pillHeight) / 2, pillWidth, pillHeight);

            using GraphicsPath pillPath = Win11CardPanel.CreateRoundRect(pillRect, 4);
            using SolidBrush pillBrush = new SolidBrush(isSelected ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(45, badge.Color));
            e.Graphics.FillPath(pillBrush, pillPath);

            using SolidBrush badgeTextBrush = new SolidBrush(isSelected ? Color.White : badge.Color);
            using StringFormat sfBadge = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(badge.Text, badgeFont, badgeTextBrush, pillRect, sfBadge);

            textRect.Width = Math.Max(40, pillRect.Left - textRect.Left - 6);
        }

        TextRenderer.DrawText(
            e.Graphics,
            itemText,
            this.Font,
            textRect,
            Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT)
        {
            using Graphics g = Graphics.FromHwnd(this.Handle);
            using Pen outerPen = new Pen(this.Focused ? Color.FromArgb(0, 120, 212) : Color.FromArgb(68, 68, 68), 1);
            using Pen innerPen = new Pen(this.BackColor, 1);
            g.DrawRectangle(outerPen, 0, 0, this.Width - 1, this.Height - 1);
            g.DrawRectangle(innerPen, 1, 1, this.Width - 3, this.Height - 3);
        }
    }
}

// Windows 11 Dark Mode Settings Window
public class SettingsForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private Button _navBtnGeneral = null!;
    private Button _navBtnGroups = null!;
    private Button _navBtnPersonalization = null!;
    private DarkScrollPanel _pageGeneral = null!;
    private DarkScrollPanel _pageGroups = null!;
    private DarkScrollPanel _pagePersonalization = null!;

    private Win11CardPanel _sourcesCard = null!;
    private ModernComboBox _cmbMode = null!;
    private ModernComboBox _cmbSource1 = null!;
    private Panel _swatchSource1 = null!;
    private ModernComboBox _cmbSource2 = null!;
    private Panel _swatchSource2 = null!;
    private Panel _source2Container = null!;

    private Win11CardPanel _keysCard = null!;
    private Win11CardPanel _cycleListCard = null!;
    private Label _lblCycleCardTitle = null!;
    private Button _btnAddCycleEntry = null!;
    private Panel _cycleSlotsHost = null!;
    private Win11CardPanel _behaviorCard = null!;

    private Label _lblKey1 = null!;
    private TextBox _txtKey1 = null!;
    private Button _btnRec1 = null!;

    private Label _lblKey2 = null!;
    private TextBox _txtKey2 = null!;
    private Button _btnRec2 = null!;

    private Label _lblKey3 = null!;
    private TextBox _txtKey3 = null!;
    private Button _btnRec3 = null!;

    private Label _lblKeyCycleNext = null!;
    private TextBox _txtKeyCycleNext = null!;
    private Button _btnRecCycleNext = null!;

    private Label _lblKeyCyclePrev = null!;
    private TextBox _txtKeyCyclePrev = null!;
    private Button _btnRecCyclePrev = null!;

    private CheckBox _chkKeepOverlayAlwaysOn = null!;
    private ModernComboBox _cmbIdentityMode = null!;
    private ModernComboBox _cmbLevelMode = null!;
    private CheckBox _chkUseSourceBgColors = null!;
    private CheckBox _chkFadeBgWithVolume = null!;
    private ModernComboBox _cmbSingleSourceFillMode = null!;
    private TextBox _txtCarouselNeighbors = null!;
    private CheckBox _chkCarouselBlendColors = null!;
    private ModernComboBox _cmbPosition = null!;
    private TextBox _txtPopupDuration = null!;
    private TextBox _txtStepSize = null!;
    private TextBox _txtWidth = null!;
    private TextBox _txtHeight = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkInterpolation = null!;
    private TextBox _txtInterpSpeed = null!;
    private CheckBox _chkBuffer = null!;

    private Panel _unmutedColorBox = null!;
    private Panel _mutedColorBox = null!;

    private readonly TrayApplicationContext _context;

    private AppSettings _draftSettings;
    private AppSettings _savedSettingsSnapshot;
    private DetectedAudioPrograms _detectedAudio = new DetectedAudioPrograms();
    private Label _lblStatus = null!;

    private bool _isRecording = false;
    private Button? _activeRecordButton = null;
    private TextBox? _activeTargetTextBox = null;
    private Action<int>? _activeKeyCommitCallback = null;
    private string _previousButtonText = "";
    private string _previousTextBoxText = "";
    private KeyCaptureFilter? _mouseFilter;
    private bool _isInitializing = true;

    public SettingsForm(AppSettings initialSettings, TrayApplicationContext context)
    {
        _context = context;
        _draftSettings = initialSettings.Clone();
        _savedSettingsSnapshot = initialSettings.Clone();
        _detectedAudio = AudioProcessDetector.Scan();

        this.Text = "Audio Crossfader Settings";
        this.Size = new Size(520, 780);
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.MinimizeBox = true;
        this.ShowInTaskbar = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.KeyPreview = true;
        this.Font = new Font("Segoe UI", 9.25f, FontStyle.Regular);

        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                this.Icon = new Icon(iconPath);
            }
            else
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
        }
        catch { }

        this.BackColor = Color.FromArgb(28, 28, 28);
        this.ForeColor = Color.FromArgb(242, 242, 242);

        Panel topNavHeader = BuildTopNavigationBar();
        Controls.Add(topNavHeader);

        Panel bottomPanel = BuildBottomActionBar();
        Controls.Add(bottomPanel);

        Panel contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 28),
            Padding = new Padding(0)
        };

        _pageGeneral = CreateGeneralPage();
        _pageGroups = CreateGroupsPage();
        _pagePersonalization = CreatePersonalizationPage();

        contentHost.Controls.Add(_pageGeneral);
        contentHost.Controls.Add(_pageGroups);
        contentHost.Controls.Add(_pagePersonalization);
        Controls.Add(contentHost);

        contentHost.BringToFront();

        _mouseFilter = new KeyCaptureFilter(CancelRecording);
        Application.AddMessageFilter(_mouseFilter);

        this.FormClosed += (s, e) =>
        {
            if (_mouseFilter != null) Application.RemoveMessageFilter(_mouseFilter);
            _context.NotifySettingsWindowState(false);
        };

        this.KeyDown += SettingsForm_KeyDown;
        this.KeyUp += SettingsForm_KeyUp;

        _isInitializing = true;
        LoadSettingsIntoUI(_draftSettings);
        _isInitializing = false;

        SwitchTab(0);
        UpdateContextUI(false);
        MarkClean();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(this.Handle, 20, ref useImmersiveDarkMode, sizeof(int));
            int cornerPreference = 2;
            DwmSetWindowAttribute(this.Handle, 33, ref cornerPreference, sizeof(int));
        }
        catch { }
    }

    private Panel BuildTopNavigationBar()
    {
        Panel header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            BackColor = Color.FromArgb(28, 28, 28)
        };

        Win11CardPanel pillTrack = new Win11CardPanel
        {
            Left = 18,
            Top = 10,
            Width = 468,
            Height = 38,
            FillColor = Color.FromArgb(37, 37, 37),
            BorderColor = Color.FromArgb(52, 52, 52),
            CornerRadius = 8,
            Padding = new Padding(3)
        };

        _navBtnGeneral = CreateNavTabButton("General & Sources", 4, 4, 156, () => SwitchTab(0));
        _navBtnGroups = CreateNavTabButton("Program Groups", 162, 4, 150, () => SwitchTab(1));
        _navBtnPersonalization = CreateNavTabButton("Personalization", 314, 4, 150, () => SwitchTab(2));

        pillTrack.Controls.Add(_navBtnGeneral);
        pillTrack.Controls.Add(_navBtnGroups);
        pillTrack.Controls.Add(_navBtnPersonalization);
        header.Controls.Add(pillTrack);

        return header;
    }

    private Button CreateNavTabButton(string text, int left, int top, int width, Action onClick)
    {
        Button btn = new Button
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            BackColor = Color.FromArgb(37, 37, 37),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 50);
        btn.Click += (s, e) => onClick();
        return btn;
    }

    private void SwitchTab(int index)
    {
        _pageGeneral.Visible = (index == 0);
        _pageGroups.Visible = (index == 1);
        _pagePersonalization.Visible = (index == 2);

        StyleNavButton(_navBtnGeneral, index == 0);
        StyleNavButton(_navBtnGroups, index == 1);
        StyleNavButton(_navBtnPersonalization, index == 2);

        if (index == 0) _pageGeneral.RecalculateScroll();
        else if (index == 1) _pageGroups.RecalculateScroll();
        else if (index == 2) _pagePersonalization.RecalculateScroll();
    }

    private void StyleNavButton(Button btn, bool active)
    {
        btn.BackColor = active ? Color.FromArgb(0, 120, 212) : Color.FromArgb(37, 37, 37);
        btn.ForeColor = active ? Color.White : Color.FromArgb(185, 185, 185);
    }

    private Panel BuildBottomActionBar()
    {
        Panel bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
            BackColor = Color.FromArgb(24, 24, 24)
        };

        bottomPanel.Paint += (s, e) =>
        {
            using Pen dividerPen = new Pen(Color.FromArgb(48, 48, 48), 1);
            e.Graphics.DrawLine(dividerPen, 0, 0, bottomPanel.Width, 0);
        };

        _lblStatus = new Label
        {
            Text = "",
            Left = 18,
            Top = 6,
            Width = 468,
            Height = 18,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(240, 190, 80)
        };
        bottomPanel.Controls.Add(_lblStatus);

        Button btnResetVol = CreateModernButton("Reset Volumes", 18, 29, 135, 33, false);
        btnResetVol.Click += (s, e) => _context.ResetLiveVolumes();
        bottomPanel.Controls.Add(btnResetVol);

        Button btnRevert = CreateModernButton("Revert Changes", 163, 29, 140, 33, false);
        btnRevert.Click += (s, e) => RevertChanges();
        bottomPanel.Controls.Add(btnRevert);

        Button btnSave = CreateModernButton("Save & Apply", 313, 29, 173, 33, true);
        btnSave.Click += (s, e) => SaveChanges();
        bottomPanel.Controls.Add(btnSave);

        return bottomPanel;
    }

    private Button CreateModernButton(string text, int left, int top, int width, int height, bool isPrimary)
    {
        Button btn = new Button
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BackColor = isPrimary ? Color.FromArgb(0, 120, 212) : Color.FromArgb(52, 52, 52),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = isPrimary ? Color.FromArgb(30, 144, 255) : Color.FromArgb(70, 70, 70);
        btn.FlatAppearance.MouseOverBackColor = isPrimary ? Color.FromArgb(20, 138, 230) : Color.FromArgb(66, 66, 66);
        return btn;
    }

    private static Color SafeParseHexColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return ColorTranslator.FromHtml(hex);
        }
        catch { }
        return fallback;
    }

    private Panel CreateCompactColorSwatch(int left, int top, int width, int height, string initialHex, Action<string> onColorPicked)
    {
        Panel swatch = new Panel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BackColor = SafeParseHexColor(initialHex, Color.FromArgb(27, 59, 90)),
            Cursor = Cursors.Hand
        };

        swatch.Paint += (s, e) =>
        {
            using Pen borderPen = new Pen(Color.FromArgb(110, 110, 110), 1);
            e.Graphics.DrawRectangle(borderPen, 0, 0, swatch.Width - 1, swatch.Height - 1);
        };

        swatch.Click += (s, e) =>
        {
            using ColorDialog dlg = new ColorDialog { FullOpen = true };
            try { dlg.Color = swatch.BackColor; } catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                swatch.BackColor = dlg.Color;
                string hex = ColorTranslator.ToHtml(dlg.Color);
                onColorPicked(hex);
                MarkDirty();
                PushLivePreviewIfActive();
            }
        };

        return swatch;
    }

    private Bitmap? ResolveDropdownIcon(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return null;
        return AppIconCache.GetIconForSource(item, _draftSettings);
    }

    private BadgeInfo ResolveDropdownBadge(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return new BadgeInfo("", Color.Transparent);
        if (item.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
            return new BadgeInfo("GROUP", Color.FromArgb(180, 140, 255));
        if (_detectedAudio.PlayingAudio.Contains(item))
            return new BadgeInfo("PLAYING AUDIO", Color.FromArgb(75, 220, 130));
        if (_detectedAudio.ActiveAudioSessions.Contains(item))
            return new BadgeInfo("AUDIO MIXER", Color.FromArgb(80, 185, 255));
        if (_draftSettings.RecentPrograms.Contains(item, StringComparer.OrdinalIgnoreCase))
            return new BadgeInfo("RECENT", Color.FromArgb(210, 175, 95));
        if (_detectedAudio.RunningWindowedApps.Contains(item))
            return new BadgeInfo("RUNNING APP", Color.FromArgb(160, 160, 160));

        return new BadgeInfo("", Color.Transparent);
    }

    private void BrowseForExecutable(Control targetControl)
    {
        using OpenFileDialog dlg = new OpenFileDialog
        {
            Title = "Select an Application (.exe)",
            Filter = "Applications (*.exe)|*.exe|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            string exeName = AppSettings.NormalizeExeName(dlg.FileName);
            if (!string.IsNullOrEmpty(exeName))
            {
                _draftSettings.RecordProgramHistory(exeName, dlg.FileName);
                RefreshAudioDetectionAndDropdowns();
                targetControl.Text = exeName;
                MarkDirty();
            }
        }
    }

    private void RefreshAudioDetectionAndDropdowns()
    {
        _detectedAudio = AudioProcessDetector.Scan();
        UpdateSourceDropdownOptions();
    }

    private DarkScrollPanel CreateGeneralPage()
    {
        DarkScrollPanel page = new DarkScrollPanel { Dock = DockStyle.Fill };

        // Card 1: Mode & Active Audio Sources
        _sourcesCard = new Win11CardPanel
        {
            Left = 18,
            Top = 4,
            Width = 468
        };

        int cy = 10;
        AddSectionHeader(_sourcesCard, "Mode & Active Audio Sources", 14, ref cy);

        AddSubLabel(_sourcesCard, "Operation Mode", 14, ref cy);
        _cmbMode = new ModernComboBox
        {
            Left = 14,
            Top = cy,
            Width = 440,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbMode.Items.AddRange(new object[] { "Crossfade", "Volume Control" });
        _cmbMode.SelectedItem = _draftSettings.OperationMode;
        if (_cmbMode.SelectedIndex == -1) _cmbMode.SelectedIndex = 0;
        _cmbMode.SelectedIndexChanged += (s, e) =>
        {
            if (!_isInitializing)
            {
                UpdateContextUI(true);
                MarkDirty();
                PushLivePreviewIfActive();
            }
        };
        _sourcesCard.Controls.Add(_cmbMode);
        cy += 30;

        List<string> options = GetAvailableSourceOptions();

        AddSubLabel(_sourcesCard, "Active Source 1 (Color swatch + Group/App + Browse)", 14, ref cy);
        var s1Row = AddColoredSourcePickerRow(
            _sourcesCard,
            options,
            _draftSettings.Source1,
            _draftSettings.Source1Color,
            hex => _draftSettings.Source1Color = hex,
            14,
            440,
            ref cy);
        _cmbSource1 = s1Row.cmb;
        _swatchSource1 = s1Row.swatch;

        _source2Container = new Panel
        {
            Left = 14,
            Top = cy,
            Width = 440,
            Height = 50,
            BackColor = Color.Transparent
        };
        int s2y = 0;
        AddSubLabel(_source2Container, "Active Source 2 / Crossfade Target", 0, ref s2y);
        var s2Row = AddColoredSourcePickerRow(
            _source2Container,
            options,
            _draftSettings.Source2,
            _draftSettings.Source2Color,
            hex => _draftSettings.Source2Color = hex,
            0,
            440,
            ref s2y);
        _cmbSource2 = s2Row.cmb;
        _swatchSource2 = s2Row.swatch;
        _sourcesCard.Controls.Add(_source2Container);

        _sourcesCard.FitHeightToContent(12);
        page.InnerControls.Add(_sourcesCard);

        // Card 2: Hotkeys & Key Combinations
        _keysCard = new Win11CardPanel
        {
            Left = 18,
            Top = _sourcesCard.Bottom + 10,
            Width = 468
        };

        int ky = 10;
        AddSectionHeader(_keysCard, "Hotkeys & Key Combinations", 14, ref ky);

        _lblKey1 = AddAlignedLabel(_keysCard, "Key 1:", 14, ky);
        var r1 = AddKeyBindControls(_keysCard, ref ky, null);
        _txtKey1 = r1.txt;
        _btnRec1 = r1.btn;

        _lblKey2 = AddAlignedLabel(_keysCard, "Key 2:", 14, ky);
        var r2 = AddKeyBindControls(_keysCard, ref ky, null);
        _txtKey2 = r2.txt;
        _btnRec2 = r2.btn;

        _lblKey3 = AddAlignedLabel(_keysCard, "Key 3:", 14, ky);
        var r3 = AddKeyBindControls(_keysCard, ref ky, null);
        _txtKey3 = r3.txt;
        _btnRec3 = r3.btn;

        _lblKeyCycleNext = AddAlignedLabel(_keysCard, "Cycle Next Pair:", 14, ky);
        var rNext = AddKeyBindControls(_keysCard, ref ky, val =>
        {
            _draftSettings.CycleNextKey = val;
            _draftSettings.QuickSwapKey = val;
        }, allowClear: true);
        _txtKeyCycleNext = rNext.txt;
        _btnRecCycleNext = rNext.btn;

        _lblKeyCyclePrev = AddAlignedLabel(_keysCard, "Cycle Prev Pair:", 14, ky);
        var rPrev = AddKeyBindControls(_keysCard, ref ky, val => _draftSettings.CyclePrevKey = val, allowClear: true);
        _txtKeyCyclePrev = rPrev.txt;
        _btnRecCyclePrev = rPrev.btn;

        _keysCard.FitHeightToContent(12);
        page.InnerControls.Add(_keysCard);

        // Card 3: Mode-Specific Source Cycle List
        _cycleListCard = new Win11CardPanel
        {
            Left = 18,
            Top = _keysCard.Bottom + 10,
            Width = 468,
            Height = 44
        };

        _lblCycleCardTitle = new Label
        {
            Text = "Crossfade Source Pairs (Cycle List)",
            Left = 14,
            Top = 12,
            Width = 290,
            Height = 20,
            Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 205, 255)
        };
        _cycleListCard.Controls.Add(_lblCycleCardTitle);

        _btnAddCycleEntry = CreateModernButton("+ Add Source Pair", 312, 9, 142, 26, true);
        _btnAddCycleEntry.Click += (s, e) =>
        {
            bool isVolMode = (_cmbMode.SelectedItem?.ToString() == "Volume Control");
            if (isVolMode)
            {
                _draftSettings.VolumeCycleList.Add(new VolumeCycleEntry
                {
                    Source = "",
                    ColorHex = "#1B3B5A"
                });
            }
            else
            {
                _draftSettings.CrossfadeCycleList.Add(new CrossfadeCyclePair
                {
                    Source1 = "",
                    Color1Hex = "#1B3B5A",
                    Source2 = "",
                    Color2Hex = "#4A2040"
                });
            }
            PopulateCycleListUI();
            ReflowGeneralPage();
            MarkDirty();
        };
        _cycleListCard.Controls.Add(_btnAddCycleEntry);

        _cycleSlotsHost = new Panel
        {
            Left = 12,
            Top = 42,
            Width = 444,
            Height = 0,
            BackColor = Color.Transparent
        };
        _cycleListCard.Controls.Add(_cycleSlotsHost);
        page.InnerControls.Add(_cycleListCard);

        // Card 4: Behavior & Startup
        _behaviorCard = new Win11CardPanel
        {
            Left = 18,
            Top = _cycleListCard.Bottom + 10,
            Width = 468
        };

        int by = 10;
        AddSectionHeader(_behaviorCard, "Step & System Behavior", 14, ref by);

        Label lblStep = new Label
        {
            Text = "Step Size (%, 1 - 100):",
            Left = 14,
            Top = by + 3,
            Width = 150,
            ForeColor = Color.FromArgb(205, 205, 205)
        };
        _behaviorCard.Controls.Add(lblStep);

        _txtStepSize = new TextBox
        {
            Text = _draftSettings.StepSize.ToString(),
            Left = 170,
            Top = by,
            Width = 100,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtStepSize.TextChanged += (s, e) => MarkDirty();
        _behaviorCard.Controls.Add(_txtStepSize);
        by += 30;

        _chkStartup = new CheckBox
        {
            Text = "Run on Windows Startup",
            Checked = _draftSettings.RunOnStartup,
            Left = 14,
            Top = by,
            Width = 430,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkStartup.CheckedChanged += (s, e) => MarkDirty();
        _behaviorCard.Controls.Add(_chkStartup);
        by += 26;

        _chkBuffer = new CheckBox
        {
            Text = "Enable 100% Equalization Center Detent (Crossfade)",
            Checked = _draftSettings.EnableEqualizationBuffer,
            Left = 14,
            Top = by,
            Width = 430,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkBuffer.CheckedChanged += (s, e) => MarkDirty();
        _behaviorCard.Controls.Add(_chkBuffer);

        _behaviorCard.FitHeightToContent(12);
        page.InnerControls.Add(_behaviorCard);
        return page;
    }

    private static string ReadComboText(ModernComboBox cmb)
    {
        return (cmb.SelectedItem?.ToString() ?? cmb.Text ?? "").Trim();
    }

    private void PopulateCycleListUI()
    {
        if (_cycleSlotsHost == null) return;
        _cycleSlotsHost.SuspendLayout();
        _cycleSlotsHost.Controls.Clear();

        bool isVolumeControl = (_cmbMode?.SelectedItem?.ToString() == "Volume Control");
        List<string> options = GetAvailableSourceOptions();
        int y = 0;

        if (isVolumeControl)
        {
            for (int i = 0; i < _draftSettings.VolumeCycleList.Count; i++)
            {
                var entry = _draftSettings.VolumeCycleList[i];
                Win11CardPanel rowCard = new Win11CardPanel
                {
                    Left = 0,
                    Top = y,
                    Width = 442,
                    Height = 40,
                    FillColor = Color.FromArgb(48, 48, 48),
                    BorderColor = Color.FromArgb(66, 66, 66),
                    CornerRadius = 6
                };

                Panel swatch = CreateCompactColorSwatch(8, 7, 26, 24, entry.ColorHex, hex => entry.ColorHex = hex);
                rowCard.Controls.Add(swatch);

                ModernComboBox cmb = new ModernComboBox
                {
                    Left = 40,
                    Top = 7,
                    Width = 276,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    BadgeResolver = ResolveDropdownBadge,
                    IconResolver = ResolveDropdownIcon
                };
                cmb.Items.AddRange(options.Cast<object>().ToArray());
                cmb.SelectedItem = entry.Source;
                if (cmb.SelectedIndex == -1) cmb.Text = entry.Source;
                cmb.DropDown += (s, e) =>
                {
                    _detectedAudio = AudioProcessDetector.Scan();
                    PopulateSingleComboOptions(cmb, includeGroups: true);
                };
                cmb.SelectedIndexChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    entry.Source = ReadComboText(cmb);
                    MarkDirty();
                };
                cmb.TextChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    entry.Source = ReadComboText(cmb);
                    MarkDirty();
                };
                rowCard.Controls.Add(cmb);

                Button btnBrowse = CreateModernButton("Browse", 322, 6, 74, 25, false);
                btnBrowse.Click += (s, e) =>
                {
                    BrowseForExecutable(cmb);
                    entry.Source = ReadComboText(cmb);
                };
                rowCard.Controls.Add(btnBrowse);

                Button btnDel = CreateModernButton("✕", 400, 6, 34, 25, false);
                btnDel.Click += (s, e) =>
                {
                    _draftSettings.VolumeCycleList.Remove(entry);
                    PopulateCycleListUI();
                    ReflowGeneralPage();
                    MarkDirty();
                };
                rowCard.Controls.Add(btnDel);

                _cycleSlotsHost.Controls.Add(rowCard);
                y += 46;
            }
        }
        else
        {
            for (int i = 0; i < _draftSettings.CrossfadeCycleList.Count; i++)
            {
                var pair = _draftSettings.CrossfadeCycleList[i];
                Win11CardPanel pairCard = new Win11CardPanel
                {
                    Left = 0,
                    Top = y,
                    Width = 442,
                    Height = 72,
                    FillColor = Color.FromArgb(48, 48, 48),
                    BorderColor = Color.FromArgb(66, 66, 66),
                    CornerRadius = 6
                };

                // Row 1: Left / Source 1
                Label lblS1 = new Label
                {
                    Text = "L:",
                    Left = 8,
                    Top = 10,
                    Width = 18,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(190, 190, 190)
                };
                pairCard.Controls.Add(lblS1);

                Panel swatch1 = CreateCompactColorSwatch(28, 7, 24, 24, pair.Color1Hex, hex => pair.Color1Hex = hex);
                pairCard.Controls.Add(swatch1);

                ModernComboBox cmb1 = new ModernComboBox
                {
                    Left = 58,
                    Top = 7,
                    Width = 258,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    BadgeResolver = ResolveDropdownBadge,
                    IconResolver = ResolveDropdownIcon
                };
                cmb1.Items.AddRange(options.Cast<object>().ToArray());
                cmb1.SelectedItem = pair.Source1;
                if (cmb1.SelectedIndex == -1) cmb1.Text = pair.Source1;
                cmb1.DropDown += (s, e) =>
                {
                    _detectedAudio = AudioProcessDetector.Scan();
                    PopulateSingleComboOptions(cmb1, includeGroups: true);
                };
                cmb1.SelectedIndexChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    pair.Source1 = ReadComboText(cmb1);
                    MarkDirty();
                };
                cmb1.TextChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    pair.Source1 = ReadComboText(cmb1);
                    MarkDirty();
                };
                pairCard.Controls.Add(cmb1);

                Button btnBrowse1 = CreateModernButton("Browse", 322, 6, 74, 25, false);
                btnBrowse1.Click += (s, e) =>
                {
                    BrowseForExecutable(cmb1);
                    pair.Source1 = ReadComboText(cmb1);
                };
                pairCard.Controls.Add(btnBrowse1);

                Button btnDel = CreateModernButton("✕", 400, 6, 34, 57, false);
                btnDel.Click += (s, e) =>
                {
                    _draftSettings.CrossfadeCycleList.Remove(pair);
                    PopulateCycleListUI();
                    ReflowGeneralPage();
                    MarkDirty();
                };
                pairCard.Controls.Add(btnDel);

                // Row 2: Right / Source 2
                Label lblS2 = new Label
                {
                    Text = "R:",
                    Left = 8,
                    Top = 42,
                    Width = 18,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(190, 190, 190)
                };
                pairCard.Controls.Add(lblS2);

                Panel swatch2 = CreateCompactColorSwatch(28, 39, 24, 24, pair.Color2Hex, hex => pair.Color2Hex = hex);
                pairCard.Controls.Add(swatch2);

                ModernComboBox cmb2 = new ModernComboBox
                {
                    Left = 58,
                    Top = 39,
                    Width = 258,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    BadgeResolver = ResolveDropdownBadge,
                    IconResolver = ResolveDropdownIcon
                };
                cmb2.Items.AddRange(options.Cast<object>().ToArray());
                cmb2.SelectedItem = pair.Source2;
                if (cmb2.SelectedIndex == -1) cmb2.Text = pair.Source2;
                cmb2.DropDown += (s, e) =>
                {
                    _detectedAudio = AudioProcessDetector.Scan();
                    PopulateSingleComboOptions(cmb2, includeGroups: true);
                };
                cmb2.SelectedIndexChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    pair.Source2 = ReadComboText(cmb2);
                    MarkDirty();
                };
                cmb2.TextChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    pair.Source2 = ReadComboText(cmb2);
                    MarkDirty();
                };
                pairCard.Controls.Add(cmb2);

                Button btnBrowse2 = CreateModernButton("Browse", 322, 38, 74, 25, false);
                btnBrowse2.Click += (s, e) =>
                {
                    BrowseForExecutable(cmb2);
                    pair.Source2 = ReadComboText(cmb2);
                };
                pairCard.Controls.Add(btnBrowse2);

                _cycleSlotsHost.Controls.Add(pairCard);
                y += 78;
            }
        }

        _cycleSlotsHost.Height = y;
        _cycleSlotsHost.ResumeLayout();
    }

    private void ReflowGeneralPage()
    {
        if (_sourcesCard == null || _keysCard == null || _cycleListCard == null || _behaviorCard == null)
            return;

        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");

        _source2Container.Visible = !isVolumeControl;
        _sourcesCard.FitHeightToContent(12);

        _keysCard.FitHeightToContent(12);
        _keysCard.Top = _sourcesCard.Bottom + 10;

        _lblCycleCardTitle.Text = isVolumeControl
            ? "Volume Sources (Cycle List)"
            : "Crossfade Source Pairs (Cycle List)";
        _btnAddCycleEntry.Text = isVolumeControl
            ? "+ Add Source"
            : "+ Add Source Pair";

        int itemCount = isVolumeControl
            ? _draftSettings.VolumeCycleList.Count
            : _draftSettings.CrossfadeCycleList.Count;

        _cycleSlotsHost.Visible = (itemCount > 0);
        _cycleListCard.Top = _keysCard.Bottom + 10;
        _cycleListCard.Height = (itemCount == 0) ? 44 : (46 + _cycleSlotsHost.Height);

        _chkBuffer.Visible = !isVolumeControl;
        _behaviorCard.FitHeightToContent(12);
        _behaviorCard.Top = _cycleListCard.Bottom + 10;

        _pageGeneral?.RecalculateScroll();
    }

    private DarkScrollPanel CreateGroupsPage()
    {
        DarkScrollPanel page = new DarkScrollPanel { Dock = DockStyle.Fill };
        return page;
    }

    private static float ParseFloatFlexible(string text, float defaultVal)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultVal;
        string normalized = text.Trim().Replace(',', '.');
        return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) ? val : defaultVal;
    }

    private void SyncDraftPersonalizationFields()
    {
        if (_isInitializing) return;
        int ParseInt(string text, int defaultVal) => int.TryParse(text, out int val) ? val : defaultVal;

        _draftSettings.Source1 = ReadComboText(_cmbSource1);
        _draftSettings.Source2 = ReadComboText(_cmbSource2);
        _draftSettings.OverlayPosition = _cmbPosition?.SelectedItem?.ToString() ?? _draftSettings.OverlayPosition;
        _draftSettings.PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration?.Text ?? "2500", 2500), 500, 10000);
        _draftSettings.OverlayWidth = Math.Clamp(ParseInt(_txtWidth?.Text ?? "360", 360), 120, 2000);
        _draftSettings.OverlayHeight = Math.Clamp(ParseInt(_txtHeight?.Text ?? "80", 80), 50, 1000);
        _draftSettings.EnableInterpolation = _chkInterpolation?.Checked ?? true;
        _draftSettings.InterpolationSpeed = Math.Clamp(ParseInt(_txtInterpSpeed?.Text ?? "45", 45), 5, 100);
        _draftSettings.CarouselVisibleNeighbors = Math.Clamp(ParseFloatFlexible(_txtCarouselNeighbors?.Text ?? "1.0", 1.0f), 0.5f, 2.5f);
        _draftSettings.CarouselBlendColors = _chkCarouselBlendColors?.Checked ?? true;
    }

    private void PushLivePreviewIfActive(bool forceTrigger = false)
    {
        if (_isInitializing) return;
        SyncDraftPersonalizationFields();
        if (forceTrigger || _draftSettings.KeepOverlayOnWhileSettingsOpen)
        {
            _context.TriggerOverlayPreview(_draftSettings);
        }
    }

    private DarkScrollPanel CreatePersonalizationPage()
    {
        DarkScrollPanel page = new DarkScrollPanel { Dock = DockStyle.Fill };
        int y = 4;

        // Card 0: Live Overlay Preview & Always-On while Settings Open
        Win11CardPanel previewCard = new Win11CardPanel
        {
            Left = 18,
            Top = y,
            Width = 468
        };

        int py = 10;
        AddSectionHeader(previewCard, "Live Overlay Preview", 14, ref py);

        Button btnShowPreview = CreateModernButton("Show Overlay Preview", 14, py, 195, 29, true);
        btnShowPreview.Click += (s, e) => PushLivePreviewIfActive(forceTrigger: true);
        previewCard.Controls.Add(btnShowPreview);
        py += 36;

        _chkKeepOverlayAlwaysOn = new CheckBox
        {
            Text = "Keep Overlay Always Visible While Settings Window is Open",
            Checked = _draftSettings.KeepOverlayOnWhileSettingsOpen,
            Left = 14,
            Top = py,
            Width = 435,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkKeepOverlayAlwaysOn.CheckedChanged += (s, e) =>
        {
            _draftSettings.KeepOverlayOnWhileSettingsOpen = _chkKeepOverlayAlwaysOn.Checked;
            MarkDirty();
            _context.NotifySettingsWindowState(true, _draftSettings);
        };
        previewCard.Controls.Add(_chkKeepOverlayAlwaysOn);

        previewCard.FitHeightToContent(12);
        page.InnerControls.Add(previewCard);
        y = previewCard.Bottom + 10;

        // Card 1: Display, Background Flair & Carousel Settings
        Win11CardPanel contentStyleCard = new Win11CardPanel
        {
            Left = 18,
            Top = y,
            Width = 468
        };

        int cy = 10;
        AddSectionHeader(contentStyleCard, "Overlay Content, Flair & Carousel", 14, ref cy);

        Label lblIdMode = new Label
        {
            Text = "Overlay App Identity:",
            Left = 14,
            Top = cy + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        contentStyleCard.Controls.Add(lblIdMode);

        _cmbIdentityMode = new ModernComboBox
        {
            Left = 185,
            Top = cy,
            Width = 265,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbIdentityMode.Items.AddRange(new object[] { "Both", "Name", "Icon" });
        _cmbIdentityMode.SelectedItem = _draftSettings.OverlayIdentityMode;
        if (_cmbIdentityMode.SelectedIndex == -1) _cmbIdentityMode.SelectedIndex = 0;
        _cmbIdentityMode.SelectedIndexChanged += (s, e) =>
        {
            _draftSettings.OverlayIdentityMode = _cmbIdentityMode.SelectedItem?.ToString() ?? "Both";
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_cmbIdentityMode);
        cy += 30;

        Label lblLvlMode = new Label
        {
            Text = "Overlay Volume Readout:",
            Left = 14,
            Top = cy + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        contentStyleCard.Controls.Add(lblLvlMode);

        _cmbLevelMode = new ModernComboBox
        {
            Left = 185,
            Top = cy,
            Width = 265,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbLevelMode.Items.AddRange(new object[] { "Both", "Bar", "Percentage" });
        _cmbLevelMode.SelectedItem = _draftSettings.OverlayLevelMode;
        if (_cmbLevelMode.SelectedIndex == -1) _cmbLevelMode.SelectedIndex = 0;
        _cmbLevelMode.SelectedIndexChanged += (s, e) =>
        {
            _draftSettings.OverlayLevelMode = _cmbLevelMode.SelectedItem?.ToString() ?? "Both";
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_cmbLevelMode);
        cy += 30;

        _chkUseSourceBgColors = new CheckBox
        {
            Text = "Tint Overlay Background with Assigned Source Colors",
            Checked = _draftSettings.UseSourceBackgroundColors,
            Left = 14,
            Top = cy + 2,
            Width = 440,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkUseSourceBgColors.CheckedChanged += (s, e) =>
        {
            _draftSettings.UseSourceBackgroundColors = _chkUseSourceBgColors.Checked;
            if (_chkFadeBgWithVolume != null) _chkFadeBgWithVolume.Enabled = _chkUseSourceBgColors.Checked;
            if (_cmbSingleSourceFillMode != null) _cmbSingleSourceFillMode.Enabled = _chkUseSourceBgColors.Checked;
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_chkUseSourceBgColors);
        cy += 26;

        _chkFadeBgWithVolume = new CheckBox
        {
            Text = "Fade Background Color to Dark Grey as Volume Lowers / Mutes",
            Checked = _draftSettings.FadeBgColorWithVolume,
            Enabled = _draftSettings.UseSourceBackgroundColors,
            Left = 14,
            Top = cy + 2,
            Width = 440,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkFadeBgWithVolume.CheckedChanged += (s, e) =>
        {
            _draftSettings.FadeBgColorWithVolume = _chkFadeBgWithVolume.Checked;
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_chkFadeBgWithVolume);
        cy += 28;

        Label lblSingleFill = new Label
        {
            Text = "Single-Source Color Fill:",
            Left = 14,
            Top = cy + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        contentStyleCard.Controls.Add(lblSingleFill);

        _cmbSingleSourceFillMode = new ModernComboBox
        {
            Left = 185,
            Top = cy,
            Width = 265,
            Enabled = _draftSettings.UseSourceBackgroundColors,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbSingleSourceFillMode.Items.AddRange(new object[] { "Solid Fill", "Gradient to Dark Grey" });
        _cmbSingleSourceFillMode.SelectedItem = _draftSettings.SingleSourceBgFillMode;
        if (_cmbSingleSourceFillMode.SelectedIndex == -1) _cmbSingleSourceFillMode.SelectedIndex = 0;
        _cmbSingleSourceFillMode.SelectedIndexChanged += (s, e) =>
        {
            _draftSettings.SingleSourceBgFillMode = _cmbSingleSourceFillMode.SelectedItem?.ToString() ?? "Solid Fill";
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_cmbSingleSourceFillMode);
        cy += 32;

        Label lblCarouselRange = new Label
        {
            Text = "Carousel Extension (0.5 - 2.5 items):",
            Left = 14,
            Top = cy + 3,
            Width = 220,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        contentStyleCard.Controls.Add(lblCarouselRange);

        _txtCarouselNeighbors = new TextBox
        {
            Text = _draftSettings.CarouselVisibleNeighbors.ToString("0.0", CultureInfo.InvariantCulture),
            Left = 240,
            Top = cy,
            Width = 80,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtCarouselNeighbors.TextChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_txtCarouselNeighbors);
        cy += 28;

        _chkCarouselBlendColors = new CheckBox
        {
            Text = "Smoothly Fade Colors Between Items in Cycle Carousel",
            Checked = _draftSettings.CarouselBlendColors,
            Left = 14,
            Top = cy + 2,
            Width = 440,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkCarouselBlendColors.CheckedChanged += (s, e) =>
        {
            _draftSettings.CarouselBlendColors = _chkCarouselBlendColors.Checked;
            MarkDirty();
            PushLivePreviewIfActive();
        };
        contentStyleCard.Controls.Add(_chkCarouselBlendColors);

        contentStyleCard.FitHeightToContent(12);
        page.InnerControls.Add(contentStyleCard);
        y = contentStyleCard.Bottom + 10;

        // Card 2: Overlay Colors, Position, Size & Interpolation Speed
        Win11CardPanel appearanceCard = new Win11CardPanel
        {
            Left = 18,
            Top = y,
            Width = 468
        };

        int ay = 10;
        AddSectionHeader(appearanceCard, "Overlay Colors, Position & Animation", 14, ref ay);

        _unmutedColorBox = AddColorPickerRow(appearanceCard, "Unmuted Bar Color:", _draftSettings.UnmutedColor, (hex) =>
        {
            _draftSettings.UnmutedColor = hex;
            MarkDirty();
            PushLivePreviewIfActive();
        }, ref ay);

        _mutedColorBox = AddColorPickerRow(appearanceCard, "Muted Bar Color:", _draftSettings.MutedColor, (hex) =>
        {
            _draftSettings.MutedColor = hex;
            MarkDirty();
            PushLivePreviewIfActive();
        }, ref ay);

        Label lblPos = new Label
        {
            Text = "Overlay Screen Position:",
            Left = 14,
            Top = ay + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        appearanceCard.Controls.Add(lblPos);

        _cmbPosition = new ModernComboBox
        {
            Left = 185,
            Top = ay,
            Width = 265,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbPosition.Items.AddRange(new object[] { "Bottom Center", "Top Center", "Bottom Right", "Bottom Left", "Top Right", "Top Left" });
        _cmbPosition.SelectedItem = _draftSettings.OverlayPosition;
        if (_cmbPosition.SelectedIndex == -1) _cmbPosition.SelectedIndex = 0;
        _cmbPosition.SelectedIndexChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        appearanceCard.Controls.Add(_cmbPosition);
        ay += 30;

        Label lblDur = new Label
        {
            Text = "Popup Duration (ms):",
            Left = 14,
            Top = ay + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        appearanceCard.Controls.Add(lblDur);

        _txtPopupDuration = new TextBox
        {
            Text = _draftSettings.PopupDurationMs.ToString(),
            Left = 185,
            Top = ay,
            Width = 110,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtPopupDuration.TextChanged += (s, e) => MarkDirty();
        appearanceCard.Controls.Add(_txtPopupDuration);
        ay += 28;

        Label lblSize = new Label
        {
            Text = "Overlay Size (Width × Height):",
            Left = 14,
            Top = ay + 3,
            Width = 170,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        appearanceCard.Controls.Add(lblSize);

        _txtWidth = new TextBox
        {
            Text = _draftSettings.OverlayWidth.ToString(),
            Left = 185,
            Top = ay,
            Width = 80,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtWidth.TextChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        appearanceCard.Controls.Add(_txtWidth);

        Label lblBy = new Label
        {
            Text = "×",
            Left = 270,
            Top = ay + 3,
            Width = 16,
            ForeColor = Color.FromArgb(180, 180, 180)
        };
        appearanceCard.Controls.Add(lblBy);

        _txtHeight = new TextBox
        {
            Text = _draftSettings.OverlayHeight.ToString(),
            Left = 288,
            Top = ay,
            Width = 80,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtHeight.TextChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        appearanceCard.Controls.Add(_txtHeight);
        ay += 30;

        _chkInterpolation = new CheckBox
        {
            Text = "Overlay Bar & Color Interpolation",
            Checked = _draftSettings.EnableInterpolation,
            Left = 14,
            Top = ay + 2,
            Width = 222,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkInterpolation.CheckedChanged += (s, e) =>
        {
            if (_txtInterpSpeed != null) _txtInterpSpeed.Enabled = _chkInterpolation.Checked;
            MarkDirty();
            PushLivePreviewIfActive();
        };
        appearanceCard.Controls.Add(_chkInterpolation);

        Label lblSpeed = new Label
        {
            Text = "Speed (5-100):",
            Left = 240,
            Top = ay + 2,
            Width = 92,
            Height = 20,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        appearanceCard.Controls.Add(lblSpeed);

        _txtInterpSpeed = new TextBox
        {
            Text = _draftSettings.InterpolationSpeed.ToString(),
            Left = lblSpeed.Right + 4,
            Top = ay + 1,
            Width = 58,
            Enabled = _draftSettings.EnableInterpolation,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _txtInterpSpeed.TextChanged += (s, e) => MarkDirty();
        appearanceCard.Controls.Add(_txtInterpSpeed);

        appearanceCard.FitHeightToContent(12);
        page.InnerControls.Add(appearanceCard);
        page.RecalculateScroll();
        return page;
    }

    private (ModernComboBox cmb, Panel swatch) AddColoredSourcePickerRow(
        Control parent,
        List<string> items,
        string selectedValue,
        string initialColorHex,
        Action<string> onColorChanged,
        int left,
        int totalWidth,
        ref int top)
    {
        int swatchWidth = 26;
        int browseWidth = 84;
        int comboWidth = totalWidth - swatchWidth - browseWidth - 12;

        Panel swatch = CreateCompactColorSwatch(left, top, swatchWidth, 24, initialColorHex, onColorChanged);
        parent.Controls.Add(swatch);

        ModernComboBox cmb = new ModernComboBox
        {
            Left = left + swatchWidth + 6,
            Top = top,
            Width = comboWidth,
            DropDownStyle = ComboBoxStyle.DropDown,
            BadgeResolver = ResolveDropdownBadge,
            IconResolver = ResolveDropdownIcon
        };
        cmb.Items.AddRange(items.Cast<object>().ToArray());
        cmb.SelectedItem = selectedValue;
        if (cmb.SelectedIndex == -1) cmb.Text = selectedValue;

        cmb.DropDown += (s, e) =>
        {
            _detectedAudio = AudioProcessDetector.Scan();
            PopulateSingleComboOptions(cmb, includeGroups: true);
        };
        cmb.SelectedIndexChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        cmb.TextChanged += (s, e) =>
        {
            MarkDirty();
            PushLivePreviewIfActive();
        };
        parent.Controls.Add(cmb);

        Button btnBrowse = CreateModernButton("Browse...", cmb.Right + 6, top - 1, browseWidth, 25, false);
        btnBrowse.Click += (s, e) => BrowseForExecutable(cmb);
        parent.Controls.Add(btnBrowse);

        top += 30;
        return (cmb, swatch);
    }

    private void PopulateSingleComboOptions(ModernComboBox cmb, bool includeGroups)
    {
        string currentText = ReadComboText(cmb);
        var list = includeGroups ? GetAvailableSourceOptions() : GetExecutableSuggestions();
        _isInitializing = true;
        cmb.Items.Clear();
        cmb.Items.AddRange(list.Cast<object>().ToArray());
        cmb.SelectedItem = currentText;
        if (cmb.SelectedIndex == -1) cmb.Text = currentText;
        _isInitializing = false;
    }

    private void MarkDirty()
    {
        if (_isInitializing) return;
        if (_lblStatus != null)
        {
            _lblStatus.ForeColor = Color.FromArgb(255, 200, 90);
            _lblStatus.Text = "● Unsaved changes";
        }
    }

    private void MarkClean()
    {
        if (_lblStatus != null)
        {
            _lblStatus.ForeColor = Color.FromArgb(95, 215, 135);
            _lblStatus.Text = "✓ All settings saved";
        }
    }

    private void LoadSettingsIntoUI(AppSettings settings)
    {
        _detectedAudio = AudioProcessDetector.Scan();
        RefreshGroupsTabUI();

        _cmbMode.SelectedItem = settings.OperationMode;

        _cmbSource1.SelectedItem = settings.Source1;
        if (!_cmbSource1.Items.Contains(settings.Source1)) _cmbSource1.Text = settings.Source1;
        if (_swatchSource1 != null) _swatchSource1.BackColor = SafeParseHexColor(settings.Source1Color, Color.FromArgb(27, 59, 90));

        _cmbSource2.SelectedItem = settings.Source2;
        if (!_cmbSource2.Items.Contains(settings.Source2)) _cmbSource2.Text = settings.Source2;
        if (_swatchSource2 != null) _swatchSource2.BackColor = SafeParseHexColor(settings.Source2Color, Color.FromArgb(74, 32, 64));

        PopulateCycleListUI();

        bool isVolumeControl = (settings.OperationMode == "Volume Control");
        int k1 = isVolumeControl ? settings.VolKeyUp : settings.CrossfadeKeyUp;
        int k2 = isVolumeControl ? settings.VolKeyDown : settings.CrossfadeKeyDown;
        int k3 = isVolumeControl ? settings.VolKeyMute : settings.CrossfadeKeyMute;

        _txtKey1.Text = KeybindHelper.FormatKeybind(k1);
        _txtKey1.Tag = k1;

        _txtKey2.Text = KeybindHelper.FormatKeybind(k2);
        _txtKey2.Tag = k2;

        _txtKey3.Text = KeybindHelper.FormatKeybind(k3);
        _txtKey3.Tag = k3;

        int nextKey = settings.GetEffectiveCycleNextKey();
        _txtKeyCycleNext.Text = KeybindHelper.FormatKeybind(nextKey);
        _txtKeyCycleNext.Tag = nextKey;

        _txtKeyCyclePrev.Text = KeybindHelper.FormatKeybind(settings.CyclePrevKey);
        _txtKeyCyclePrev.Tag = settings.CyclePrevKey;

        _txtStepSize.Text = settings.StepSize.ToString();
        _chkBuffer.Checked = settings.EnableEqualizationBuffer;
        _chkStartup.Checked = settings.RunOnStartup;

        _chkKeepOverlayAlwaysOn.Checked = settings.KeepOverlayOnWhileSettingsOpen;
        _cmbIdentityMode.SelectedItem = settings.OverlayIdentityMode;
        _cmbLevelMode.SelectedItem = settings.OverlayLevelMode;
        _chkUseSourceBgColors.Checked = settings.UseSourceBackgroundColors;
        _chkFadeBgWithVolume.Checked = settings.FadeBgColorWithVolume;
        _chkFadeBgWithVolume.Enabled = settings.UseSourceBackgroundColors;
        _cmbSingleSourceFillMode.SelectedItem = settings.SingleSourceBgFillMode;
        _cmbSingleSourceFillMode.Enabled = settings.UseSourceBackgroundColors;
        _txtCarouselNeighbors.Text = settings.CarouselVisibleNeighbors.ToString("0.0", CultureInfo.InvariantCulture);
        _chkCarouselBlendColors.Checked = settings.CarouselBlendColors;

        if (_unmutedColorBox != null)
            _unmutedColorBox.BackColor = SafeParseHexColor(settings.UnmutedColor, Color.FromArgb(0, 168, 255));
        if (_mutedColorBox != null)
            _mutedColorBox.BackColor = SafeParseHexColor(settings.MutedColor, Color.FromArgb(255, 71, 87));

        _cmbPosition.SelectedItem = settings.OverlayPosition;
        _txtPopupDuration.Text = settings.PopupDurationMs.ToString();
        _txtWidth.Text = settings.OverlayWidth.ToString();
        _txtHeight.Text = settings.OverlayHeight.ToString();
        _chkInterpolation.Checked = settings.EnableInterpolation;
        _txtInterpSpeed.Text = settings.InterpolationSpeed.ToString();
        _txtInterpSpeed.Enabled = settings.EnableInterpolation;

        ReflowGeneralPage();
    }

    private void RevertChanges()
    {
        _isInitializing = true;
        _draftSettings = _savedSettingsSnapshot.Clone();
        LoadSettingsIntoUI(_draftSettings);
        UpdateContextUI(false);
        _isInitializing = false;
        MarkClean();
        _context.NotifySettingsWindowState(true, _draftSettings);
    }

    private void RefreshGroupsTabUI()
    {
        PopulateGroupsFlowContent();
        UpdateSourceDropdownOptions();
    }

    private void PopulateGroupsFlowContent()
    {
        if (_pageGroups == null) return;
        _pageGroups.SuspendLayout();
        _pageGroups.InnerControls.Clear();

        Label lblInfo = new Label
        {
            Text = "Create program groups to control multiple applications together:",
            Left = 18,
            Top = 10,
            Width = 335,
            Height = 22,
            ForeColor = Color.FromArgb(190, 190, 190)
        };
        _pageGroups.InnerControls.Add(lblInfo);

        Button btnAddGroup = CreateModernButton("+ New Group", 360, 6, 126, 28, true);
        btnAddGroup.Click += (s, e) =>
        {
            _draftSettings.Groups.Add(new ProgramGroup
            {
                GroupName = $"Group{_draftSettings.Groups.Count + 1}",
                Executables = new List<string> { "" }
            });
            RefreshGroupsTabUI();
            MarkDirty();
        };
        _pageGroups.InnerControls.Add(btnAddGroup);

        List<string> exeSuggestions = GetExecutableSuggestions();
        int cardY = 42;

        foreach (var group in _draftSettings.Groups)
        {
            int cardHeight = 82 + (group.Executables.Count * 30);
            Win11CardPanel card = new Win11CardPanel
            {
                Left = 18,
                Top = cardY,
                Width = 468,
                Height = cardHeight
            };

            Label lblGroupName = new Label
            {
                Text = "Name:",
                Left = 12,
                Top = 14,
                Width = 44,
                ForeColor = Color.FromArgb(195, 195, 195)
            };
            card.Controls.Add(lblGroupName);

            TextBox txtGroupName = new TextBox
            {
                Text = group.GroupName,
                Left = 58,
                Top = 11,
                Width = 272,
                BackColor = Color.FromArgb(52, 52, 52),
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
                Left = 340,
                Top = 10,
                Width = 114,
                Height = 25,
                BackColor = Color.FromArgb(165, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnRemoveGroup.FlatAppearance.BorderSize = 0;
            btnRemoveGroup.Click += (s, e) =>
            {
                _draftSettings.Groups.Remove(group);
                RefreshGroupsTabUI();
                MarkDirty();
            };
            card.Controls.Add(btnRemoveGroup);

            int innerY = 44;
            for (int i = 0; i < group.Executables.Count; i++)
            {
                int index = i;
                ModernComboBox cmbExe = new ModernComboBox
                {
                    Left = 12,
                    Top = innerY,
                    Width = 314,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    BadgeResolver = ResolveDropdownBadge,
                    IconResolver = ResolveDropdownIcon
                };
                cmbExe.Items.AddRange(exeSuggestions.Cast<object>().ToArray());
                cmbExe.SelectedItem = group.Executables[index];
                if (cmbExe.SelectedIndex == -1) cmbExe.Text = group.Executables[index];
                cmbExe.DropDown += (s, e) =>
                {
                    _detectedAudio = AudioProcessDetector.Scan();
                    PopulateSingleComboOptions(cmbExe, includeGroups: false);
                };
                cmbExe.SelectedIndexChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    group.Executables[index] = ReadComboText(cmbExe);
                    MarkDirty();
                };
                cmbExe.TextChanged += (s, e) =>
                {
                    if (_isInitializing) return;
                    group.Executables[index] = ReadComboText(cmbExe);
                    MarkDirty();
                };
                card.Controls.Add(cmbExe);

                Button btnBrowseExe = CreateModernButton("Browse...", 332, innerY - 1, 82, 25, false);
                btnBrowseExe.Click += (s, e) =>
                {
                    BrowseForExecutable(cmbExe);
                    group.Executables[index] = ReadComboText(cmbExe);
                };
                card.Controls.Add(btnBrowseExe);

                Button btnRemoveExe = CreateModernButton("✕", 420, innerY - 1, 34, 25, false);
                btnRemoveExe.Click += (s, e) =>
                {
                    group.Executables.RemoveAt(index);
                    RefreshGroupsTabUI();
                    MarkDirty();
                };
                card.Controls.Add(btnRemoveExe);

                innerY += 30;
            }

            Button btnAddExe = CreateModernButton("+ Add Program to Group", 12, innerY + 2, 185, 25, false);
            btnAddExe.Click += (s, e) =>
            {
                group.Executables.Add("");
                RefreshGroupsTabUI();
                MarkDirty();
            };
            card.Controls.Add(btnAddExe);

            _pageGroups.InnerControls.Add(card);
            cardY += cardHeight + 10;
        }

        _pageGroups.ResumeLayout();
        _pageGroups.RecalculateScroll();
    }

    private List<string> GetExecutableSuggestions()
    {
        List<string> list = new List<string>();
        list.AddRange(_detectedAudio.PlayingAudio);
        list.AddRange(_detectedAudio.ActiveAudioSessions);
        list.AddRange(_draftSettings.RecentPrograms);
        list.AddRange(_detectedAudio.RunningWindowedApps);
        return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> GetAvailableSourceOptions()
    {
        List<string> options = new List<string>();

        foreach (var group in _draftSettings.Groups)
        {
            if (!string.IsNullOrWhiteSpace(group.GroupName))
            {
                options.Add($"Group: {group.GroupName}");
            }
        }

        options.AddRange(GetExecutableSuggestions());
        return options.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void UpdateSourceDropdownOptions()
    {
        if (_cmbSource1 == null || _cmbSource2 == null) return;
        List<string> options = GetAvailableSourceOptions();

        string cur1 = ReadComboText(_cmbSource1);
        string cur2 = ReadComboText(_cmbSource2);

        _isInitializing = true;

        _cmbSource1.Items.Clear();
        _cmbSource1.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource1.SelectedItem = cur1;
        if (_cmbSource1.SelectedIndex == -1) _cmbSource1.Text = cur1;

        _cmbSource2.Items.Clear();
        _cmbSource2.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource2.SelectedItem = cur2;
        if (_cmbSource2.SelectedIndex == -1) _cmbSource2.Text = cur2;

        _isInitializing = false;
    }

    private void UpdateContextUI(bool modeSwitched)
    {
        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");

        if (modeSwitched)
        {
            int currentKey1 = (int)(_txtKey1.Tag ?? 0);
            int currentKey2 = (int)(_txtKey2.Tag ?? 0);
            int currentKey3 = (int)(_txtKey3.Tag ?? 0);

            if (_draftSettings.OperationMode == "Volume Control")
            {
                _draftSettings.VolKeyUp = currentKey1;
                _draftSettings.VolKeyDown = currentKey2;
                _draftSettings.VolKeyMute = currentKey3;
            }
            else
            {
                _draftSettings.CrossfadeKeyUp = currentKey1;
                _draftSettings.CrossfadeKeyDown = currentKey2;
                _draftSettings.CrossfadeKeyMute = currentKey3;
            }

            _draftSettings.OperationMode = isVolumeControl ? "Volume Control" : "Crossfade";

            int targetKey1 = isVolumeControl ? _draftSettings.VolKeyUp : _draftSettings.CrossfadeKeyUp;
            int targetKey2 = isVolumeControl ? _draftSettings.VolKeyDown : _draftSettings.CrossfadeKeyDown;
            int targetKey3 = isVolumeControl ? _draftSettings.VolKeyMute : _draftSettings.CrossfadeKeyMute;

            _txtKey1.Text = KeybindHelper.FormatKeybind(targetKey1);
            _txtKey1.Tag = targetKey1;

            _txtKey2.Text = KeybindHelper.FormatKeybind(targetKey2);
            _txtKey2.Tag = targetKey2;

            _txtKey3.Text = KeybindHelper.FormatKeybind(targetKey3);
            _txtKey3.Tag = targetKey3;
        }

        if (isVolumeControl)
        {
            _lblKey1.Text = "Volume Up:";
            _lblKey2.Text = "Volume Down:";
            _lblKey3.Text = "Mute / Unmute:";
            _lblKeyCycleNext.Text = "Cycle Next Source:";
            _lblKeyCyclePrev.Text = "Cycle Prev Source:";
        }
        else
        {
            _lblKey1.Text = "Fade Up / Right:";
            _lblKey2.Text = "Fade Down / Left:";
            _lblKey3.Text = "Cycle Mute:";
            _lblKeyCycleNext.Text = "Cycle Next Pair:";
            _lblKeyCyclePrev.Text = "Cycle Prev Pair:";
        }

        PopulateCycleListUI();
        ReflowGeneralPage();
    }

    private void AddSectionHeader(Control parent, string text, int left, ref int top)
    {
        Label lbl = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 350,
            Height = 19,
            Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 205, 255)
        };
        parent.Controls.Add(lbl);
        top += 22;
    }

    private void AddSubLabel(Control parent, string text, int left, ref int top)
    {
        Label lbl = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 420,
            Height = 17,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(190, 190, 190)
        };
        parent.Controls.Add(lbl);
        top += 18;
    }

    private Label AddAlignedLabel(Control parent, string text, int left, int top)
    {
        Label lbl = new Label
        {
            Text = text,
            Left = left,
            Top = top + 3,
            Width = 132,
            Height = 20,
            ForeColor = Color.FromArgb(210, 210, 210)
        };
        parent.Controls.Add(lbl);
        return lbl;
    }

    private (TextBox txt, Button btn) AddKeyBindControls(Control parent, ref int top, Action<int>? onCustomCommit, bool allowClear = false)
    {
        int txtWidth = allowClear ? 162 : 196;
        TextBox txt = new TextBox
        {
            Left = 148,
            Top = top,
            Width = txtWidth,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            TextAlign = HorizontalAlignment.Center
        };
        parent.Controls.Add(txt);

        Button btnRecord = CreateModernButton("Record Combo", 352, top - 1, 102, 25, false);
        btnRecord.Click += (s, e) => StartRecording(btnRecord, txt, onCustomCommit);
        parent.Controls.Add(btnRecord);

        if (allowClear)
        {
            Button btnClear = CreateModernButton("✕", 314, top - 1, 32, 25, false);
            btnClear.Click += (s, e) =>
            {
                txt.Tag = 0;
                txt.Text = "None";
                onCustomCommit?.Invoke(0);
                MarkDirty();
            };
            parent.Controls.Add(btnClear);
        }

        top += 32;
        return (txt, btnRecord);
    }

    private Panel AddColorPickerRow(Control parent, string labelText, string initialHex, Action<string> onColorChanged, ref int top)
    {
        Label lbl = new Label
        {
            Text = labelText,
            Left = 14,
            Top = top + 3,
            Width = 165,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        parent.Controls.Add(lbl);

        Panel colorBox = new Panel
        {
            Left = 185,
            Top = top,
            Width = 265,
            Height = 22,
            BackColor = SafeParseHexColor(initialHex, Color.FromArgb(0, 168, 255)),
            Cursor = Cursors.Hand
        };

        colorBox.Paint += (s, e) =>
        {
            using Pen p = new Pen(Color.FromArgb(90, 90, 90));
            e.Graphics.DrawRectangle(p, 0, 0, colorBox.Width - 1, colorBox.Height - 1);
        };

        colorBox.Click += (s, e) =>
        {
            using ColorDialog dlg = new ColorDialog { FullOpen = true };
            try { dlg.Color = colorBox.BackColor; } catch { }
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                colorBox.BackColor = dlg.Color;
                string hex = ColorTranslator.ToHtml(dlg.Color);
                onColorChanged(hex);
            }
        };

        parent.Controls.Add(colorBox);
        top += 30;
        return colorBox;
    }

    private void SettingsForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;

        if (e.KeyCode == Keys.Escape)
        {
            CancelRecording();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu ||
            e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey ||
            e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey ||
            e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu)
        {
            if (_activeTargetTextBox != null)
            {
                _activeTargetTextBox.Text = KeybindHelper.FormatLiveModifiers(e.Modifiers);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        int combinedBinding = KeybindHelper.CreateBinding(e.KeyCode, e.Modifiers);
        string comboName = KeybindHelper.FormatKeybind(combinedBinding);

        if (_activeTargetTextBox != null)
        {
            _activeTargetTextBox.Text = comboName;
            _activeTargetTextBox.Tag = combinedBinding;
            _activeKeyCommitCallback?.Invoke(combinedBinding);
            MarkDirty();
        }

        StopRecording();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void SettingsForm_KeyUp(object? sender, KeyEventArgs e)
    {
        if (!_isRecording || _activeTargetTextBox == null) return;
        _activeTargetTextBox.Text = KeybindHelper.FormatLiveModifiers(e.Modifiers);
    }

    private void StartRecording(Button? btn, TextBox targetBox, Action<int>? onCommit = null)
    {
        if (_isRecording) CancelRecording();
        _isRecording = true;
        _activeRecordButton = btn;
        _activeTargetTextBox = targetBox;
        _activeKeyCommitCallback = onCommit;
        _previousTextBoxText = targetBox.Text;

        targetBox.Text = "Press keys...";
        if (btn != null)
        {
            _previousButtonText = btn.Text;
            btn.Text = "Listening...";
            btn.BackColor = Color.FromArgb(205, 95, 0);
        }
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        if (_activeRecordButton != null)
        {
            _activeRecordButton.Text = _previousButtonText;
            _activeRecordButton.BackColor = Color.FromArgb(52, 52, 52);
        }
        _isRecording = false;
        _activeRecordButton = null;
        _activeTargetTextBox = null;
        _activeKeyCommitCallback = null;
    }

    private void CancelRecording()
    {
        if (!_isRecording) return;
        if (_activeTargetTextBox != null)
        {
            _activeTargetTextBox.Text = _previousTextBoxText;
        }
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
        string s1 = _cmbSource1.Text.Trim();
        string s2 = _cmbSource2.Text.Trim();
        string mode = _cmbMode.SelectedItem?.ToString() ?? "Crossfade";

        if (!s1.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s1))
            s1 = AppSettings.NormalizeExeName(s1);
        if (!s2.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s2))
            s2 = AppSettings.NormalizeExeName(s2);

        int key1Val = (int)(_txtKey1.Tag ?? 0);
        int key2Val = (int)(_txtKey2.Tag ?? 0);
        int key3Val = (int)(_txtKey3.Tag ?? 0);
        int cycleNextVal = (int)(_txtKeyCycleNext.Tag ?? 0);
        int cyclePrevVal = (int)(_txtKeyCyclePrev.Tag ?? 0);

        int cfUp = _draftSettings.CrossfadeKeyUp;
        int cfDown = _draftSettings.CrossfadeKeyDown;
        int cfMute = _draftSettings.CrossfadeKeyMute;

        int volUp = _draftSettings.VolKeyUp;
        int volDown = _draftSettings.VolKeyDown;
        int volMute = _draftSettings.VolKeyMute;

        if (mode == "Volume Control")
        {
            volUp = key1Val;
            volDown = key2Val;
            volMute = key3Val;

            if (string.IsNullOrEmpty(s1))
            {
                MessageBox.Show("Volume Control mode requires a primary source or group selection.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else
        {
            cfUp = key1Val;
            cfDown = key2Val;
            cfMute = key3Val;

            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                MessageBox.Show("Crossfade mode requires both Source 1 and Source 2 to be selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (s1.Equals(s2, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Source 1 and Source 2 cannot be the exact same program or group.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> overlappingExes = _draftSettings.GetOverlappingExecutables(s1, s2);
            if (overlappingExes.Count > 0)
            {
                string duplicatesFormatted = string.Join("\n• ", overlappingExes);
                MessageBox.Show(
                    $"The following software is assigned to both Source 1 ({s1}) and Source 2 ({s2}):\n\n• {duplicatesFormatted}\n\n" +
                    "Please remove the duplicate software from one of the groups or select different sources before saving.",
                    "Duplicate Software in Sources",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        // Normalize and validate Cycle Lists
        foreach (var entry in _draftSettings.VolumeCycleList)
        {
            if (!string.IsNullOrWhiteSpace(entry.Source) && !entry.Source.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
            {
                entry.Source = AppSettings.NormalizeExeName(entry.Source);
                _draftSettings.RecordProgramHistory(entry.Source);
            }
        }

        for (int i = 0; i < _draftSettings.CrossfadeCycleList.Count; i++)
        {
            var pair = _draftSettings.CrossfadeCycleList[i];
            if (!string.IsNullOrWhiteSpace(pair.Source1) && !pair.Source1.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
            {
                pair.Source1 = AppSettings.NormalizeExeName(pair.Source1);
                _draftSettings.RecordProgramHistory(pair.Source1);
            }
            if (!string.IsNullOrWhiteSpace(pair.Source2) && !pair.Source2.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
            {
                pair.Source2 = AppSettings.NormalizeExeName(pair.Source2);
                _draftSettings.RecordProgramHistory(pair.Source2);
            }

            if (!string.IsNullOrWhiteSpace(pair.Source1) && !string.IsNullOrWhiteSpace(pair.Source2))
            {
                if (pair.Source1.Equals(pair.Source2, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Crossfade Cycle Pair #{i + 1} has the same source ({pair.Source1}) on both sides.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var pairOverlaps = _draftSettings.GetOverlappingExecutables(pair.Source1, pair.Source2);
                if (pairOverlaps.Count > 0)
                {
                    string dupList = string.Join("\n• ", pairOverlaps);
                    MessageBox.Show(
                        $"Crossfade Cycle Pair #{i + 1} ({pair.Source1} ⇄ {pair.Source2}) contains duplicate software on both sides:\n\n• {dupList}",
                        "Duplicate Software in Cycle Pair",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }
        }

        foreach (var group in _draftSettings.Groups)
        {
            group.Executables = group.Executables
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(AppSettings.NormalizeExeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var exe in group.Executables)
            {
                _draftSettings.RecordProgramHistory(exe);
            }
        }

        _draftSettings.RecordProgramHistory(s1);
        if (mode == "Crossfade")
        {
            _draftSettings.RecordProgramHistory(s2);
        }

        int ParseInt(string text, int defaultVal) => int.TryParse(text, out int val) ? val : defaultVal;

        _draftSettings.OperationMode = mode;
        _draftSettings.Source1 = s1;
        _draftSettings.Source2 = s2;
        _draftSettings.CycleNextKey = cycleNextVal;
        _draftSettings.QuickSwapKey = cycleNextVal;
        _draftSettings.CyclePrevKey = cyclePrevVal;
        _draftSettings.RunOnStartup = _chkStartup.Checked;
        _draftSettings.CrossfadeKeyUp = cfUp;
        _draftSettings.CrossfadeKeyDown = cfDown;
        _draftSettings.CrossfadeKeyMute = cfMute;
        _draftSettings.VolKeyUp = volUp;
        _draftSettings.VolKeyDown = volDown;
        _draftSettings.VolKeyMute = volMute;
        _draftSettings.KeepOverlayOnWhileSettingsOpen = _chkKeepOverlayAlwaysOn.Checked;
        _draftSettings.OverlayPosition = _cmbPosition.SelectedItem?.ToString() ?? "Bottom Center";
        _draftSettings.PopupDurationMs = Math.Clamp(ParseInt(_txtPopupDuration.Text, 2500), 500, 10000);
        _draftSettings.StepSize = Math.Clamp(ParseInt(_txtStepSize.Text, 10), 1, 100);
        _draftSettings.OverlayWidth = Math.Clamp(ParseInt(_txtWidth.Text, 360), 120, 2000);
        _draftSettings.OverlayHeight = Math.Clamp(ParseInt(_txtHeight.Text, 80), 50, 1000);
        _draftSettings.EnableInterpolation = _chkInterpolation.Checked;
        _draftSettings.InterpolationSpeed = Math.Clamp(ParseInt(_txtInterpSpeed.Text, 45), 5, 100);
        _draftSettings.EnableEqualizationBuffer = _chkBuffer.Checked;
        _draftSettings.OverlayIdentityMode = _cmbIdentityMode.SelectedItem?.ToString() ?? "Both";
        _draftSettings.OverlayLevelMode = _cmbLevelMode.SelectedItem?.ToString() ?? "Both";
        _draftSettings.UseSourceBackgroundColors = _chkUseSourceBgColors.Checked;
        _draftSettings.FadeBgColorWithVolume = _chkFadeBgWithVolume.Checked;
        _draftSettings.SingleSourceBgFillMode = _cmbSingleSourceFillMode.SelectedItem?.ToString() ?? "Solid Fill";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (_draftSettings.RunOnStartup) key?.SetValue("AudioCrossfader", $"\"{exePath}\"");
            else key?.DeleteValue("AudioCrossfader", false);
        }
        catch { }

        _savedSettingsSnapshot = _draftSettings.Clone();
        _context.SaveSettings(_draftSettings);

        RefreshGroupsTabUI();
        PopulateCycleListUI();
        ReflowGeneralPage();
        MarkClean();
    }
}

// Hardware-Accelerated DWM Fullscreen-Compatible 60 FPS Overlay with Color Interpolation & Cycle Wheel Column
public class VolumeOverlayForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly Color DefaultDarkGrey = Color.FromArgb(25, 25, 25);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private string _app1Label;
    private string _app2Label;
    private string _rawSource1;
    private string _rawSource2;
    private string _color1Hex;
    private string _color2Hex;
    private AppSettings _settings;
    private bool _isSingleSource;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;

    // Smoothly interpolated background & bar colors
    private Color _renderedBgLeft = DefaultDarkGrey;
    private Color _renderedBgRight = DefaultDarkGrey;
    private Color _renderedBar1 = Color.FromArgb(0, 168, 255);
    private Color _renderedBar2 = Color.FromArgb(0, 168, 255);

    // Cycle Wheel Column State (1.5 items above + 1 active + 1.5 items below = 4.0x base height)
    private List<CycleDisplayItem> _cycleItems = new List<CycleDisplayItem>();
    private int _cycleActiveIndex = 0;
    private int _cycleCountdownMs = 0;
    private float _wheelExpansion = 0f; // 0 = compact, 1 = full 4x height column
    private float _wheelScrollOffset = 0f; // Animates from +1 or -1 toward 0 on cycle

    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private int _totalDurationMs = 2500;
    private bool _isFadingOut = false;
    private bool _pinnedBySettings = false;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsFadedOut => this.Opacity <= 0.0;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public VolumeOverlayForm(string app1, string app2, AppSettings settings)
    {
        _app1Label = app1;
        _app2Label = app2;
        _rawSource1 = settings.Source1;
        _rawSource2 = settings.Source2;
        _color1Hex = settings.Source1Color;
        _color2Hex = settings.Source2Color;
        _settings = settings.Clone();

        _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = DefaultDarkGrey;
        this.ClientSize = new Size(Math.Max(120, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

        _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);
        SnapInterpolatedColorsImmediate();

        PositionOnActiveScreen();
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _fadeTimer.Tick += OnAnimationTick;
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_pinnedBySettings)
        {
            _isFadingOut = false;
            _displayCountdownMs = _totalDurationMs;
        }

        // Update Cycle Wheel column timers & expansion animation
        if (_cycleCountdownMs > 0)
        {
            _cycleCountdownMs -= 16;
        }

        float targetExpansion = (_cycleCountdownMs > 0 && _cycleItems.Count > 0) ? 1.0f : 0.0f;
        float lerpSpeed = _settings.EnableInterpolation
            ? Math.Clamp(_settings.InterpolationSpeed, 5, 100) / 100.0f
            : 1.0f;
        float wheelLerp = Math.Clamp(lerpSpeed * 0.65f, 0.18f, 0.85f);

        if (Math.Abs(targetExpansion - _wheelExpansion) > 0.004f)
        {
            _wheelExpansion += (targetExpansion - _wheelExpansion) * wheelLerp;
        }
        else
        {
            _wheelExpansion = targetExpansion;
        }

        if (Math.Abs(_wheelScrollOffset) > 0.005f)
        {
            _wheelScrollOffset *= (1.0f - Math.Clamp(wheelLerp * 0.85f, 0.20f, 0.75f));
        }
        else
        {
            _wheelScrollOffset = 0f;
        }

        UpdateWindowGeometryForWheel();
        StepColorInterpolations(lerpSpeed);

        if (!_isFadingOut)
        {
            EnsureTopMost();

            if (this.Opacity < 1.0)
            {
                this.Opacity = Math.Min(1.0, this.Opacity + 0.22);
            }
            if (!_pinnedBySettings && _displayCountdownMs > 0)
            {
                _displayCountdownMs -= 16;
                if (_displayCountdownMs <= 0) _isFadingOut = true;
            }
        }
        else
        {
            if (this.Opacity > 0.0)
            {
                this.Opacity = Math.Max(0.0, this.Opacity - 0.12);
            }
            else
            {
                this.Opacity = 0.0;
                _wheelExpansion = 0f;
                _cycleCountdownMs = 0;
                UpdateWindowGeometryForWheel();
                _fadeTimer?.Stop();
            }
        }
        this.Invalidate();
    }

    private static Color ColorLerp(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int a = (int)Math.Round(from.A + (to.A - from.A) * t);
        int r = (int)Math.Round(from.R + (to.R - from.R) * t);
        int g = (int)Math.Round(from.G + (to.G - from.G) * t);
        int b = (int)Math.Round(from.B + (to.B - from.B) * t);
        return Color.FromArgb(Math.Clamp(a, 0, 255), Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    private (Color targetLeft, Color targetRight, Color targetBar1, Color targetBar2) ComputeTargetColors()
    {
        Color unmutedColor = ParseOverlayColor(_settings.UnmutedColor, Color.FromArgb(0, 168, 255));
        Color mutedColor = ParseOverlayColor(_settings.MutedColor, Color.FromArgb(255, 71, 87));

        bool app1Muted = _isSingleSource ? (_muteState > 0) : (_muteState == 2);
        bool app2Muted = !_isSingleSource && (_muteState == 1);

        Color bar1 = app1Muted ? mutedColor : unmutedColor;
        Color bar2 = app2Muted ? mutedColor : unmutedColor;

        if (!_settings.UseSourceBackgroundColors)
        {
            return (DefaultDarkGrey, DefaultDarkGrey, bar1, bar2);
        }

        Color rawC1 = ParseOverlayColor(_color1Hex, Color.FromArgb(27, 59, 90));
        float effVol1 = app1Muted ? 0f : Math.Clamp(_app1Vol, 0f, 1f);
        Color effectiveC1 = _settings.FadeBgColorWithVolume
            ? ColorLerp(DefaultDarkGrey, rawC1, effVol1)
            : rawC1;

        if (_isSingleSource)
        {
            bool gradientToGrey = string.Equals(_settings.SingleSourceBgFillMode, "Gradient to Dark Grey", StringComparison.OrdinalIgnoreCase);
            Color rightC = gradientToGrey ? DefaultDarkGrey : effectiveC1;
            return (effectiveC1, rightC, bar1, bar2);
        }
        else
        {
            Color rawC2 = ParseOverlayColor(_color2Hex, Color.FromArgb(74, 32, 64));
            float effVol2 = app2Muted ? 0f : Math.Clamp(_app2Vol, 0f, 1f);
            Color effectiveC2 = _settings.FadeBgColorWithVolume
                ? ColorLerp(DefaultDarkGrey, rawC2, effVol2)
                : rawC2;

            return (effectiveC1, effectiveC2, bar1, bar2);
        }
    }

    private void SnapInterpolatedColorsImmediate()
    {
        var targets = ComputeTargetColors();
        _renderedBgLeft = targets.targetLeft;
        _renderedBgRight = targets.targetRight;
        _renderedBar1 = targets.targetBar1;
        _renderedBar2 = targets.targetBar2;
    }

    private void StepColorInterpolations(float factor)
    {
        var targets = ComputeTargetColors();
        float cLerp = _settings.EnableInterpolation ? Math.Clamp(factor * 0.55f, 0.12f, 1.0f) : 1.0f;

        _renderedBgLeft = ColorLerp(_renderedBgLeft, targets.targetLeft, cLerp);
        _renderedBgRight = ColorLerp(_renderedBgRight, targets.targetRight, cLerp);
        _renderedBar1 = ColorLerp(_renderedBar1, targets.targetBar1, cLerp);
        _renderedBar2 = ColorLerp(_renderedBar2, targets.targetBar2, cLerp);
    }

    public void ApplyLivePreviewSettings(AppSettings newSettings, bool pinnedWhileSettingsOpen, bool forcePopup)
    {
        if (this.IsDisposed || !this.IsHandleCreated) return;

        this.BeginInvoke(new Action(() =>
        {
            _settings = newSettings.Clone();
            _pinnedBySettings = pinnedWhileSettingsOpen;
            _totalDurationMs = Math.Max(500, _settings.PopupDurationMs);
            _color1Hex = _settings.Source1Color;
            _color2Hex = _settings.Source2Color;
            _rawSource1 = _settings.Source1;
            _rawSource2 = _settings.Source2;
            _app1Label = AudioCrossfaderWorker.GetResolvedLabel(_rawSource1);
            _app2Label = AudioCrossfaderWorker.GetResolvedLabel(_rawSource2);
            _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));

            if (forcePopup || _pinnedBySettings)
            {
                _displayCountdownMs = _totalDurationMs;
                _isFadingOut = false;
                UpdateWindowGeometryForWheel();
                EnsureTopMost();
                if (_fadeTimer != null && !_fadeTimer.Enabled)
                {
                    _fadeTimer.Start();
                }
            }
            this.Invalidate();
        }));
    }

    public void TriggerCycleWheel(List<CycleDisplayItem> items, int activeIndex, int stepDirection)
    {
        if (this.IsDisposed || !this.IsHandleCreated) return;

        this.BeginInvoke(new Action(() =>
        {
            _cycleItems = items ?? new List<CycleDisplayItem>();
            _cycleActiveIndex = Math.Max(0, activeIndex);
            _cycleCountdownMs = Math.Max(1800, _totalDurationMs);
            _displayCountdownMs = Math.Max(_displayCountdownMs, _cycleCountdownMs);
            _isFadingOut = false;
            _wheelScrollOffset = stepDirection >= 0 ? 1.0f : -1.0f;

            if (_fadeTimer != null && !_fadeTimer.Enabled)
            {
                _fadeTimer.Start();
            }
            EnsureTopMost();
            this.Invalidate();
        }));
    }

    private void UpdateWindowGeometryForWheel()
    {
        int baseW = Math.Max(120, _settings.OverlayWidth);
        int baseH = Math.Max(50, _settings.OverlayHeight);

        // 1.5 items above + 1 active item + 1.5 items below = 4.0x base height when fully expanded
        int totalH = baseH + (int)Math.Round(baseH * 3.0f * _wheelExpansion);

        Screen targetScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        try
        {
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
                targetScreen = Screen.FromHandle(fgHwnd);
            }
        }
        catch { }

        Rectangle bounds = targetScreen.Bounds;
        int baseX = bounds.Left + (bounds.Width - baseW) / 2;
        int baseY = bounds.Bottom - baseH - 65;

        switch (_settings.OverlayPosition)
        {
            case "Top Center":
                baseX = bounds.Left + (bounds.Width - baseW) / 2; baseY = bounds.Top + 40; break;
            case "Bottom Right":
                baseX = bounds.Right - baseW - 40; baseY = bounds.Bottom - baseH - 65; break;
            case "Bottom Left":
                baseX = bounds.Left + 40; baseY = bounds.Bottom - baseH - 65; break;
            case "Top Right":
                baseX = bounds.Right - baseW - 40; baseY = bounds.Top + 40; break;
            case "Top Left":
                baseX = bounds.Left + 40; baseY = bounds.Top + 40; break;
            case "Bottom Center":
            default:
                baseX = bounds.Left + (bounds.Width - baseW) / 2; baseY = bounds.Bottom - baseH - 65; break;
        }

        // Expand symmetrically up and down around the active center slot, clamped safely inside screen bounds
        int extraEachSide = (totalH - baseH) / 2;
        int targetY = Math.Clamp(baseY - extraEachSide, bounds.Top + 12, Math.Max(bounds.Top + 12, bounds.Bottom - totalH - 12));

        if (this.Width != baseW || this.Height != totalH || this.Left != baseX || this.Top != targetY)
        {
            this.Bounds = new Rectangle(baseX, targetY, baseW, totalH);
        }
    }

    private void PositionOnActiveScreen() => UpdateWindowGeometryForWheel();

    private void EnsureTopMost()
    {
        if (!this.IsDisposed && this.IsHandleCreated)
        {
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            int cornerPreference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(this.Handle, 33, ref cornerPreference, sizeof(int));
        }
        catch { }
        EnsureTopMost();
    }

    public void UpdateDynamicOverlay(
        bool isSingleSource,
        string label1,
        string label2,
        string rawSource1,
        string rawSource2,
        string color1Hex,
        string color2Hex,
        float app1Vol,
        float app2Vol,
        int muteState,
        bool forceShow)
    {
        _isSingleSource = isSingleSource;
        _app1Label = label1;
        _app2Label = label2;
        _rawSource1 = rawSource1;
        _rawSource2 = rawSource2;
        _color1Hex = color1Hex;
        _color2Hex = color2Hex;
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
            this.BeginInvoke(new Action(() =>
            {
                if (forceShow)
                {
                    UpdateWindowGeometryForWheel();
                    EnsureTopMost();
                    if (_fadeTimer != null && !_fadeTimer.Enabled)
                    {
                        _fadeTimer.Start();
                    }
                }
                if (this.Opacity > 0.0)
                {
                    this.Invalidate();
                }
            }));
        }
    }

    private void DrawHeaderBadge(
        Graphics g,
        RectangleF bounds,
        string label,
        string rawSource,
        float vol,
        bool isMuted,
        float scale,
        bool showName,
        bool showIcon,
        bool showInlinePercent,
        StringAlignment horizontalAlign,
        Color textColorOverride)
    {
        Bitmap? icon = showIcon ? AppIconCache.GetIconForSource(rawSource, _settings) : null;
        int iconPx = icon != null ? (int)Math.Clamp(20 * scale, 14, 36) : 0;

        float fontSize = Math.Clamp(11.5f * scale, 6.5f, 28f);
        using Font font = new Font("Segoe UI", fontSize, FontStyle.Bold);

        string namePart = showName ? label : "";
        SizeF nameSize = string.IsNullOrEmpty(namePart) ? SizeF.Empty : g.MeasureString(namePart, font);

        string pctText = isMuted ? "0%" : $"{(int)Math.Round(vol * 100)}%";
        SizeF fixedPctSlotSize = showInlinePercent ? g.MeasureString("100%", font) : SizeF.Empty;

        float iconGap = (icon != null && (!string.IsNullOrEmpty(namePart) || showInlinePercent)) ? (6f * scale) : 0f;
        float pctGap = (!string.IsNullOrEmpty(namePart) && showInlinePercent) ? (6f * scale) : 0f;

        float totalW = iconPx + iconGap + nameSize.Width + pctGap + fixedPctSlotSize.Width;
        float textHeight = Math.Max(nameSize.Height, fixedPctSlotSize.Height);

        float startX = bounds.X + (bounds.Width - totalW) / 2f;
        if (horizontalAlign == StringAlignment.Near) startX = bounds.X;
        else if (horizontalAlign == StringAlignment.Far) startX = bounds.Right - totalW;

        startX = Math.Max(bounds.X, startX);
        float centerY = bounds.Y + bounds.Height / 2f;

        using SolidBrush textBrush = new SolidBrush(textColorOverride);

        bool mirrorOrder = (horizontalAlign == StringAlignment.Far);

        if (mirrorOrder)
        {
            if (showInlinePercent)
            {
                RectangleF pctRect = new RectangleF(startX, centerY - textHeight / 2f, fixedPctSlotSize.Width + 4f, textHeight);
                using StringFormat sfPct = new StringFormat
                {
                    Alignment = string.IsNullOrEmpty(namePart) && icon == null ? StringAlignment.Center : StringAlignment.Far,
                    LineAlignment = StringAlignment.Near,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(pctText, font, textBrush, pctRect, sfPct);
                startX += fixedPctSlotSize.Width + pctGap;
            }

            if (!string.IsNullOrEmpty(namePart))
            {
                g.DrawString(namePart, font, textBrush, startX, centerY - textHeight / 2f);
                startX += nameSize.Width + iconGap;
            }

            if (icon != null)
            {
                g.DrawImage(icon, new RectangleF(startX, centerY - iconPx / 2f, iconPx, iconPx));
            }
        }
        else
        {
            if (icon != null)
            {
                g.DrawImage(icon, new RectangleF(startX, centerY - iconPx / 2f, iconPx, iconPx));
                startX += iconPx + iconGap;
            }

            if (!string.IsNullOrEmpty(namePart))
            {
                g.DrawString(namePart, font, textBrush, startX, centerY - textHeight / 2f);
                startX += nameSize.Width + pctGap;
            }

            if (showInlinePercent)
            {
                RectangleF pctRect = new RectangleF(startX, centerY - textHeight / 2f, fixedPctSlotSize.Width + 4f, textHeight);
                using StringFormat sfPct = new StringFormat
                {
                    Alignment = string.IsNullOrEmpty(namePart) ? StringAlignment.Center : StringAlignment.Near,
                    LineAlignment = StringAlignment.Near,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(pctText, font, textBrush, pctRect, sfPct);
            }
        }
    }

    private static int GetSnappedFillWidth(int barWidth, float vol)
    {
        if (vol <= 0.01f) return 0;
        if (vol >= 0.985f) return barWidth;
        return Math.Clamp((int)Math.Round(barWidth * vol), 0, barWidth);
    }

    private static Color ParseOverlayColor(string hex, Color defaultColor)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return ColorTranslator.FromHtml(hex);
        }
        catch { }
        return defaultColor;
    }

    private void FillSlotBackground(Graphics g, RectangleF slotRect, Color leftColor, Color rightColor)
    {
        if (slotRect.Width <= 0 || slotRect.Height <= 0) return;

        if (leftColor.ToArgb() == rightColor.ToArgb())
        {
            using SolidBrush sb = new SolidBrush(leftColor);
            g.FillRectangle(sb, slotRect);
            return;
        }

        using LinearGradientBrush gradBrush = new LinearGradientBrush(
            new PointF(slotRect.Left, slotRect.Top),
            new PointF(slotRect.Right, slotRect.Top),
            leftColor,
            rightColor);

        ColorBlend blend = new ColorBlend
        {
            Colors = new[] { leftColor, leftColor, rightColor, rightColor },
            Positions = new[] { 0.0f, 0.22f, 0.78f, 1.0f }
        };
        gradBrush.InterpolationColors = blend;
        g.FillRectangle(gradBrush, slotRect);
    }

    private void DrawActiveHUDContent(Graphics g, RectangleF activeBounds, float scale)
    {
        int width = (int)activeBounds.Width;
        int height = (int)activeBounds.Height;
        float topY = activeBounds.Y;
        int centerX = width / 2;

        bool showName = _settings.OverlayIdentityMode == "Name" || _settings.OverlayIdentityMode == "Both";
        bool showIcon = _settings.OverlayIdentityMode == "Icon" || _settings.OverlayIdentityMode == "Both";
        bool showBar = _settings.OverlayLevelMode == "Bar" || _settings.OverlayLevelMode == "Both";
        bool showPct = _settings.OverlayLevelMode == "Percentage" || _settings.OverlayLevelMode == "Both";

        Color mutedColor = ParseOverlayColor(_settings.MutedColor, Color.FromArgb(255, 71, 87));

        if (_isSingleSource)
        {
            bool isMuted = (_muteState > 0);
            Color headerTextColor = isMuted ? mutedColor : Color.White;
            int sideMargin = 26;

            if (showBar)
            {
                RectangleF headerBounds = new RectangleF(sideMargin, topY + 6 * scale, width - sideMargin * 2, height * 0.48f);
                DrawHeaderBadge(g, headerBounds, _app1Label, _rawSource1, _app1Vol, isMuted, scale, showName, showIcon, showPct, StringAlignment.Center, headerTextColor);

                int barWidth = Math.Max(20, width - (sideMargin * 2));
                int barHeight = Math.Clamp((int)(14 * scale), 8, 40);
                int barY = (int)topY + Math.Clamp((int)(height * 0.60f), 10, Math.Max(10, height - barHeight - 6));

                using Brush barBrush = new SolidBrush(_renderedBar1);
                using Brush trackBgBrush = new SolidBrush(Color.FromArgb(120, 15, 15, 15));

                using (GraphicsPath? trackPath = GetSafeRoundedBarPath(sideMargin, barY, barWidth, barHeight, true, true))
                {
                    if (trackPath != null) g.FillPath(trackBgBrush, trackPath);
                }

                int fillWidth = GetSnappedFillWidth(barWidth, _app1Vol);
                if (fillWidth > 0)
                {
                    using (GraphicsPath? fillPath = GetSafeRoundedBarPath(sideMargin, barY, fillWidth, barHeight, true, true))
                    {
                        if (fillPath != null) g.FillPath(barBrush, fillPath);
                    }
                }
            }
            else
            {
                RectangleF headerBounds = new RectangleF(sideMargin, topY + 6 * scale, width - sideMargin * 2, height * 0.42f);
                DrawHeaderBadge(g, headerBounds, _app1Label, _rawSource1, _app1Vol, isMuted, scale, showName, showIcon, false, StringAlignment.Center, headerTextColor);

                string bigPct = isMuted ? "MUTED" : $"{(int)Math.Round(_app1Vol * 100)}%";
                using Font bigFont = new Font("Segoe UI", Math.Clamp(18f * scale, 10f, 44f), FontStyle.Bold);
                using SolidBrush pctBrush = new SolidBrush(_renderedBar1);
                using StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(bigPct, bigFont, pctBrush, new RectangleF(0, topY + height * 0.42f, width, height * 0.52f), sf);
            }
        }
        else
        {
            int centerLineTop = (int)topY + (int)(10 * scale);
            int centerLineHeight = Math.Max(10, height - (int)(20 * scale));
            using (SolidBrush dividerBrush = new SolidBrush(Color.FromArgb(150, 220, 220, 220)))
            {
                g.FillRectangle(dividerBrush, centerX - 1, centerLineTop, 2, centerLineHeight);
            }

            bool app1Muted = (_muteState == 2);
            bool app2Muted = (_muteState == 1);
            Color text1Color = app1Muted ? mutedColor : Color.White;
            Color text2Color = app2Muted ? mutedColor : Color.White;
            int sideMargin = 20;

            RectangleF leftHeader = new RectangleF(sideMargin, topY + 6 * scale, centerX - sideMargin - 10, height * 0.48f);
            RectangleF rightHeader = new RectangleF(centerX + 10, topY + 6 * scale, centerX - sideMargin - 10, height * 0.48f);

            if (showBar)
            {
                DrawHeaderBadge(g, leftHeader, _app1Label, _rawSource1, _app1Vol, app1Muted, scale, showName, showIcon, showPct, StringAlignment.Far, text1Color);
                DrawHeaderBadge(g, rightHeader, _app2Label, _rawSource2, _app2Vol, app2Muted, scale, showName, showIcon, showPct, StringAlignment.Near, text2Color);

                int barWidth = Math.Max(20, centerX - sideMargin - 8);
                int barHeight = Math.Clamp((int)(14 * scale), 8, 40);
                int barY = (int)topY + Math.Clamp((int)(height * 0.60f), 10, Math.Max(10, height - barHeight - 6));

                using Brush app1BarColor = new SolidBrush(_renderedBar1);
                using Brush app2BarColor = new SolidBrush(_renderedBar2);
                using Brush trackBgBrush = new SolidBrush(Color.FromArgb(120, 15, 15, 15));

                using (GraphicsPath? app1TrackPath = GetSafeRoundedBarPath(centerX - barWidth, barY, barWidth, barHeight, true, false))
                {
                    if (app1TrackPath != null) g.FillPath(trackBgBrush, app1TrackPath);
                }
                using (GraphicsPath? app2TrackPath = GetSafeRoundedBarPath(centerX, barY, barWidth, barHeight, false, true))
                {
                    if (app2TrackPath != null) g.FillPath(trackBgBrush, app2TrackPath);
                }

                int app1FillWidth = GetSnappedFillWidth(barWidth, _app1Vol);
                if (app1FillWidth > 0)
                {
                    using (GraphicsPath? app1FillPath = GetSafeRoundedBarPath(centerX - app1FillWidth, barY, app1FillWidth, barHeight, true, false))
                    {
                        if (app1FillPath != null) g.FillPath(app1BarColor, app1FillPath);
                    }
                }

                int app2FillWidth = GetSnappedFillWidth(barWidth, _app2Vol);
                if (app2FillWidth > 0)
                {
                    using (GraphicsPath? app2FillPath = GetSafeRoundedBarPath(centerX, barY, app2FillWidth, barHeight, false, true))
                    {
                        if (app2FillPath != null) g.FillPath(app2BarColor, app2FillPath);
                    }
                }
            }
            else
            {
                DrawHeaderBadge(g, leftHeader, _app1Label, _rawSource1, _app1Vol, app1Muted, scale, showName, showIcon, false, StringAlignment.Far, text1Color);
                DrawHeaderBadge(g, rightHeader, _app2Label, _rawSource2, _app2Vol, app2Muted, scale, showName, showIcon, false, StringAlignment.Near, text2Color);

                string p1 = app1Muted ? "MUTED" : $"{(int)Math.Round(_app1Vol * 100)}%";
                string p2 = app2Muted ? "MUTED" : $"{(int)Math.Round(_app2Vol * 100)}%";

                using Font bigFont = new Font("Segoe UI", Math.Clamp(16f * scale, 9f, 40f), FontStyle.Bold);
                using SolidBrush b1 = new SolidBrush(_renderedBar1);
                using SolidBrush b2 = new SolidBrush(_renderedBar2);
                using StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(p1, bigFont, b1, new RectangleF(sideMargin, topY + height * 0.44f, centerX - sideMargin - 6, height * 0.50f), sf);
                g.DrawString(p2, bigFont, b2, new RectangleF(centerX + 6, topY + height * 0.44f, centerX - sideMargin - 6, height * 0.50f), sf);
            }
        }
    }

    private void DrawNeighborCycleSlot(Graphics g, RectangleF slotRect, CycleDisplayItem item, float distanceFromCenter, float scale)
    {
        if (slotRect.Bottom < 0 || slotRect.Top > this.Height) return;

        // Smoothly blend neighbor background colors toward DefaultDarkGrey based on distance from active center
        float colorRetention = Math.Clamp(1.0f - (distanceFromCenter * 0.42f), 0.18f, 0.72f);
        Color c1 = _settings.UseSourceBackgroundColors
            ? ColorLerp(DefaultDarkGrey, ParseOverlayColor(item.Color1Hex, DefaultDarkGrey), colorRetention)
            : DefaultDarkGrey;

        Color c2;
        if (item.IsSingleSource)
        {
            bool gradGrey = string.Equals(_settings.SingleSourceBgFillMode, "Gradient to Dark Grey", StringComparison.OrdinalIgnoreCase);
            c2 = gradGrey ? DefaultDarkGrey : c1;
        }
        else
        {
            c2 = _settings.UseSourceBackgroundColors
                ? ColorLerp(DefaultDarkGrey, ParseOverlayColor(item.Color2Hex, DefaultDarkGrey), colorRetention)
                : DefaultDarkGrey;
        }

        FillSlotBackground(g, slotRect, c1, c2);

        int alpha = (int)Math.Clamp((1.0f - (distanceFromCenter / 2.2f)) * 215f * _wheelExpansion, 25f, 215f);
        Color itemTextColor = Color.FromArgb(alpha, 235, 235, 235);

        bool showName = _settings.OverlayIdentityMode == "Name" || _settings.OverlayIdentityMode == "Both";
        bool showIcon = _settings.OverlayIdentityMode == "Icon" || _settings.OverlayIdentityMode == "Both";
        float neighborScale = scale * 0.88f;

        if (item.IsSingleSource)
        {
            RectangleF bounds = new RectangleF(24, slotRect.Y + slotRect.Height * 0.18f, slotRect.Width - 48, slotRect.Height * 0.64f);
            DrawHeaderBadge(g, bounds, item.Label1, item.RawSource1, 1f, false, neighborScale, showName, showIcon, false, StringAlignment.Center, itemTextColor);
        }
        else
        {
            int centerX = (int)(slotRect.Width / 2f);
            RectangleF leftB = new RectangleF(20, slotRect.Y + slotRect.Height * 0.18f, centerX - 32, slotRect.Height * 0.64f);
            RectangleF rightB = new RectangleF(centerX + 12, slotRect.Y + slotRect.Height * 0.18f, centerX - 32, slotRect.Height * 0.64f);

            DrawHeaderBadge(g, leftB, item.Label1, item.RawSource1, 1f, false, neighborScale, showName, showIcon, false, StringAlignment.Far, itemTextColor);
            DrawHeaderBadge(g, rightB, item.Label2, item.RawSource2, 1f, false, neighborScale, showName, showIcon, false, StringAlignment.Near, itemTextColor);

            using Pen divPen = new Pen(Color.FromArgb(alpha / 2, 220, 220, 220), 1.5f);
            g.DrawLine(divPen, centerX, slotRect.Y + slotRect.Height * 0.25f, centerX, slotRect.Bottom - slotRect.Height * 0.25f);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (this.ClientRectangle.Width <= 10 || this.ClientRectangle.Height <= 10) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int baseH = Math.Max(50, _settings.OverlayHeight);
        float scale = Math.Clamp((float)baseH / 80f, 0.5f, 5.0f);
        float centerY = (this.Height - baseH) / 2f;

        using (SolidBrush baseBg = new SolidBrush(DefaultDarkGrey))
        {
            g.FillRectangle(baseBg, this.ClientRectangle);
        }

        // Render extended vertical cycle column when expanded (1.5 items above and 1.5 items below active slot)
        if (_wheelExpansion > 0.01f && _cycleItems.Count > 0)
        {
            int count = _cycleItems.Count;
            for (int offset = -2; offset <= 2; offset++)
            {
                if (offset == 0) continue;

                int itemIdx = ((_cycleActiveIndex + offset) % count + count) % count;
                float visualOffset = offset + _wheelScrollOffset;
                float slotY = centerY + (visualOffset * baseH);
                RectangleF slotRect = new RectangleF(0, slotY, this.Width, baseH);

                DrawNeighborCycleSlot(g, slotRect, _cycleItems[itemIdx], Math.Abs(visualOffset), scale);
            }
        }

        // Render active center HUD slot
        float activeSlotY = centerY + (_wheelScrollOffset * baseH * 0.35f);
        RectangleF activeRect = new RectangleF(0, activeSlotY, this.Width, baseH);
        FillSlotBackground(g, activeRect, _renderedBgLeft, _renderedBgRight);
        DrawActiveHUDContent(g, activeRect, scale);

        // If the cycle wheel is expanded, draw crisp highlight borders around the active slot and smooth top/bottom vignette fades
        if (_wheelExpansion > 0.02f)
        {
            int borderAlpha = (int)Math.Clamp(190 * _wheelExpansion, 0, 200);
            using (Pen activeSlotPen = new Pen(Color.FromArgb(borderAlpha, 255, 255, 255), 1.5f))
            {
                g.DrawLine(activeSlotPen, 0, activeRect.Top, this.Width, activeRect.Top);
                g.DrawLine(activeSlotPen, 0, activeRect.Bottom, this.Width, activeRect.Bottom);
            }

            int fadeHeight = Math.Max(16, (int)(baseH * 0.85f));
            using (LinearGradientBrush topFade = new LinearGradientBrush(
                new Rectangle(0, 0, this.Width, fadeHeight),
                Color.FromArgb((int)(235 * _wheelExpansion), DefaultDarkGrey),
                Color.FromArgb(0, DefaultDarkGrey),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(topFade, 0, 0, this.Width, fadeHeight);
            }

            using (LinearGradientBrush bottomFade = new LinearGradientBrush(
                new Rectangle(0, this.Height - fadeHeight, this.Width, fadeHeight),
                Color.FromArgb(0, DefaultDarkGrey),
                Color.FromArgb((int)(235 * _wheelExpansion), DefaultDarkGrey),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bottomFade, 0, this.Height - fadeHeight, this.Width, fadeHeight);
            }
        }

        // Subtle outer border frame
        using Pen borderPen = new Pen(Color.FromArgb(95, 255, 255, 255), 1);
        g.DrawRectangle(borderPen, 0, 0, this.Width - 1, this.Height - 1);
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
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
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

// Custom Dark-Mode Renderer for System Tray ContextMenuStrip (Eliminates the white left ImageMargin column)
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

// Manages System Tray Quick-Actions, Favorite Groups Menu, and Background Worker
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

    private void StyleSubMenu(ToolStripMenuItem parentItem)
    {
        if (parentItem.DropDown is ToolStripDropDownMenu dd)
        {
            dd.Renderer = _contextMenu.Renderer;
            dd.BackColor = Color.FromArgb(32, 32, 32);
            dd.ForeColor = Color.FromArgb(240, 240, 240);
            dd.ShowImageMargin = true;
        }
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
                SaveSettings(updated);
            };
            _contextMenu.Items.Add(swapItem);
        }

        var resetItem = new ToolStripMenuItem("Reset Volumes to 100%");
        resetItem.Click += (s, e) => ResetLiveVolumes();
        _contextMenu.Items.Add(resetItem);

        var favGroups = _activeSettings.Groups
            .Where(g => g.IsFavorite && !string.IsNullOrWhiteSpace(g.GroupName))
            .ToList();

        _contextMenu.Items.Add(new ToolStripSeparator());

        if (favGroups.Count > 0)
        {
            if (isVolMode)
            {
                var favHeader = new ToolStripMenuItem("Favorite Groups") { Enabled = false };
                _contextMenu.Items.Add(favHeader);

                foreach (var fg in favGroups)
                {
                    string groupTarget = $"Group: {fg.GroupName}";
                    var item = new ToolStripMenuItem(fg.GroupName)
                    {
                        Checked = _activeSettings.Source1.Equals(groupTarget, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += (s, e) =>
                    {
                        var updated = _activeSettings.Clone();
                        updated.Source1 = groupTarget;
                        SaveSettings(updated);
                    };
                    _contextMenu.Items.Add(item);
                }
            }
            else
            {
                var favSub1 = new ToolStripMenuItem("Set Source 1 (Favorites)");
                var favSub2 = new ToolStripMenuItem("Set Source 2 (Favorites)");
                StyleSubMenu(favSub1);
                StyleSubMenu(favSub2);

                foreach (var fg in favGroups)
                {
                    string groupTarget = $"Group: {fg.GroupName}";

                    var item1 = new ToolStripMenuItem(fg.GroupName)
                    {
                        Checked = _activeSettings.Source1.Equals(groupTarget, StringComparison.OrdinalIgnoreCase)
                    };
                    item1.Click += (s, e) =>
                    {
                        if (_activeSettings.Source2.Equals(groupTarget, StringComparison.OrdinalIgnoreCase)) return;
                        var updated = _activeSettings.Clone();
                        updated.Source1 = groupTarget;
                        SaveSettings(updated);
                    };
                    favSub1.DropDownItems.Add(item1);

                    var item2 = new ToolStripMenuItem(fg.GroupName)
                    {
                        Checked = _activeSettings.Source2.Equals(groupTarget, StringComparison.OrdinalIgnoreCase)
                    };
                    item2.Click += (s, e) =>
                    {
                        if (_activeSettings.Source1.Equals(groupTarget, StringComparison.OrdinalIgnoreCase)) return;
                        var updated = _activeSettings.Clone();
                        updated.Source2 = groupTarget;
                        SaveSettings(updated);
                    };
                    favSub2.DropDownItems.Add(item2);
                }

                _contextMenu.Items.Add(favSub1);
                _contextMenu.Items.Add(favSub2);
            }
        }
        else
        {
            var noFavItem = new ToolStripMenuItem("No Favorite Groups Starred") { Enabled = false };
            _contextMenu.Items.Add(noFavItem);
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
            _settingsForm.Show();
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

// Custom Geometric Vector Star Button (Centered 5-pointed star inside the button box)
public class StarToggleButton : Control
{
    private bool _isFavorite;
    private bool _isHovered;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; Invalidate(); }
    }

    public StarToggleButton()
    {
        this.SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.Size = new Size(28, 25);
        this.Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);

        Color bg = _isHovered ? Color.FromArgb(64, 64, 64) : Color.FromArgb(50, 50, 50);
        Color border = _isFavorite ? Color.FromArgb(190, 150, 40) : Color.FromArgb(72, 72, 72);

        using (GraphicsPath boxPath = Win11CardPanel.CreateRoundRect(rect, 4))
        using (SolidBrush bgBrush = new SolidBrush(bg))
        using (Pen borderPen = new Pen(border, 1f))
        {
            e.Graphics.FillPath(bgBrush, boxPath);
            e.Graphics.DrawPath(borderPen, boxPath);
        }

        float cx = Width / 2f;
        float cy = Height / 2f + 0.5f;
        float outerRadius = Math.Min(Width, Height) * 0.34f;
        float innerRadius = outerRadius * 0.42f;

        PointF[] pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2.0 + i * (Math.PI / 5.0);
            float r = (i % 2 == 0) ? outerRadius : innerRadius;
            pts[i] = new PointF(cx + (float)(Math.Cos(angle) * r), cy + (float)(Math.Sin(angle) * r));
        }

        using GraphicsPath starPath = new GraphicsPath();
        starPath.AddPolygon(pts);

        if (_isFavorite)
        {
            using SolidBrush goldBrush = new SolidBrush(Color.FromArgb(255, 202, 45));
            using Pen goldPen = new Pen(Color.FromArgb(255, 220, 90), 1f);
            e.Graphics.FillPath(goldBrush, starPath);
            e.Graphics.DrawPath(goldPen, starPath);
        }
        else
        {
            using Pen outlinePen = new Pen(Color.FromArgb(155, 155, 155), 1.3f);
            e.Graphics.DrawPath(outlinePen, starPath);
        }
    }
}

// Custom Dark-Mode Scrollable Container (Replaces the white Win32 scrollbar with a sleek dark pill thumb)
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

    // Dynamically resizes the card height to hug its lowest visible child control
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
    private ModernComboBox _cmbSource2 = null!;
    private Panel _source2Container = null!;
    private Win11CardPanel _keysCard = null!;
    private Win11CardPanel _multiVolCard = null!;
    private Panel _multiVolSlotsHost = null!;
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

    private TextBox _txtKeySwap = null!;
    private Button _btnRecSwap = null!;

    private ModernComboBox _cmbIdentityMode = null!;
    private ModernComboBox _cmbLevelMode = null!;
    private CheckBox _chkShowIconsInSettings = null!;
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
        this.Size = new Size(520, 740);
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

    private Bitmap? ResolveDropdownIcon(string item)
    {
        if (!_draftSettings.ShowIconsInSettings || string.IsNullOrWhiteSpace(item))
            return null;
        return AppIconCache.GetIconForSource(item, _draftSettings);
    }

    private BadgeInfo ResolveDropdownBadge(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return new BadgeInfo("", Color.Transparent);
        if (item.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
        {
            string gName = item.Substring(7).Trim();
            var grp = _draftSettings.Groups.FirstOrDefault(g => g.GroupName.Equals(gName, StringComparison.OrdinalIgnoreCase));
            return (grp != null && grp.IsFavorite)
                ? new BadgeInfo("★ FAV GROUP", Color.FromArgb(255, 200, 60))
                : new BadgeInfo("GROUP", Color.FromArgb(180, 140, 255));
        }
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

        // Card 1: Mode & Primary Audio Sources
        _sourcesCard = new Win11CardPanel
        {
            Left = 18,
            Top = 4,
            Width = 468,
            Height = 206
        };

        int cy = 10;
        AddSectionHeader(_sourcesCard, "Mode & Primary Audio Sources", 14, ref cy);

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
            }
        };
        _sourcesCard.Controls.Add(_cmbMode);
        cy += 30;

        List<string> options = GetAvailableSourceOptions();

        AddSubLabel(_sourcesCard, "Primary Source 1 (Type, pick detected audio app, or Browse .exe)", 14, ref cy);
        _cmbSource1 = AddSourcePickerRow(_sourcesCard, options, _draftSettings.Source1, 14, 440, ref cy);

        _source2Container = new Panel
        {
            Left = 14,
            Top = cy,
            Width = 440,
            Height = 50,
            BackColor = Color.Transparent
        };
        int s2y = 0;
        AddSubLabel(_source2Container, "Source 2 / Crossfade Target", 0, ref s2y);
        _cmbSource2 = AddSourcePickerRow(_source2Container, options, _draftSettings.Source2, 0, 440, ref s2y);
        _sourcesCard.Controls.Add(_source2Container);

        page.InnerControls.Add(_sourcesCard);

        // Card 2: Hotkeys & Key Combinations
        _keysCard = new Win11CardPanel
        {
            Left = 18,
            Top = _sourcesCard.Bottom + 10,
            Width = 468,
            Height = 176
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

        AddAlignedLabel(_keysCard, "Cycle Fav Group ★:", 14, ky);
        var rSwap = AddKeyBindControls(_keysCard, ref ky, val => _draftSettings.QuickSwapKey = val, allowClear: true);
        _txtKeySwap = rSwap.txt;
        _btnRecSwap = rSwap.btn;

        page.InnerControls.Add(_keysCard);

        // Card 3: Additional Multi-Source Volume Control Slots
        _multiVolCard = new Win11CardPanel
        {
            Left = 18,
            Top = _keysCard.Bottom + 10,
            Width = 468,
            Height = 44
        };

        Label lblMultiHeader = new Label
        {
            Text = "Additional Simultaneous Volume Sources",
            Left = 14,
            Top = 12,
            Width = 285,
            Height = 20,
            Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 205, 255)
        };
        _multiVolCard.Controls.Add(lblMultiHeader);

        Button btnAddSlot = CreateModernButton("+ Add Volume Source", 312, 9, 142, 26, true);
        btnAddSlot.Click += (s, e) =>
        {
            _draftSettings.AdditionalVolumeSlots.Add(new VolumeControlSlot { Source = "" });
            PopulateMultiVolumeSlotsUI();
            ReflowGeneralPage();
            MarkDirty();
        };
        _multiVolCard.Controls.Add(btnAddSlot);

        _multiVolSlotsHost = new Panel
        {
            Left = 12,
            Top = 42,
            Width = 444,
            Height = 0,
            BackColor = Color.Transparent
        };
        _multiVolCard.Controls.Add(_multiVolSlotsHost);
        page.InnerControls.Add(_multiVolCard);

        // Card 4: Behavior & Startup
        _behaviorCard = new Win11CardPanel
        {
            Left = 18,
            Top = _multiVolCard.Bottom + 10,
            Width = 468,
            Height = 144
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
            Text = "Run automatically on Windows Startup",
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

        page.InnerControls.Add(_behaviorCard);
        return page;
    }

    private void PopulateMultiVolumeSlotsUI()
    {
        if (_multiVolSlotsHost == null) return;
        _multiVolSlotsHost.SuspendLayout();
        _multiVolSlotsHost.Controls.Clear();

        List<string> options = GetAvailableSourceOptions();
        int y = 0;

        for (int i = 0; i < _draftSettings.AdditionalVolumeSlots.Count; i++)
        {
            var slot = _draftSettings.AdditionalVolumeSlots[i];
            Win11CardPanel rowCard = new Win11CardPanel
            {
                Left = 0,
                Top = y,
                Width = 442,
                Height = 68,
                FillColor = Color.FromArgb(48, 48, 48),
                BorderColor = Color.FromArgb(66, 66, 66),
                CornerRadius = 6
            };

            ModernComboBox cmb = new ModernComboBox
            {
                Left = 8,
                Top = 7,
                Width = 308,
                DropDownStyle = ComboBoxStyle.DropDown,
                BadgeResolver = ResolveDropdownBadge,
                IconResolver = ResolveDropdownIcon
            };
            cmb.Items.AddRange(options.Cast<object>().ToArray());
            cmb.Text = slot.Source;
            cmb.DropDown += (s, e) =>
            {
                _detectedAudio = AudioProcessDetector.Scan();
                PopulateSingleComboOptions(cmb, includeGroups: true);
            };
            cmb.TextChanged += (s, e) =>
            {
                slot.Source = cmb.Text.Trim();
                MarkDirty();
            };
            rowCard.Controls.Add(cmb);

            Button btnBrowse = CreateModernButton("Browse", 322, 6, 74, 25, false);
            btnBrowse.Click += (s, e) =>
            {
                BrowseForExecutable(cmb);
                slot.Source = cmb.Text.Trim();
            };
            rowCard.Controls.Add(btnBrowse);

            Button btnDel = CreateModernButton("✕", 400, 6, 34, 25, false);
            btnDel.Click += (s, e) =>
            {
                _draftSettings.AdditionalVolumeSlots.Remove(slot);
                PopulateMultiVolumeSlotsUI();
                ReflowGeneralPage();
                MarkDirty();
            };
            rowCard.Controls.Add(btnDel);

            AddMiniSlotKeybind(rowCard, "Up:", slot.KeyUp, 8, 37, val => slot.KeyUp = val);
            AddMiniSlotKeybind(rowCard, "Down:", slot.KeyDown, 150, 37, val => slot.KeyDown = val);
            AddMiniSlotKeybind(rowCard, "Mute:", slot.KeyMute, 300, 37, val => slot.KeyMute = val);

            _multiVolSlotsHost.Controls.Add(rowCard);
            y += 74;
        }

        _multiVolSlotsHost.Height = y;
        _multiVolSlotsHost.ResumeLayout();
    }

    private void ReflowGeneralPage()
    {
        if (_sourcesCard == null || _keysCard == null || _multiVolCard == null || _behaviorCard == null)
            return;

        bool isVolumeControl = (_cmbMode.SelectedItem?.ToString() == "Volume Control");

        _source2Container.Visible = !isVolumeControl;
        _sourcesCard.Height = isVolumeControl ? 148 : 202;

        _keysCard.Top = _sourcesCard.Bottom + 10;

        if (isVolumeControl)
        {
            _multiVolCard.Visible = true;
            _multiVolCard.Top = _keysCard.Bottom + 10;
            int slotsCount = _draftSettings.AdditionalVolumeSlots.Count;
            _multiVolCard.Height = (slotsCount == 0) ? 44 : (46 + _multiVolSlotsHost.Height);

            _chkBuffer.Visible = false;
            _behaviorCard.Top = _multiVolCard.Bottom + 10;
            _behaviorCard.Height = 104;
        }
        else
        {
            _multiVolCard.Visible = false;
            _chkBuffer.Visible = true;
            _behaviorCard.Top = _keysCard.Bottom + 10;
            _behaviorCard.Height = 132;
        }

        _pageGeneral?.RecalculateScroll();
    }

    private void AddMiniSlotKeybind(Control parent, string label, int currentBinding, int left, int top, Action<int> onCommit)
    {
        Label lbl = new Label
        {
            Text = label,
            Left = left,
            Top = top + 4,
            Width = 38,
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            ForeColor = Color.FromArgb(200, 200, 200)
        };
        parent.Controls.Add(lbl);

        TextBox txt = new TextBox
        {
            Text = KeybindHelper.FormatKeybind(currentBinding),
            Tag = currentBinding,
            Left = left + 38,
            Top = top + 1,
            Width = 94,
            ReadOnly = true,
            BackColor = Color.FromArgb(35, 35, 35),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            Cursor = Cursors.Hand,
            TextAlign = HorizontalAlignment.Center
        };
        txt.Click += (s, e) => StartRecording(null, txt, onCommit);
        parent.Controls.Add(txt);
    }

    private DarkScrollPanel CreateGroupsPage()
    {
        DarkScrollPanel page = new DarkScrollPanel { Dock = DockStyle.Fill };
        return page;
    }

    private DarkScrollPanel CreatePersonalizationPage()
    {
        DarkScrollPanel page = new DarkScrollPanel { Dock = DockStyle.Fill };
        int y = 4;

        // Card 1: Display Customization
        Win11CardPanel contentStyleCard = new Win11CardPanel
        {
            Left = 18,
            Top = y,
            Width = 468,
            Height = 152
        };

        int cy = 10;
        AddSectionHeader(contentStyleCard, "Overlay & Menu Content Style", 14, ref cy);

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
        };
        contentStyleCard.Controls.Add(_cmbIdentityMode);
        cy += 32;

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
        };
        contentStyleCard.Controls.Add(_cmbLevelMode);
        cy += 32;

        _chkShowIconsInSettings = new CheckBox
        {
            Text = "Display Program Icons inside Settings Menu Dropdowns",
            Checked = _draftSettings.ShowIconsInSettings,
            Left = 14,
            Top = cy + 2,
            Width = 420,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkShowIconsInSettings.CheckedChanged += (s, e) =>
        {
            _draftSettings.ShowIconsInSettings = _chkShowIconsInSettings.Checked;
            MarkDirty();
        };
        contentStyleCard.Controls.Add(_chkShowIconsInSettings);

        page.InnerControls.Add(contentStyleCard);
        y += contentStyleCard.Height + 10;

        // Card 2: Overlay Colors, Position, Size & Interpolation Speed
        Win11CardPanel appearanceCard = new Win11CardPanel
        {
            Left = 18,
            Top = y,
            Width = 468,
            Height = 290
        };

        int ay = 10;
        AddSectionHeader(appearanceCard, "Overlay Colors, Position & Animation", 14, ref ay);

        _unmutedColorBox = AddColorPickerRow(appearanceCard, "Unmuted Bar Color:", _draftSettings.UnmutedColor, (hex) =>
        {
            _draftSettings.UnmutedColor = hex;
            MarkDirty();
        }, ref ay);

        _mutedColorBox = AddColorPickerRow(appearanceCard, "Muted Bar Color:", _draftSettings.MutedColor, (hex) =>
        {
            _draftSettings.MutedColor = hex;
            MarkDirty();
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
        _cmbPosition.SelectedIndexChanged += (s, e) => MarkDirty();
        appearanceCard.Controls.Add(_cmbPosition);
        ay += 32;

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
        ay += 30;

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
        _txtWidth.TextChanged += (s, e) => MarkDirty();
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
        _txtHeight.TextChanged += (s, e) => MarkDirty();
        appearanceCard.Controls.Add(_txtHeight);
        ay += 32;

        _chkInterpolation = new CheckBox
        {
            Text = "Overlay Bar Interpolation",
            Checked = _draftSettings.EnableInterpolation,
            Left = 14,
            Top = ay + 2,
            Width = 230,
            Height = 22,
            ForeColor = Color.FromArgb(230, 230, 230)
        };
        _chkInterpolation.CheckedChanged += (s, e) =>
        {
            if (_txtInterpSpeed != null) _txtInterpSpeed.Enabled = _chkInterpolation.Checked;
            MarkDirty();
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

        page.InnerControls.Add(appearanceCard);
        page.RecalculateScroll();
        return page;
    }

    private ModernComboBox AddSourcePickerRow(Control parent, List<string> items, string selectedValue, int left, int totalWidth, ref int top)
    {
        int browseWidth = 84;
        int comboWidth = totalWidth - browseWidth - 6;

        ModernComboBox cmb = new ModernComboBox
        {
            Left = left,
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
        cmb.SelectedIndexChanged += (s, e) => MarkDirty();
        cmb.TextChanged += (s, e) => MarkDirty();
        parent.Controls.Add(cmb);

        Button btnBrowse = CreateModernButton("Browse...", left + comboWidth + 6, top - 1, browseWidth, 25, false);
        btnBrowse.Click += (s, e) => BrowseForExecutable(cmb);
        parent.Controls.Add(btnBrowse);

        top += 30;
        return cmb;
    }

    private void PopulateSingleComboOptions(ModernComboBox cmb, bool includeGroups)
    {
        string currentText = cmb.Text;
        var list = includeGroups ? GetAvailableSourceOptions() : GetExecutableSuggestions();
        _isInitializing = true;
        cmb.Items.Clear();
        cmb.Items.AddRange(list.Cast<object>().ToArray());
        cmb.Text = currentText;
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
        PopulateMultiVolumeSlotsUI();

        _cmbMode.SelectedItem = settings.OperationMode;

        _cmbSource1.SelectedItem = settings.Source1;
        if (!_cmbSource1.Items.Contains(settings.Source1)) _cmbSource1.Text = settings.Source1;

        _cmbSource2.SelectedItem = settings.Source2;
        if (!_cmbSource2.Items.Contains(settings.Source2)) _cmbSource2.Text = settings.Source2;

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

        _txtKeySwap.Text = KeybindHelper.FormatKeybind(settings.QuickSwapKey);
        _txtKeySwap.Tag = settings.QuickSwapKey;

        _txtStepSize.Text = settings.StepSize.ToString();
        _chkBuffer.Checked = settings.EnableEqualizationBuffer;
        _chkStartup.Checked = settings.RunOnStartup;

        _cmbIdentityMode.SelectedItem = settings.OverlayIdentityMode;
        _cmbLevelMode.SelectedItem = settings.OverlayLevelMode;
        _chkShowIconsInSettings.Checked = settings.ShowIconsInSettings;

        if (_unmutedColorBox != null)
            _unmutedColorBox.BackColor = ColorTranslator.FromHtml(settings.UnmutedColor);
        if (_mutedColorBox != null)
            _mutedColorBox.BackColor = ColorTranslator.FromHtml(settings.MutedColor);

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
            Text = "Star (★) your favorite groups for Tray & Hotkey quick-swapping:",
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
                IsFavorite = true,
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

            StarToggleButton btnStar = new StarToggleButton
            {
                IsFavorite = group.IsFavorite,
                Left = 12,
                Top = 10,
                Width = 28,
                Height = 25
            };
            btnStar.Click += (s, e) =>
            {
                group.IsFavorite = !group.IsFavorite;
                btnStar.IsFavorite = group.IsFavorite;
                UpdateSourceDropdownOptions();
                MarkDirty();
            };
            card.Controls.Add(btnStar);

            TextBox txtGroupName = new TextBox
            {
                Text = group.GroupName,
                Left = 46,
                Top = 11,
                Width = 284,
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
                cmbExe.Text = group.Executables[index];
                cmbExe.DropDown += (s, e) =>
                {
                    _detectedAudio = AudioProcessDetector.Scan();
                    PopulateSingleComboOptions(cmbExe, includeGroups: false);
                };
                cmbExe.TextChanged += (s, e) =>
                {
                    group.Executables[index] = cmbExe.Text.Trim();
                    MarkDirty();
                };
                card.Controls.Add(cmbExe);

                Button btnBrowseExe = CreateModernButton("Browse...", 332, innerY - 1, 82, 25, false);
                btnBrowseExe.Click += (s, e) =>
                {
                    BrowseForExecutable(cmbExe);
                    group.Executables[index] = cmbExe.Text.Trim();
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

        foreach (var group in _draftSettings.Groups.OrderByDescending(g => g.IsFavorite))
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

        string cur1 = _cmbSource1.Text;
        string cur2 = _cmbSource2.Text;

        _isInitializing = true;

        _cmbSource1.Items.Clear();
        _cmbSource1.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource1.Text = cur1;

        _cmbSource2.Items.Clear();
        _cmbSource2.Items.AddRange(options.Cast<object>().ToArray());
        _cmbSource2.Text = cur2;

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
            _lblKey1.Text = "Primary Vol Up:";
            _lblKey2.Text = "Primary Vol Down:";
            _lblKey3.Text = "Primary Mute:";
        }
        else
        {
            _lblKey1.Text = "Fade Up / Right:";
            _lblKey2.Text = "Fade Down / Left:";
            _lblKey3.Text = "Cycle Mute:";
        }

        ReflowGeneralPage();
    }

    private void AddSectionHeader(Control parent, string text, int left, ref int top)
    {
        Label lbl = new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 320,
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
            BackColor = ColorTranslator.FromHtml(initialHex),
            Cursor = Cursors.Hand
        };

        colorBox.Paint += (s, e) =>
        {
            using Pen p = new Pen(Color.FromArgb(90, 90, 90));
            e.Graphics.DrawRectangle(p, 0, 0, colorBox.Width - 1, colorBox.Height - 1);
        };

        colorBox.Click += (s, e) =>
        {
            using ColorDialog dlg = new ColorDialog();
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
        int swapKeyVal = (int)(_txtKeySwap.Tag ?? 0);

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
                    "Having the same program in both crossfade sources causes its volume to jump between 100% and the crossfaded level. " +
                    "Please remove the duplicate software from one of the groups or select different sources before saving.",
                    "Duplicate Software in Sources",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
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

        foreach (var slot in _draftSettings.AdditionalVolumeSlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.Source) && !slot.Source.StartsWith("Group: ", StringComparison.OrdinalIgnoreCase))
            {
                slot.Source = AppSettings.NormalizeExeName(slot.Source);
                _draftSettings.RecordProgramHistory(slot.Source);
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
        _draftSettings.QuickSwapKey = swapKeyVal;
        _draftSettings.RunOnStartup = _chkStartup.Checked;
        _draftSettings.CrossfadeKeyUp = cfUp;
        _draftSettings.CrossfadeKeyDown = cfDown;
        _draftSettings.CrossfadeKeyMute = cfMute;
        _draftSettings.VolKeyUp = volUp;
        _draftSettings.VolKeyDown = volDown;
        _draftSettings.VolKeyMute = volMute;
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
        _draftSettings.ShowIconsInSettings = _chkShowIconsInSettings.Checked;

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
        MarkClean();
    }
}

// Click-Through 60 FPS Overlay Popup with Mirrored Header & Fixed-Width Percentage Slot
public class VolumeOverlayForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
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
    private readonly AppSettings _settings;
    private bool _isSingleSource;
    private float _app1Vol = 0.5f;
    private float _app2Vol = 0.5f;
    private int _muteState = 0;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _displayCountdownMs = 0;
    private readonly int _totalDurationMs = 2500;
    private bool _isFadingOut = false;

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
        _settings = settings;

        _isSingleSource = (_settings.OperationMode == "Volume Control" || string.IsNullOrEmpty(_app2Label));

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ClientSize = new Size(Math.Max(120, _settings.OverlayWidth), Math.Max(50, _settings.OverlayHeight));

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
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _fadeTimer.Tick += (s, e) =>
        {
            if (!_isFadingOut)
            {
                if (this.Opacity < 1.0)
                {
                    this.Opacity = Math.Min(1.0, this.Opacity + 0.22);
                }
                if (_displayCountdownMs > 0)
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
                    _fadeTimer.Stop();
                }
            }
            this.Invalidate();
        };
        _fadeTimer.Start();
        this.Opacity = 0.0;
    }

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
        UpdateRegionPath();
        EnsureTopMost();
    }

    private void UpdateRegionPath()
    {
        try
        {
            if (this.Width < 10 || this.Height < 10) return;
            using GraphicsPath path = new GraphicsPath();
            int radius = Math.Min(16, this.Height / 2);
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(this.Width - radius, 0, radius, radius, 270, 90);
            path.AddArc(this.Width - radius, this.Height - radius, radius, radius, 0, 90);
            path.AddArc(0, this.Height - radius, radius, radius, 90, 90);
            path.CloseFigure();
            this.Region = new Region(path);
        }
        catch { }
    }

    public void UpdateDynamicOverlay(
        bool isSingleSource,
        string label1,
        string label2,
        string rawSource1,
        string rawSource2,
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

    // Locks the width of the percentage readout to "100%" so the icon and program name never shift
    // Mirrors the layout on the left side ([Pct] [Name] [Icon] | [Icon] [Name] [Pct])
    // and keeps the percentage slot locked to "100%" width so nothing ever shifts.
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
        StringAlignment horizontalAlign)
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

        Color textColor = isMuted ? ColorTranslator.FromHtml(_settings.MutedColor) : Color.White;
        using SolidBrush textBrush = new SolidBrush(textColor);

        // Left side of crossfader (StringAlignment.Far): Draw [Percentage] -> [Name] -> [Icon]
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
            // Right side & Single Source: Draw [Icon] -> [Name] -> [Percentage]
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (this.ClientRectangle.Width <= 10 || this.ClientRectangle.Height <= 10) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (Brush bgBrush = new SolidBrush(Color.FromArgb(238, 25, 25, 25)))
        {
            g.FillRectangle(bgBrush, this.ClientRectangle);
        }

        int centerX = this.Width / 2;
        float scale = Math.Clamp((float)this.Height / 80f, 0.5f, 5.0f);

        bool showName = _settings.OverlayIdentityMode == "Name" || _settings.OverlayIdentityMode == "Both";
        bool showIcon = _settings.OverlayIdentityMode == "Icon" || _settings.OverlayIdentityMode == "Both";
        bool showBar = _settings.OverlayLevelMode == "Bar" || _settings.OverlayLevelMode == "Both";
        bool showPct = _settings.OverlayLevelMode == "Percentage" || _settings.OverlayLevelMode == "Both";

        Color unmutedColor = ColorTranslator.FromHtml(_settings.UnmutedColor);
        Color mutedColor = ColorTranslator.FromHtml(_settings.MutedColor);

        if (_isSingleSource)
        {
            bool isMuted = (_muteState > 0);
            Color activeColor = isMuted ? mutedColor : unmutedColor;
            int sideMargin = 26;

            if (showBar)
            {
                RectangleF headerBounds = new RectangleF(sideMargin, 6 * scale, this.Width - sideMargin * 2, this.Height * 0.48f);
                DrawHeaderBadge(g, headerBounds, _app1Label, _rawSource1, _app1Vol, isMuted, scale, showName, showIcon, showPct, StringAlignment.Center);

                int barWidth = Math.Max(20, this.Width - (sideMargin * 2));
                int barHeight = Math.Clamp((int)(14 * scale), 8, 40);
                int barY = Math.Clamp((int)(this.Height * 0.60f), 10, Math.Max(10, this.Height - barHeight - 6));

                using Brush barBrush = new SolidBrush(activeColor);
                using Brush trackBgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));

                using (GraphicsPath? trackPath = GetSafeRoundedBarPath(sideMargin, barY, barWidth, barHeight, true, true))
                {
                    if (trackPath != null) g.FillPath(trackBgBrush, trackPath);
                }

                int fillWidth = (int)(barWidth * _app1Vol);
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
                RectangleF headerBounds = new RectangleF(sideMargin, 6 * scale, this.Width - sideMargin * 2, this.Height * 0.42f);
                DrawHeaderBadge(g, headerBounds, _app1Label, _rawSource1, _app1Vol, isMuted, scale, showName, showIcon, false, StringAlignment.Center);

                string bigPct = isMuted ? "MUTED" : $"{(int)Math.Round(_app1Vol * 100)}%";
                using Font bigFont = new Font("Segoe UI", Math.Clamp(18f * scale, 10f, 44f), FontStyle.Bold);
                using SolidBrush pctBrush = new SolidBrush(activeColor);
                using StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(bigPct, bigFont, pctBrush, new RectangleF(0, this.Height * 0.42f, this.Width, this.Height * 0.52f), sf);
            }
        }
        else
        {
            int centerLineTop = (int)(10 * scale);
            int centerLineHeight = Math.Max(10, this.Height - (centerLineTop * 2));
            g.FillRectangle(Brushes.DimGray, centerX - 1, centerLineTop, 2, centerLineHeight);

            bool app1Muted = (_muteState == 2);
            bool app2Muted = (_muteState == 1);
            int sideMargin = 20;

            RectangleF leftHeader = new RectangleF(sideMargin, 6 * scale, centerX - sideMargin - 10, this.Height * 0.48f);
            RectangleF rightHeader = new RectangleF(centerX + 10, 6 * scale, centerX - sideMargin - 10, this.Height * 0.48f);

            if (showBar)
            {
                DrawHeaderBadge(g, leftHeader, _app1Label, _rawSource1, _app1Vol, app1Muted, scale, showName, showIcon, showPct, StringAlignment.Far);
                DrawHeaderBadge(g, rightHeader, _app2Label, _rawSource2, _app2Vol, app2Muted, scale, showName, showIcon, showPct, StringAlignment.Near);

                int barWidth = Math.Max(20, centerX - sideMargin - 8);
                int barHeight = Math.Clamp((int)(14 * scale), 8, 40);
                int barY = Math.Clamp((int)(this.Height * 0.60f), 10, Math.Max(10, this.Height - barHeight - 6));

                using Brush app1BarColor = new SolidBrush(app1Muted ? mutedColor : unmutedColor);
                using Brush app2BarColor = new SolidBrush(app2Muted ? mutedColor : unmutedColor);
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
            else
            {
                DrawHeaderBadge(g, leftHeader, _app1Label, _rawSource1, _app1Vol, app1Muted, scale, showName, showIcon, false, StringAlignment.Center);
                DrawHeaderBadge(g, rightHeader, _app2Label, _rawSource2, _app2Vol, app2Muted, scale, showName, showIcon, false, StringAlignment.Center);

                string p1 = app1Muted ? "MUTED" : $"{(int)Math.Round(_app1Vol * 100)}%";
                string p2 = app2Muted ? "MUTED" : $"{(int)Math.Round(_app2Vol * 100)}%";

                using Font bigFont = new Font("Segoe UI", Math.Clamp(16f * scale, 9f, 40f), FontStyle.Bold);
                using SolidBrush b1 = new SolidBrush(app1Muted ? mutedColor : unmutedColor);
                using SolidBrush b2 = new SolidBrush(app2Muted ? mutedColor : unmutedColor);
                using StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(p1, bigFont, b1, new RectangleF(sideMargin, this.Height * 0.44f, centerX - sideMargin - 6, this.Height * 0.50f), sf);
                g.DrawString(p2, bigFont, b2, new RectangleF(centerX + 6, this.Height * 0.44f, centerX - sideMargin - 6, this.Height * 0.50f), sf);
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

                using Pen pen = new Pen(Color.FromArgb(70, 70, 70), 1);
                g.DrawPath(pen, borderPath);
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
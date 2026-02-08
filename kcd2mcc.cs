using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

namespace KCD2ModConflictChecker
{
    // ---------------------------------------------------------------
    //  THEME
    // ---------------------------------------------------------------
    public static class Theme
    {
        // Backgrounds
        public static readonly Color BgDeep       = Color.FromArgb(18, 18, 22);
        public static readonly Color BgBase       = Color.FromArgb(24, 24, 30);
        public static readonly Color BgCard       = Color.FromArgb(32, 33, 40);
        public static readonly Color BgCardHover  = Color.FromArgb(40, 41, 50);
        public static readonly Color BgInput      = Color.FromArgb(28, 28, 36);
        public static readonly Color BgInputFocus = Color.FromArgb(34, 34, 44);

        // Accent
        public static readonly Color Accent       = Color.FromArgb(99, 102, 241);   // Indigo-500
        public static readonly Color AccentHover  = Color.FromArgb(129, 140, 248);  // Indigo-400
        public static readonly Color AccentGlow   = Color.FromArgb(40, 99, 102, 241);
        public static readonly Color AccentSoft   = Color.FromArgb(20, 99, 102, 241);

        // Semantic
        public static readonly Color Success      = Color.FromArgb(52, 211, 153);   // Emerald-400
        public static readonly Color Warning       = Color.FromArgb(251, 191, 36);   // Amber-400
        public static readonly Color Danger        = Color.FromArgb(248, 113, 113);  // Red-400
        public static readonly Color DangerSoft    = Color.FromArgb(20, 248, 113, 113);

        // Text
        public static readonly Color TextPrimary   = Color.FromArgb(240, 240, 245);
        public static readonly Color TextSecondary  = Color.FromArgb(160, 162, 175);
        public static readonly Color TextMuted      = Color.FromArgb(100, 102, 115);

        // Border
        public static readonly Color Border        = Color.FromArgb(48, 50, 62);
        public static readonly Color BorderLight   = Color.FromArgb(58, 60, 72);

        // Fonts
        public static readonly Font DisplayLarge = new("Segoe UI", 22, FontStyle.Bold);
        public static readonly Font DisplayMed   = new("Segoe UI", 16, FontStyle.Bold);
        public static readonly Font HeadingSm    = new("Segoe UI", 12, FontStyle.Bold);
        public static readonly Font Body         = new("Segoe UI", 10, FontStyle.Regular);
        public static readonly Font BodySmall    = new("Segoe UI", 9, FontStyle.Regular);
        public static readonly Font Mono         = new("Cascadia Code", 9.5f, FontStyle.Regular);
        public static readonly Font MonoSmall    = new("Cascadia Code", 8.5f, FontStyle.Regular);
        public static readonly Font ButtonLarge  = new("Segoe UI", 13, FontStyle.Bold);

        public const int Radius = 10;
        public const int RadiusSm = 6;
    }

    // ---------------------------------------------------------------
    //  ENTRY POINT
    // ---------------------------------------------------------------
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------------------------------------------------------------
    //  CONFIG
    // ---------------------------------------------------------------
    public class AppConfig
    {
        public string SteamPath { get; set; } = "";
        public string Kcd2Path { get; set; } = "";
        public List<string> WhitelistedMods { get; set; } = new();
    }

    // ---------------------------------------------------------------
    //  MOD SCANNER  (logic unchanged)
    // ---------------------------------------------------------------
    public class ModScanner
    {
        public Dictionary<string, List<string>> Conflicts { get; private set; } = new();
        public List<string> ScannedMods { get; private set; } = new();
        public List<string> UniqueModNames { get; private set; } = new();

        private Dictionary<string, List<(string Name, string Source, string DisplayName)>> _fileMap = new();
        private static readonly HttpClient _httpClient = new();
        private readonly Dictionary<string, string> _nameCache = new();

        public string? GetSteamPath()
        {
            string? path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(path))
                path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
            return path;
        }

        public string? FindKcd2Path(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            string candidate = Path.Combine(steamPath, "steamapps", "common", "KingdomComeDeliverance2");
            return Directory.Exists(candidate) ? candidate : null;
        }

        public bool CheckWorkshopStatus(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) return false;
            string workshopPath = Path.Combine(steamPath, "steamapps", "workshop", "content", "1771300");
            if (Directory.Exists(workshopPath))
            {
                try { return Directory.GetDirectories(workshopPath).Length > 0; }
                catch { return false; }
            }
            return false;
        }

        private List<string> ScanPakContents(string pakPath)
        {
            var files = new List<string>();
            try
            {
                using var archive = ZipFile.OpenRead(pakPath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue;
                    files.Add(entry.FullName.Replace('\\', '/').ToLowerInvariant());
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error reading {pakPath}: {ex.Message}"); }
            return files;
        }

        private string ResolveModName(string rootFolder, string folderName, string sourceType)
        {
            string fullFolderPath = Path.Combine(rootFolder, folderName);
            string manifestPath = Path.Combine(fullFolderPath, "mod.manifest");
            if (File.Exists(manifestPath))
            {
                try
                {
                    string xml = File.ReadAllText(manifestPath);
                    var match = Regex.Match(xml, @"<name>(.*?)</name>", RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
                catch { }
            }

            if (sourceType == "Workshop" && long.TryParse(folderName, out _))
            {
                if (_nameCache.ContainsKey(folderName)) return _nameCache[folderName];
                try
                {
                    string url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={folderName}";
                    string html = _httpClient.GetStringAsync(url).Result;
                    var titleMatch = Regex.Match(html, @"<title>Steam Workshop::(.*?)</title>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success) { string n = titleMatch.Groups[1].Value.Trim(); _nameCache[folderName] = n; return n; }
                }
                catch { }
            }
            return sourceType == "Local" ? folderName : $"Workshop {folderName}";
        }

        private void ScanDirectoryForPaks(string rootFolder, string sourceType)
        {
            if (!Directory.Exists(rootFolder)) return;
            foreach (var dir in Directory.GetDirectories(rootFolder))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.Contains("ptf", StringComparison.OrdinalIgnoreCase)) continue;

                string modName = ResolveModName(rootFolder, folderName, sourceType);
                foreach (var fullPath in Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories))
                {
                    var pakContents = ScanPakContents(fullPath);
                    if (pakContents.Count > 0 && pakContents.All(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        && pakContents.All(f => Path.GetFileName(f).Contains("__")))
                        continue;

                    string pakName = Path.GetFileNameWithoutExtension(fullPath);
                    string displayName = pakName.Replace(" ", "").Contains(modName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                        ? $"[{sourceType}] {modName}" : $"[{sourceType}] {modName} ({pakName})";

                    ScannedMods.Add(displayName);
                    foreach (var internalFile in pakContents)
                    {
                        if (!_fileMap.ContainsKey(internalFile)) _fileMap[internalFile] = new();
                        _fileMap[internalFile].Add((modName, sourceType, displayName));
                    }
                }
            }
        }

        public Dictionary<string, List<string>> RunFullScan(string kcd2Path, string steamPath)
        {
            Conflicts.Clear(); _fileMap.Clear(); ScannedMods.Clear(); UniqueModNames.Clear();
            ScanDirectoryForPaks(Path.Combine(kcd2Path, "Mods"), "Local");
            if (!string.IsNullOrEmpty(steamPath))
                ScanDirectoryForPaks(Path.Combine(steamPath, "steamapps", "workshop", "content", "1771300"), "Workshop");

            // Build unique mod-level names (matches conflict format)
            var seen = new HashSet<string>();
            foreach (var entries in _fileMap.Values)
                foreach (var e in entries)
                {
                    string key = $"[{e.Source}] {e.Name}";
                    if (seen.Add(key)) UniqueModNames.Add(key);
                }
            UniqueModNames.Sort();

            foreach (var kvp in _fileMap)
            {
                var unique = kvp.Value.Select(e => new { e.Name, e.Source }).Distinct().ToList();
                if (unique.Count > 1)
                    Conflicts[kvp.Key] = unique.Select(m => $"[{m.Source}] {m.Name}").OrderBy(n => n).ToList();
            }
            return Conflicts;
        }

        /// <summary>
        /// Quickly discovers mod folder names without scanning pak contents.
        /// Used to populate the whitelist before a full scan.
        /// </summary>
        public List<string> DiscoverModNames(string kcd2Path, string steamPath)
        {
            var names = new List<string>();
            DiscoverFromDirectory(Path.Combine(kcd2Path, "Mods"), "Local", names);
            if (!string.IsNullOrEmpty(steamPath))
                DiscoverFromDirectory(Path.Combine(steamPath, "steamapps", "workshop", "content", "1771300"), "Workshop", names);
            names.Sort();
            return names;
        }

        private void DiscoverFromDirectory(string rootFolder, string sourceType, List<string> results)
        {
            if (!Directory.Exists(rootFolder)) return;
            foreach (var dir in Directory.GetDirectories(rootFolder))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.Contains("ptf", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories).Length == 0) continue;
                string modName = ResolveModName(rootFolder, folderName, sourceType);
                string key = $"[{sourceType}] {modName}";
                if (!results.Contains(key)) results.Add(key);
            }
        }
    }

    // ---------------------------------------------------------------
    //  DRAWING HELPERS
    // ---------------------------------------------------------------
    public static class GfxHelper
    {
        public static GraphicsPath RoundedRect(RectangleF rect, int radius)
        {
            var gp = new GraphicsPath();
            float d = radius * 2f;
            gp.AddArc(rect.X, rect.Y, d, d, 180, 90);
            gp.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            gp.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            gp.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static void DrawRoundedRect(Graphics g, RectangleF rect, int radius, Color fill, Color? border = null, float borderWidth = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(rect, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (border.HasValue)
            {
                using var pen = new Pen(border.Value, borderWidth);
                g.DrawPath(pen, path);
            }
        }
    }

    // ---------------------------------------------------------------
    //  MODERN BUTTON
    // ---------------------------------------------------------------
    public class ModernButton : Control
    {
        public enum ButtonVariant { Primary, Secondary, Ghost }
        public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;
        public string Icon { get; set; } = "";
        public bool IsLoading { get; set; }

        private bool _hover;
        private bool _pressed;
        private float _hoverAnim;
        private System.Windows.Forms.Timer _animTimer;

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            Size = new Size(140, 38);

            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (s, e) =>
            {
                float target = _hover ? 1f : 0f;
                _hoverAnim += (target - _hoverAnim) * 0.25f;
                if (Math.Abs(_hoverAnim - target) < 0.01f) { _hoverAnim = target; _animTimer.Stop(); }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; _animTimer.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; _animTimer.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Theme.BgBase);

            var rect = new RectangleF(1, 1, Width - 2, Height - 2);

            Color bg, fg, borderColor;
            switch (Variant)
            {
                case ButtonVariant.Primary:
                    bg = Blend(Theme.Accent, Theme.AccentHover, _hoverAnim);
                    fg = Color.White;
                    borderColor = Color.Transparent;
                    break;
                case ButtonVariant.Ghost:
                    bg = Color.FromArgb((int)(30 * _hoverAnim), Theme.TextPrimary);
                    fg = Theme.TextSecondary;
                    borderColor = Color.Transparent;
                    break;
                default:
                    bg = Blend(Theme.BgCard, Theme.BgCardHover, _hoverAnim);
                    fg = Theme.TextPrimary;
                    borderColor = Theme.Border;
                    break;
            }

            if (_pressed) bg = Darken(bg, 0.15f);
            if (!Enabled) { bg = Theme.BgCard; fg = Theme.TextMuted; borderColor = Theme.Border; }

            using var path = GfxHelper.RoundedRect(rect, Theme.RadiusSm);

            // Glow for primary on hover
            if (Variant == ButtonVariant.Primary && _hoverAnim > 0.1f)
            {
                var glowRect = RectangleF.Inflate(rect, 4, 4);
                using var glowPath = GfxHelper.RoundedRect(glowRect, Theme.RadiusSm + 2);
                using var glowBrush = new SolidBrush(Color.FromArgb((int)(25 * _hoverAnim), Theme.Accent));
                g.FillPath(glowBrush, glowPath);
            }

            using var bgBrush = new SolidBrush(bg);
            g.FillPath(bgBrush, path);

            if (borderColor != Color.Transparent)
            {
                using var pen = new Pen(borderColor, 1f);
                g.DrawPath(pen, path);
            }

            string displayText = IsLoading ? "...  Working..." : (string.IsNullOrEmpty(Icon) ? Text : $"{Icon}  {Text}");
            using var fgBrush = new SolidBrush(fg);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(displayText, Font, fgBrush, rect, sf);
        }

        private static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(
                Clamp((int)(a.A + (b.A - a.A) * t)),
                Clamp((int)(a.R + (b.R - a.R) * t)),
                Clamp((int)(a.G + (b.G - a.G) * t)),
                Clamp((int)(a.B + (b.B - a.B) * t)));

        private static Color Darken(Color c, float amount) =>
            Color.FromArgb(c.A, Clamp((int)(c.R * (1 - amount))), Clamp((int)(c.G * (1 - amount))), Clamp((int)(c.B * (1 - amount))));

        private static int Clamp(int v) => Math.Max(0, Math.Min(255, v));
    }

    // ---------------------------------------------------------------
    //  MODERN TEXT INPUT
    // ---------------------------------------------------------------
// ---------------------------------------------------------------
    //  MODERN TEXT INPUT
    // ---------------------------------------------------------------
    public class ModernTextBox : UserControl
    {
        private TextBox _inner;
        private string _label = "";
        private bool _focused;
        private float _focusAnim;
        private System.Windows.Forms.Timer _animTimer;

        public string Label { get => _label; set { _label = value; Invalidate(); } }
        
        [AllowNull]
        public override string Text 
        { 
            get => _inner != null ? _inner.Text : ""; 
            set { if (_inner != null) _inner.Text = value ?? ""; } 
        }

        public ModernTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            
            // 1. Initialize child controls FIRST
            _inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.BgInput,
                ForeColor = Theme.TextPrimary,
                Font = Theme.Body
            };
            _inner.GotFocus += (s, e) => { _focused = true; _animTimer?.Start(); };
            _inner.LostFocus += (s, e) => { _focused = false; _animTimer?.Start(); };
            _inner.TextChanged += (s, e) => OnTextChanged(e);
            Controls.Add(_inner);

            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (s, e) =>
            {
                float target = _focused ? 1f : 0f;
                _focusAnim += (target - _focusAnim) * 0.2f;
                if (Math.Abs(_focusAnim - target) < 0.02f) { _focusAnim = target; _animTimer.Stop(); }
                Invalidate();
            };

            // 2. Set properties that might trigger layout LAST
            Padding = new Padding(14, 14, 14, 8);
            Height = 46; 
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            // Safety check: if _inner isn't ready, don't try to move it
            if (_inner == null) return;

            int left = 14;
            int top = string.IsNullOrEmpty(_label) ? (Height - _inner.PreferredHeight) / 2 : Height - _inner.PreferredHeight - 8;
            _inner.SetBounds(left, top, Width - 28, _inner.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Theme.BgBase);

            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            var bgColor = _focused ? Theme.BgInputFocus : Theme.BgInput;
            var borderColor = _focused ? Theme.Accent : Theme.Border;

            if (_inner != null) _inner.BackColor = bgColor;
            
            GfxHelper.DrawRoundedRect(g, rect, Theme.RadiusSm, bgColor, borderColor, _focused ? 1.5f : 1f);

            if (!string.IsNullOrEmpty(_label))
            {
                using var labelBrush = new SolidBrush(Theme.TextMuted);
                g.DrawString(_label, Theme.MonoSmall, labelBrush, 14, 6);
            }
        }
    }

    // ---------------------------------------------------------------
    //  CARD PANEL (rounded bg with border)
    // ---------------------------------------------------------------
    public class CardPanel : Panel
    {
        public Color CardColor { get; set; } = Theme.BgCard;
        public bool ShowBorder { get; set; } = true;
        public int CornerRadius { get; set; } = Theme.Radius;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(16);
            BackColor = Theme.BgCard;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.BgBase);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            GfxHelper.DrawRoundedRect(g, rect, CornerRadius, CardColor,
                ShowBorder ? Theme.Border : (Color?)null);
        }
    }

    // ---------------------------------------------------------------
    //  STATUS BADGE
    // ---------------------------------------------------------------
    public class StatusBadge : Control
    {
        public enum BadgeStatus { Active, Inactive, Warning }
        public BadgeStatus Status { get; set; } = BadgeStatus.Inactive;

        public StatusBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 26;
            Font = Theme.BodySmall;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Theme.BgBase);

            Color dotColor = Status switch
            {
                BadgeStatus.Active => Theme.Success,
                BadgeStatus.Warning => Theme.Warning,
                _ => Theme.TextMuted
            };
            Color bgColor = Color.FromArgb(15, dotColor);

            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            GfxHelper.DrawRoundedRect(g, rect, Height / 2, bgColor);

            // dot
            g.FillEllipse(new SolidBrush(dotColor), 10, (Height - 8) / 2, 8, 8);

            using var brush = new SolidBrush(Theme.TextSecondary);
            g.DrawString(Text, Font, brush, 24, (Height - Font.Height) / 2f);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    }

    // ---------------------------------------------------------------
    //  SCAN PROGRESS BAR
    // ---------------------------------------------------------------
    public class IndeterminateBar : Control
    {
        private System.Windows.Forms.Timer _timer;
        private float _pos;
        private bool _running;

        public IndeterminateBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 3;
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (s, e) => { _pos = (_pos + 0.012f) % 1.0f; Invalidate(); };
        }

        public void Start() { _running = true; _pos = 0; _timer.Start(); Visible = true; }
        public void Stop() { _running = false; _timer.Stop(); Visible = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BgBase);
            if (!_running) return;

            float w = Width * 0.3f;
            float x = _pos * (Width + w) - w;
            using var brush = new LinearGradientBrush(
                new PointF(x, 0), new PointF(x + w, 0),
                Color.Transparent, Theme.Accent);

            var blend = new ColorBlend(3)
            {
                Colors = new[] { Color.Transparent, Theme.Accent, Color.Transparent },
                Positions = new[] { 0f, 0.5f, 1f }
            };
            brush.InterpolationColors = blend;
            g.FillRectangle(brush, x, 0, w, Height);
        }
    }

    // ---------------------------------------------------------------
    //  DARK RICH TEXT BOX (for log output)
    // ---------------------------------------------------------------
    public class DarkRichTextBox : RichTextBox
    {
        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string? pszSubIdList);

        [DllImport("user32.dll")]
        private static extern bool HideCaret(IntPtr hWnd);

        public DarkRichTextBox()
        {
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            ScrollBars = RichTextBoxScrollBars.Vertical;
        }

        protected override void CreateHandle()
        {
            base.CreateHandle();
            SetWindowTheme(Handle, "DarkMode_Explorer", null);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            HideCaret(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_PAINT = 0x000F
            if (m.Msg == 0x0007 || m.Msg == 0x000F)
                HideCaret(Handle);
        }
    }

    // ---------------------------------------------------------------
    //  WHITELIST DIALOG
    // ---------------------------------------------------------------
    public class WhitelistDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;

        private CheckedListBox _checkedList = null!;
        private List<string> _allMods;
        private HashSet<string> _whitelisted;

        public List<string> Result => _whitelisted.ToList();

        public WhitelistDialog(List<string> allMods, List<string> currentWhitelist)
        {
            _allMods = allMods.OrderBy(m => m).ToList();
            _whitelisted = new HashSet<string>(currentWhitelist);

            Text = "Manage Whitelisted Mods";
            Size = new Size(520, 480);
            MinimumSize = new Size(400, 320);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BgBase;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Body;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            try
            {
                int val = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref val, sizeof(int));
                int captionColor = ColorTranslator.ToWin32(Theme.BgBase);
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch { }

            BuildUI();
        }

        private void BuildUI()
        {
            var lblInfo = new Label
            {
                Text = "Whitelisted mods are excluded from conflict results. Select the mods you want to whitelist:",
                Font = Theme.BodySmall,
                ForeColor = Theme.TextSecondary,
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(12, 10, 12, 4)
            };
            Controls.Add(lblInfo);

            // Bottom button panel
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.BgBase, Padding = new Padding(12, 8, 12, 8) };
            Controls.Add(bottomPanel);

            var btnSave = new ModernButton
            {
                Text = "Save",
                Variant = ModernButton.ButtonVariant.Primary,
                Dock = DockStyle.Right,
                Size = new Size(100, 40),
                Font = Theme.Body
            };
            btnSave.Click += (s, e) =>
            {
                _whitelisted.Clear();
                for (int i = 0; i < _checkedList.Items.Count; i++)
                {
                    if (_checkedList.GetItemChecked(i))
                        _whitelisted.Add(_checkedList.Items[i].ToString()!);
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            bottomPanel.Controls.Add(btnSave);

            var btnCancel = new ModernButton
            {
                Text = "Cancel",
                Variant = ModernButton.ButtonVariant.Secondary,
                Dock = DockStyle.Right,
                Size = new Size(100, 40),
                Font = Theme.Body,
                Margin = new Padding(0, 0, 8, 0)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bottomPanel.Controls.Add(btnCancel);

            // Spacer between cancel and select buttons
            var spacer = new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Theme.BgBase };
            bottomPanel.Controls.Add(spacer);

            var btnSelectAll = new ModernButton
            {
                Text = "Select All",
                Variant = ModernButton.ButtonVariant.Ghost,
                Dock = DockStyle.Left,
                Size = new Size(90, 40),
                Font = Theme.BodySmall
            };
            btnSelectAll.Click += (s, e) => { for (int i = 0; i < _checkedList.Items.Count; i++) _checkedList.SetItemChecked(i, true); };
            bottomPanel.Controls.Add(btnSelectAll);

            var btnClearAll = new ModernButton
            {
                Text = "Clear All",
                Variant = ModernButton.ButtonVariant.Ghost,
                Dock = DockStyle.Left,
                Size = new Size(90, 40),
                Font = Theme.BodySmall
            };
            btnClearAll.Click += (s, e) => { for (int i = 0; i < _checkedList.Items.Count; i++) _checkedList.SetItemChecked(i, false); };
            bottomPanel.Controls.Add(btnClearAll);

            // Checked list box in a card
            var listCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(4),
                ShowBorder = true
            };
            Controls.Add(listCard);

            _checkedList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgCard,
                ForeColor = Theme.TextPrimary,
                Font = Theme.Body,
                BorderStyle = BorderStyle.None,
                CheckOnClick = true,
                IntegralHeight = false
            };

            foreach (var mod in _allMods)
                _checkedList.Items.Add(mod, _whitelisted.Contains(mod));

            listCard.Controls.Add(_checkedList);

            // Bring list to front for proper dock order
            listCard.BringToFront();
        }
    }

    // ---------------------------------------------------------------
    //  MAIN FORM
    // ---------------------------------------------------------------
    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;

        private AppConfig _config = new();
        private ModScanner _scanner = new();
        private string _configFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KCD2MCC");
        private string _configFile => Path.Combine(_configFolder, "config.json");

        // Views
        private Panel _root = null!;
        private Panel _setupView = null!;
        private Panel _mainView = null!;

        // Setup controls
        private ModernTextBox _txtKcd2 = null!;
        private ModernTextBox _txtSteam = null!;

        // Main controls
        private ModernButton _btnScan = null!;
        private ModernButton _btnWhitelist = null!;
        private ModernButton _btnExport = null!;
        private DarkRichTextBox _rtbLog = null!;
        private StatusBadge _badgeWorkshop = null!;
        private IndeterminateBar _progressBar = null!;
        private Label _lblModCount = null!;
        private Label _lblConflictCount = null!;
        private Label _lblHiddenCount = null!;

        public MainForm()
        {
            Text = "KCD2 Mod Conflict Checker";
            Size = new Size(880, 640);
            MinimumSize = new Size(720, 520);
            BackColor = Theme.BgBase;
            ForeColor = Theme.TextPrimary;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = Theme.Body;

            try
            {
                if (File.Exists("kcd2.ico")) Icon = new Icon("kcd2.ico");
                else { var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (ico != null) Icon = ico; }
            }
            catch { }

            ApplyDarkTitleBar();
            BuildUI();
            LoadConfig();

            if (IsConfigValid()) ShowMainView();
            else ShowSetupView(true);
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                int val = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref val, sizeof(int));
                int captionColor = ColorTranslator.ToWin32(Theme.BgBase);
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch { }
        }

        //   BUILD UI
        private void BuildUI()
        {
            _root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBase };
            Controls.Add(_root);

            BuildSetupView();
            BuildMainView();
        }

        private void BuildSetupView()
        {
            _setupView = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.BgBase };
            _root.Controls.Add(_setupView);

            var center = new Panel { Size = new Size(520, 420), BackColor = Theme.BgBase };
            _setupView.Controls.Add(center);
            _setupView.Resize += (s, e) =>
            {
                center.Left = (_setupView.Width - center.Width) / 2;
                center.Top = (_setupView.Height - center.Height) / 2;
            };

            // Logo / title area
            var lblIcon = new Label
            {
                Text = "⚔",
                Font = new Font("Segoe UI Emoji", 36),
                ForeColor = Theme.Accent,
                AutoSize = true,
                Location = new Point(center.Width / 2 - 30, 0)
            };
            center.Controls.Add(lblIcon);

            var lblTitle = new Label
            {
                Text = "KCD2 Mod Conflict Checker",
                Font = Theme.DisplayLarge,
                ForeColor = Theme.TextPrimary,
                AutoSize = false,
                Size = new Size(520, 36),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, 70)
            };
            center.Controls.Add(lblTitle);

            // Path inputs in a card
            var pathCard = new CardPanel
            {
                Location = new Point(0, 150),
                Size = new Size(520, 150),
                Padding = new Padding(20, 16, 20, 16)
            };
            center.Controls.Add(pathCard);

            // KCD2 path
            var lblKcd2 = new Label { Text = "KCD2 Installation Path", Font = Theme.BodySmall, ForeColor = Theme.TextSecondary, Location = new Point(20, 16), AutoSize = true };
            pathCard.Controls.Add(lblKcd2);

            _txtKcd2 = new ModernTextBox { Location = new Point(20, 36), Size = new Size(390, 42) };
            pathCard.Controls.Add(_txtKcd2);

            var btnBrowseKcd = new ModernButton { Text = "Browse", Variant = ModernButton.ButtonVariant.Secondary, Location = new Point(418, 36), Size = new Size(80, 42) };
            btnBrowseKcd.Click += (s, e) => { var p = BrowseFolder(); if (p != null) _txtKcd2.Text = p; };
            pathCard.Controls.Add(btnBrowseKcd);

            // Steam path (optional)
            var lblSteam = new Label { Text = "Steam Path  (optional -- for Workshop mods)", Font = Theme.BodySmall, ForeColor = Theme.TextSecondary, Location = new Point(20, 86), AutoSize = true };
            pathCard.Controls.Add(lblSteam);

            _txtSteam = new ModernTextBox { Location = new Point(20, 106), Size = new Size(390, 42) };
            pathCard.Controls.Add(_txtSteam);

            var btnBrowseSteam = new ModernButton { Text = "Browse", Variant = ModernButton.ButtonVariant.Secondary, Location = new Point(418, 106), Size = new Size(80, 42) };
            btnBrowseSteam.Click += (s, e) => { var p = BrowseFolder(); if (p != null) _txtSteam.Text = p; };
            pathCard.Controls.Add(btnBrowseSteam);

            // Action buttons
            var btnDetect = new ModernButton
            {
                Text = "Auto-Detect Paths",
                Icon = "",
                Variant = ModernButton.ButtonVariant.Ghost,
                Location = new Point(0, 320),
                Size = new Size(520, 40),
                Font = Theme.Body
            };
            btnDetect.Click += (s, e) => RunAutoDetect(false);
            center.Controls.Add(btnDetect);

            var btnContinue = new ModernButton
            {
                Text = "Continue",
                Variant = ModernButton.ButtonVariant.Primary,
                Location = new Point(0, 370),
                Size = new Size(520, 48),
                Font = Theme.ButtonLarge
            };
            btnContinue.Tag = "continue_btn";
            btnContinue.Click += (s, e) => ValidateAndSave();
            center.Controls.Add(btnContinue);
        }

        private void BuildMainView()
        {
            _mainView = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(24, 20, 24, 20), BackColor = Theme.BgBase };
            _root.Controls.Add(_mainView);

            //  Top header
            var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Theme.BgBase };
            _mainView.Controls.Add(header);

            var lblTitle = new Label
            {
                Text = "⚔  KCD2 Mod Conflict Checker",
                Font = Theme.DisplayMed,
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                Location = new Point(0, 8)
            };
            header.Controls.Add(lblTitle);

            var rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0),
                BackColor = Theme.BgBase
            };
            header.Controls.Add(rightPanel);

            var btnSettings = new ModernButton
            {
                Text = "Settings",
                Icon = "⚙",
                Variant = ModernButton.ButtonVariant.Ghost,
                Size = new Size(110, 34),
                Font = Theme.BodySmall
            };
            btnSettings.Click += (s, e) => ShowSetupView(false);
            rightPanel.Controls.Add(btnSettings);

            _badgeWorkshop = new StatusBadge { Text = "Workshop", Size = new Size(140, 28), Margin = new Padding(0, 6, 0, 0) };
            rightPanel.Controls.Add(_badgeWorkshop);

            //  Stats row
            var statsRow = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(0, 12, 0, 8), BackColor = Theme.BgBase };
            _mainView.Controls.Add(statsRow);

            var statModsCard = new CardPanel { Location = new Point(0, 8), Size = new Size(180, 56) };
            var lblModLabel = new Label { Text = "Mods Scanned", Font = Theme.MonoSmall, ForeColor = Theme.TextMuted, Location = new Point(16, 8), AutoSize = true };
            _lblModCount = new Label { Text = "--", Font = Theme.HeadingSm, ForeColor = Theme.TextPrimary, Location = new Point(16, 28), AutoSize = true };
            statModsCard.Controls.Add(lblModLabel);
            statModsCard.Controls.Add(_lblModCount);
            statsRow.Controls.Add(statModsCard);

            var statConflictsCard = new CardPanel { Location = new Point(196, 8), Size = new Size(180, 56) };
            var lblConflictLabel = new Label { Text = "Conflicts", Font = Theme.MonoSmall, ForeColor = Theme.TextMuted, Location = new Point(16, 8), AutoSize = true };
            _lblConflictCount = new Label { Text = "--", Font = Theme.HeadingSm, ForeColor = Theme.TextPrimary, Location = new Point(16, 28), AutoSize = true };
            statConflictsCard.Controls.Add(lblConflictLabel);
            statConflictsCard.Controls.Add(_lblConflictCount);
            statsRow.Controls.Add(statConflictsCard);

            var statHiddenCard = new CardPanel { Location = new Point(392, 8), Size = new Size(180, 56) };
            var lblHiddenLabel = new Label { Text = "Whitelisted", Font = Theme.MonoSmall, ForeColor = Theme.TextMuted, Location = new Point(16, 8), AutoSize = true };
            _lblHiddenCount = new Label { Text = "--", Font = Theme.HeadingSm, ForeColor = Theme.TextPrimary, Location = new Point(16, 28), AutoSize = true };
            statHiddenCard.Controls.Add(lblHiddenLabel);
            statHiddenCard.Controls.Add(_lblHiddenCount);
            statsRow.Controls.Add(statHiddenCard);

            //  Scan button row
            var scanRow = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Theme.BgBase };
            _mainView.Controls.Add(scanRow);

            _btnWhitelist = new ModernButton
            {
                Text = "Whitelist",
                Icon = "\u2630",
                Variant = ModernButton.ButtonVariant.Secondary,
                Size = new Size(120, 48),
                Font = Theme.Body
            };
            _btnWhitelist.Click += (s, e) => OpenWhitelistDialog();
            scanRow.Controls.Add(_btnWhitelist);

            _btnExport = new ModernButton
            {
                Text = "Export",
                Icon = "\u2913",
                Variant = ModernButton.ButtonVariant.Secondary,
                Size = new Size(100, 48),
                Font = Theme.Body,
                Enabled = false
            };
            _btnExport.Click += (s, e) => ExportConflicts();
            scanRow.Controls.Add(_btnExport);

            _btnScan = new ModernButton
            {
                Text = "SCAN FOR CONFLICTS",
                Icon = "\u26A1",
                Variant = ModernButton.ButtonVariant.Primary,
                Size = new Size(200, 48),
                Font = Theme.ButtonLarge
            };
            _btnScan.Click += (s, e) => StartScan();
            scanRow.Controls.Add(_btnScan);

            // Manual layout for scan row to prevent clipping
            scanRow.Resize += (s, e) =>
            {
                int gap = 12;
                int wlW = _btnWhitelist.Width;
                int exW = _btnExport.Width;
                _btnWhitelist.Location = new Point(scanRow.ClientSize.Width - wlW, 6);
                _btnExport.Location = new Point(scanRow.ClientSize.Width - wlW - gap - exW, 6);
                _btnScan.SetBounds(0, 6, scanRow.ClientSize.Width - wlW - exW - gap * 2, 48);
            };

            //  Progress bar
            _progressBar = new IndeterminateBar { Dock = DockStyle.Top, Visible = false };
            _mainView.Controls.Add(_progressBar);

            //  Log output
            var logCard = new CardPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2),
                ShowBorder = true
            };
            _mainView.Controls.Add(logCard);

            _rtbLog = new DarkRichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgCard,
                ForeColor = Theme.TextSecondary,
                Font = Theme.Mono,
                ReadOnly = true,
                Text = "  Ready to scan..."
            };
            logCard.Controls.Add(_rtbLog);

            // Fix WinForms dock ordering (last added docks innermost)
            logCard.BringToFront();
        }

        //   LOGIC
        private void ShowSetupView(bool isFirstRun)
        {
            _mainView.Visible = false;
            _setupView.Visible = true;
            _setupView.BringToFront();
            _txtSteam.Text = _config.SteamPath;
            _txtKcd2.Text = _config.Kcd2Path;

            // Find the continue button and update its text
            foreach (Control c in _setupView.Controls)
                UpdateContinueButton(c, isFirstRun ? "Continue" : "Save & Return");

            if (isFirstRun && string.IsNullOrEmpty(_txtSteam.Text))
                RunAutoDetect(true);
        }

        private void UpdateContinueButton(Control parent, string text)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ModernButton btn && btn.Tag?.ToString() == "continue_btn") btn.Text = text;
                if (c.HasChildren) UpdateContinueButton(c, text);
            }
        }

        private void ShowMainView()
        {
            _setupView.Visible = false;
            _mainView.Visible = true;
            _mainView.BringToFront();
            UpdateWorkshopBadge();
        }

        private void UpdateWorkshopBadge()
        {
            if (string.IsNullOrEmpty(_config.SteamPath))
            {
                _badgeWorkshop.Text = "Workshop: off";
                _badgeWorkshop.Status = StatusBadge.BadgeStatus.Inactive;
            }
            else
            {
                bool ok = _scanner.CheckWorkshopStatus(_config.SteamPath);
                _badgeWorkshop.Text = ok ? "Workshop: active" : "Workshop: empty";
                _badgeWorkshop.Status = ok ? StatusBadge.BadgeStatus.Active : StatusBadge.BadgeStatus.Warning;
            }
            _badgeWorkshop.Invalidate();
        }

        private void OpenWhitelistDialog()
        {
            // Use full scan results if available, otherwise do a quick folder-level discovery
            var allMods = _scanner.UniqueModNames.Count > 0
                ? new List<string>(_scanner.UniqueModNames)
                : _scanner.DiscoverModNames(_config.Kcd2Path, _config.SteamPath);

            // Also include any previously whitelisted mods that might not be currently installed
            foreach (var wl in _config.WhitelistedMods)
                if (!allMods.Contains(wl)) allMods.Add(wl);

            if (allMods.Count == 0)
            {
                MessageBox.Show("No mods found in your game or workshop folders.", "Whitelist", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new WhitelistDialog(allMods, _config.WhitelistedMods);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _config.WhitelistedMods = dlg.Result;
                SaveConfig();

                // Re-display results if we have them
                if (_scanner.Conflicts.Count > 0 || _scanner.ScannedMods.Count > 0)
                    DisplayResults(_scanner.Conflicts);
            }
        }

        private void ExportConflicts()
        {
            var conflicts = _scanner.Conflicts;
            if (conflicts.Count == 0 && _scanner.ScannedMods.Count == 0)
            {
                MessageBox.Show("No scan results to export. Run a scan first.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = $"KCD2_Conflicts_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"
            };

            if (sfd.ShowDialog() != DialogResult.OK) return;

            var whitelist = new HashSet<string>(_config.WhitelistedMods);
            var filtered = new Dictionary<string, List<string>>();
            int hiddenFiles = 0;
            foreach (var kvp in conflicts)
            {
                bool anyWhitelisted = kvp.Value.Any(mod => whitelist.Contains(mod));
                if (anyWhitelisted)
                    hiddenFiles++;
                else
                    filtered[kvp.Key] = kvp.Value;
            }

            var sb = new StringBuilder();
            sb.AppendLine("KCD2 Mod Conflict Checker - Export");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Mods scanned: {_scanner.ScannedMods.Count}");
            sb.AppendLine($"Conflicts: {filtered.Count} files");
            if (hiddenFiles > 0)
                sb.AppendLine($"Hidden by whitelist: {hiddenFiles} files");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine();

            if (filtered.Count == 0)
            {
                sb.AppendLine("No conflicts detected.");
                sb.AppendLine();
                sb.AppendLine("Scanned mods:");
                foreach (var mod in _scanner.UniqueModNames)
                {
                    bool isWl = whitelist.Contains(mod);
                    sb.AppendLine($"  - {mod}{(isWl ? "  (whitelisted)" : "")}");
                }
            }
            else
            {
                var grouped = new Dictionary<string, List<string>>();
                foreach (var kvp in filtered)
                {
                    string key = string.Join("\n", kvp.Value);
                    if (!grouped.ContainsKey(key)) grouped[key] = new();
                    grouped[key].Add(kvp.Key);
                }

                var uniqueConflictMods = new HashSet<string>();
                foreach (var kvp in filtered)
                    foreach (var m in kvp.Value)
                        uniqueConflictMods.Add(m);

                sb.AppendLine($"{uniqueConflictMods.Count} conflicting mods, {filtered.Count} overlapping files");
                sb.AppendLine();

                int groupNum = 0;
                foreach (var group in grouped)
                {
                    groupNum++;
                    sb.AppendLine(new string('-', 60));
                    sb.AppendLine($"Conflict group #{groupNum}");
                    foreach (var line in group.Key.Split('\n'))
                        sb.AppendLine($"  > {line}");
                    sb.AppendLine();
                    sb.AppendLine($"  Overlapping files ({group.Value.Count}):");
                    foreach (var f in group.Value)
                        sb.AppendLine($"    {f}");
                    sb.AppendLine();
                }
            }

            try
            {
                File.WriteAllText(sfd.FileName, sb.ToString());
                MessageBox.Show($"Exported to:\n{sfd.FileName}", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartScan()
        {
            _btnScan.Enabled = false;
            _btnScan.IsLoading = true;
            _btnScan.Invalidate();
            _rtbLog.Clear();
            _progressBar.Start();
            _lblModCount.Text = "...";
            _lblConflictCount.Text = "...";

            Task.Run(() =>
            {
                var conflicts = _scanner.RunFullScan(_config.Kcd2Path, _config.SteamPath);
                Invoke((MethodInvoker)delegate { DisplayResults(conflicts); });
            });
        }

        private void DisplayResults(Dictionary<string, List<string>> conflicts)
        {
            _btnScan.Enabled = true;
            _btnScan.IsLoading = false;
            _btnScan.Invalidate();
            _progressBar.Stop();
            _btnExport.Enabled = true;
            _btnExport.Invalidate();

            _lblModCount.Text = _scanner.ScannedMods.Count.ToString();
            _lblModCount.ForeColor = Theme.TextPrimary;

            // Filter out conflicts where ALL involved mods are whitelisted
            var whitelist = new HashSet<string>(_config.WhitelistedMods);
            var filtered = new Dictionary<string, List<string>>();
            int hiddenFiles = 0;
            foreach (var kvp in conflicts)
            {
                bool anyWhitelisted = kvp.Value.Any(mod => whitelist.Contains(mod));
                if (anyWhitelisted)
                    hiddenFiles++;
                else
                    filtered[kvp.Key] = kvp.Value;
            }

            var uniqueConflictMods = new HashSet<string>();
            foreach (var kvp in filtered)
                foreach (var m in kvp.Value)
                    uniqueConflictMods.Add(m);

            _lblConflictCount.Text = filtered.Count == 0 ? "0" : $"{uniqueConflictMods.Count} mods / {filtered.Count} files";
            _lblConflictCount.ForeColor = filtered.Count == 0 ? Theme.Success : Theme.Danger;

            _lblHiddenCount.Text = hiddenFiles == 0 ? "0" : $"{hiddenFiles} files";
            _lblHiddenCount.ForeColor = hiddenFiles > 0 ? Theme.TextMuted : Theme.TextPrimary;

            // Build RTF
            var rtf = new StringBuilder();
            rtf.Append(@"{\rtf1\ansi\deff0");
            rtf.Append(@"{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}{\f1\fnil\fcharset0 Cascadia Code;}{\f2\fnil\fcharset0 Segoe UI Emoji;}}");
            rtf.Append(@"{\colortbl;"
                + Rtf(Theme.TextPrimary)     // \cf1
                + Rtf(Theme.Success)         // \cf2
                + Rtf(Theme.Danger)          // \cf3
                + Rtf(Theme.Warning)         // \cf4
                + Rtf(Theme.TextMuted)       // \cf5
                + Rtf(Theme.Accent)          // \cf6
                + Rtf(Theme.TextSecondary)   // \cf7
                + "}");

            rtf.Append(@"\viewkind4\uc1\pard\cf7\f0\fs20 ");
            rtf.Append($@"\cf5 [{DateTime.Now:HH:mm:ss}] Scan complete \endash  {_scanner.ScannedMods.Count} mods scanned");
            if (hiddenFiles > 0)
                rtf.Append($@", {hiddenFiles} conflict(s) hidden by whitelist");
            rtf.Append(@"\par\par ");

            if (filtered.Count == 0)
            {
                rtf.Append(@"\cf2\b\fs24\f2 \u10003? \f0 No conflicts detected\b0\fs20\par\par ");
                rtf.Append(@"\cf5 Scanned mods:\par ");
                foreach (var mod in _scanner.UniqueModNames)
                {
                    bool isWl = whitelist.Contains(mod);
                    rtf.Append($@"\cf7  \f2\u8226?\f0  {Esc(mod)}");
                    if (isWl) rtf.Append(@"  \cf5\i (whitelisted)\i0");
                    rtf.Append(@"\par ");
                }
            }
            else
            {
                var grouped = new Dictionary<string, List<string>>();
                foreach (var kvp in filtered)
                {
                    string key = string.Join("\n", kvp.Value);
                    if (!grouped.ContainsKey(key)) grouped[key] = new();
                    grouped[key].Add(kvp.Key);
                }

                rtf.Append($@"\cf3\b\fs22 \f2\u9888?\f0  {uniqueConflictMods.Count} conflicting mods  \f2\u183?\f0  {filtered.Count} overlapping files\b0\fs20\par\par ");

                int groupNum = 0;
                foreach (var group in grouped)
                {
                    groupNum++;
                    rtf.Append(@"\cf5 " + new string('\u2500', 50) + @"\par ");
                    rtf.Append($@"\cf6\b Conflict group #{groupNum}\b0\par ");
                    rtf.Append(@"\cf1 ");
                    foreach (var line in group.Key.Split('\n'))
                        rtf.Append($@"  \f2\u9654?\f0  {Esc(line)}\par ");

                    rtf.Append($@"\par\cf5 Overlapping files ({group.Value.Count}):\par ");
                    foreach (var f in group.Value)
                        rtf.Append($@"\cf7\f1   {Esc(f)}\f0\par ");
                    rtf.Append(@"\par ");
                }
            }

            rtf.Append("}");
            _rtbLog.Rtf = rtf.ToString();
        }

        private static string Rtf(Color c) => $@"\red{c.R}\green{c.G}\blue{c.B};";
        private static string Esc(string t) => t.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}");

        private void RunAutoDetect(bool silent)
        {
            string? steam = _scanner.GetSteamPath();
            if (steam != null)
            {
                _txtSteam.Text = steam;
                string? kcd = _scanner.FindKcd2Path(steam);
                if (kcd != null) _txtKcd2.Text = kcd;
                else if (!silent) MessageBox.Show("Found Steam but KCD2 was not located in the default library.", "Auto-Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (!silent) MessageBox.Show("Could not auto-detect Steam installation.", "Auto-Detect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ValidateAndSave()
        {
            if (!Directory.Exists(_txtKcd2.Text.Trim()))
            {
                MessageBox.Show("Please enter a valid KCD2 installation path.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_txtSteam.Text) && !Directory.Exists(_txtSteam.Text.Trim()))
            {
                MessageBox.Show("The Steam path is invalid. Clear it to skip Workshop scanning.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _config.SteamPath = _txtSteam.Text.Trim();
            _config.Kcd2Path = _txtKcd2.Text.Trim();
            SaveConfig();
            ShowMainView();
        }

        private string? BrowseFolder()
        {
            using var fbd = new FolderBrowserDialog();
            return fbd.ShowDialog() == DialogResult.OK ? fbd.SelectedPath : null;
        }

        private void LoadConfig()
        {
            if (!Directory.Exists(_configFolder)) Directory.CreateDirectory(_configFolder);
            if (File.Exists(_configFile))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configFile));
                    if (cfg != null) _config = cfg;
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            if (!Directory.Exists(_configFolder)) Directory.CreateDirectory(_configFolder);
            try { File.WriteAllText(_configFile, JsonSerializer.Serialize(_config)); } catch { }
        }

        private bool IsConfigValid() => Directory.Exists(_config.Kcd2Path);
    }
}

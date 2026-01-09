using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace KCD2ModConflictChecker
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class AppConfig
    {
        public string SteamPath { get; set; } = "";
        public string Kcd2Path { get; set; } = "";
    }

    public class ModScanner
    {
        public Dictionary<string, List<string>> Conflicts { get; private set; } = new();
        public List<string> ScannedMods { get; private set; } = new();
        
        private Dictionary<string, List<(string Name, string Source, string DisplayName)>> _fileMap = new();
        private static readonly HttpClient _httpClient = new HttpClient();
        private Dictionary<string, string> _nameCache = new Dictionary<string, string>();

        public string? GetSteamPath()
        {
            string? path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(path))
            {
                path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
            }
            return path;
        }

        public string? FindKcd2Path(string steamPath)
        {
            if (string.IsNullOrEmpty(steamPath)) return null;
            string candidate = Path.Combine(steamPath, "steamapps", "common", "KingdomComeDeliverance2");
            return Directory.Exists(candidate) ? candidate : null;
        }

        private List<string> ScanPakContents(string pakPath)
        {
            var files = new List<string>();
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(pakPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/")) continue;
                        files.Add(entry.FullName.Replace('\\', '/').ToLowerInvariant());
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading {pakPath}: {ex.Message}");
            }
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
                    if (match.Success) 
                    {
                        return match.Groups[1].Value.Trim();
                    }
                } 
                catch {}
            }

            if (sourceType == "Workshop" && long.TryParse(folderName, out _))
            {
                if (_nameCache.ContainsKey(folderName)) return _nameCache[folderName];

                try 
                {
                    string url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={folderName}";
                    string html = _httpClient.GetStringAsync(url).Result; 
                    
                    var titleMatch = Regex.Match(html, @"<title>Steam Workshop::(.*?)</title>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success)
                    {
                        string realName = titleMatch.Groups[1].Value.Trim();
                        _nameCache[folderName] = realName;
                        return realName;
                    }
                }
                catch {}
            }

            return sourceType == "Local" ? folderName : $"Workshop {folderName}";
        }

        private void ScanDirectoryForPaks(string rootFolder, string sourceType)
        {
            if (!Directory.Exists(rootFolder)) return;

            var dirs = Directory.GetDirectories(rootFolder);
            
            foreach (var dir in dirs)
            {
                string folderName = Path.GetFileName(dir);
                string modName = ResolveModName(rootFolder, folderName, sourceType);

                var pakFiles = Directory.GetFiles(dir, "*.pak", SearchOption.AllDirectories);
                
                foreach (var fullPath in pakFiles)
                {
                    string pakName = Path.GetFileNameWithoutExtension(fullPath);
                    string displayName;
                    
                    if (pakName.Replace(" ", "").Contains(modName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                        displayName = $"[{sourceType}] {modName}";
                    else
                        displayName = $"[{sourceType}] {modName} ({pakName})";

                    ScannedMods.Add(displayName);

                    var internalFiles = ScanPakContents(fullPath);
                    foreach (var internalFile in internalFiles)
                    {
                        if (!_fileMap.ContainsKey(internalFile))
                            _fileMap[internalFile] = new List<(string, string, string)>();

                        _fileMap[internalFile].Add((modName, sourceType, displayName));
                    }
                }
            }
        }

        public Dictionary<string, List<string>> RunFullScan(string kcd2Path, string steamPath)
        {
            Conflicts.Clear();
            _fileMap.Clear();
            ScannedMods.Clear();

            string localModsPath = Path.Combine(kcd2Path, "Mods");
            ScanDirectoryForPaks(localModsPath, "Local");

            if (!string.IsNullOrEmpty(steamPath))
            {
                string workshopPath = Path.Combine(steamPath, "steamapps", "workshop", "content", "1771300");
                ScanDirectoryForPaks(workshopPath, "Workshop");
            }

            foreach (var kvp in _fileMap)
            {
                var entries = kvp.Value;
                
                var uniqueMods = entries
                    .Select(e => new { e.Name, e.Source })
                    .Distinct()
                    .ToList();

                if (uniqueMods.Count > 1)
                {
                    var conflictingModNames = uniqueMods
                        .Select(m => $"[{m.Source}] {m.Name}")
                        .OrderBy(n => n)
                        .ToList();

                    Conflicts[kvp.Key] = conflictingModNames;
                }
            }

            return Conflicts;
        }
    }

    public class MainForm : Form
    {
        private AppConfig _config = new AppConfig();
        private ModScanner _scanner = new ModScanner();
        
        private string _configFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KCD2MCC");
        private string _configFile => Path.Combine(_configFolder, "config.json");

        private Panel _pnlContainer = null!;
        private Panel _pnlSetup = null!;
        private Panel _pnlMain = null!;
        private DarkTextBox _txtSteam = null!;
        private DarkTextBox _txtKcd2 = null!;
        private DarkButton _btnScan = null!;
        private DarkRichTextBox _rtbLog = null!;

        public static Color ColorBg = Color.FromArgb(32, 32, 32);
        public static Color ColorPanel = Color.FromArgb(45, 45, 45);
        public static Color ColorText = Color.FromArgb(220, 220, 220);
        public static Color ColorAccent = Color.FromArgb(60, 120, 216);
        public static Color ColorAccentHover = Color.FromArgb(80, 140, 230);

        public MainForm()
        {
            this.Text = "KCD2 Mod Conflict Checker";
            this.Size = new Size(800, 700);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = ColorBg;
            this.ForeColor = ColorText;
            this.StartPosition = FormStartPosition.CenterScreen;
            
            try
            {
                if (File.Exists("kcd2.ico"))
                {
                    this.Icon = new Icon("kcd2.ico");
                }
                else
                {
                    var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (exeIcon != null)
                    {
                        this.Icon = exeIcon;
                    }
                }
            }
            catch {}

            InitializeCustomComponents();
            LoadConfig();

            if (IsConfigValid())
                ShowMainView();
            else
                ShowSetupView();
        }

        private void InitializeCustomComponents()
        {
            _pnlContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };
            this.Controls.Add(_pnlContainer);

            _pnlSetup = new Panel { Dock = DockStyle.Fill, Visible = false };
            
            var lblSetupTitle = CreateLabel("KCD2 Mod Conflict Checker", 24, FontStyle.Bold);
            lblSetupTitle.Location = new Point(20, 170);
            lblSetupTitle.AutoSize = false;
            lblSetupTitle.Size = new Size(760, 40);
            lblSetupTitle.TextAlign = ContentAlignment.MiddleCenter;
            _pnlSetup.Controls.Add(lblSetupTitle);

            _pnlSetup.Controls.Add(CreateLabel("Steam Installation:", 10, FontStyle.Regular, 80, 250));
            _txtSteam = new DarkTextBox { Location = new Point(230, 250), Width = 400 };
            _pnlSetup.Controls.Add(_txtSteam);
            var btnBrowseSteam = new DarkButton { Text = "Browse", Location = new Point(640, 248), Size = new Size(80, 26) };
            btnBrowseSteam.Click += (s, e) => { string? p = BrowseFolder(); if (p != null) _txtSteam.Text = p; };
            _pnlSetup.Controls.Add(btnBrowseSteam);

            _pnlSetup.Controls.Add(CreateLabel("KCD2 Installation:", 10, FontStyle.Regular, 80, 300));
            _txtKcd2 = new DarkTextBox { Location = new Point(230, 300), Width = 400 };
            _pnlSetup.Controls.Add(_txtKcd2);
            var btnBrowseKcd = new DarkButton { Text = "Browse", Location = new Point(640, 298), Size = new Size(80, 26) };
            btnBrowseKcd.Click += (s, e) => { string? p = BrowseFolder(); if (p != null) _txtKcd2.Text = p; };
            _pnlSetup.Controls.Add(btnBrowseKcd);

            var btnDetect = new DarkButton { Text = "Auto-Detect Paths", Location = new Point(250, 370), Size = new Size(300, 35) };
            btnDetect.Click += (s, e) => RunAutoDetect();
            _pnlSetup.Controls.Add(btnDetect);

            var btnSave = new DarkButton { Text = "Continue", Location = new Point(250, 430), Size = new Size(300, 45), BackColor = ColorAccent };
            btnSave.Click += (s, e) => ValidateAndSave();
            _pnlSetup.Controls.Add(btnSave);
            _pnlContainer.Controls.Add(_pnlSetup);

            _pnlMain = new Panel { Dock = DockStyle.Fill, Visible = false };
            
            var topBar = new Panel { Dock = DockStyle.Top, Height = 50 };
            
            var lblMainTitle = CreateLabel("KCD2 Mod Conflict Checker", 18, FontStyle.Bold, 0, 7);
            lblMainTitle.AutoSize = true;
            topBar.Controls.Add(lblMainTitle);
            
            var btnSettings = new DarkButton { Text = "⚙ Settings", Size = new Size(100, 30), Location = new Point(640, 10) };
            btnSettings.Click += (s, e) => ShowSetupView();
            topBar.Controls.Add(btnSettings);
            _pnlMain.Controls.Add(topBar);

            var actionArea = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(0, 10, 0, 10) };
            _btnScan = new DarkButton { Text = "SCAN FOR CONFLICTS", Height = 60, Dock = DockStyle.Fill, BackColor = ColorAccent, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
            _btnScan.Click += (s, e) => StartScan();
            actionArea.Controls.Add(_btnScan);
            _pnlMain.Controls.Add(actionArea);

            var logContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
            _rtbLog = new DarkRichTextBox 
            { 
                Dock = DockStyle.Fill, 
                BackColor = ColorPanel, 
                ForeColor = ColorText, 
                BorderStyle = BorderStyle.None, 
                Font = new Font("Consolas", 10), 
                ReadOnly = true, 
                Text = "Ready to scan..." 
            };
            logContainer.Controls.Add(_rtbLog);
            _pnlMain.Controls.Add(logContainer);

            actionArea.SendToBack();
            topBar.SendToBack();
            logContainer.BringToFront();

            _pnlContainer.Controls.Add(_pnlMain);
        }

        private Label CreateLabel(string text, float size, FontStyle style, int x = 0, int y = 0)
        {
            return new Label 
            { 
                Text = text, 
                Font = new Font("Segoe UI", size, style), 
                Location = new Point(x, y), 
                ForeColor = ColorText, 
                AutoSize = true 
            };
        }

        private string? BrowseFolder()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK) return fbd.SelectedPath;
            }
            return null;
        }

        private void RunAutoDetect()
        {
            string? steam = _scanner.GetSteamPath();
            if (steam != null)
            {
                _txtSteam.Text = steam;
                string? kcd = _scanner.FindKcd2Path(steam);
                if (kcd != null) _txtKcd2.Text = kcd;
                else MessageBox.Show("Found Steam, but KCD2 not found.", "Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else MessageBox.Show("Could not auto-detect Steam.", "Detect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ValidateAndSave()
        {
            if (!Directory.Exists(_txtSteam.Text) || !Directory.Exists(_txtKcd2.Text))
            {
                MessageBox.Show("Please enter valid paths.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _config.SteamPath = _txtSteam.Text;
            _config.Kcd2Path = _txtKcd2.Text;
            SaveConfig();
            ShowMainView();
        }

        private void StartScan()
        {
            _btnScan.Enabled = false;
            _btnScan.Text = "Scanning...";
            _rtbLog.Clear();
            _rtbLog.AppendText($"[{DateTime.Now.ToLongTimeString()}] Scan started...\n");
            
            Task.Run(() => {
                var conflicts = _scanner.RunFullScan(_config.Kcd2Path, _config.SteamPath);
                this.Invoke((MethodInvoker)delegate { DisplayResults(conflicts); });
            });
        }

        private void DisplayResults(Dictionary<string, List<string>> conflicts)
        {
            _btnScan.Enabled = true;
            _btnScan.Text = "SCAN FOR CONFLICTS";
            
            _rtbLog.AppendText($"[{DateTime.Now.ToLongTimeString()}] Scan complete.\n\n");
            _rtbLog.SelectionFont = new Font("Segoe UI", 12, FontStyle.Bold);

            if (conflicts.Count == 0)
            {
                _rtbLog.SelectionColor = Color.LightGreen;
                _rtbLog.AppendText("✅ No conflicts found!\n\n");
                _rtbLog.SelectionColor = ColorText;
                _rtbLog.SelectionFont = new Font("Consolas", 10, FontStyle.Regular);
                _rtbLog.AppendText("Scanned Mods:\n");
                foreach (var mod in _scanner.ScannedMods) _rtbLog.AppendText($" - {mod}\n");
            }
            else
            {
                _rtbLog.SelectionColor = Color.FromArgb(255, 100, 100);
                _rtbLog.AppendText($"⚠️ FOUND {conflicts.Count} CONFLICTING FILES ⚠️\n\n");
                _rtbLog.SelectionColor = ColorText;
                _rtbLog.SelectionFont = new Font("Consolas", 10, FontStyle.Regular);

                var grouped = new Dictionary<string, List<string>>();
                foreach (var kvp in conflicts)
                {
                    string key = string.Join("\n", kvp.Value); 
                    if (!grouped.ContainsKey(key)) grouped[key] = new List<string>();
                    grouped[key].Add(kvp.Key);
                }

                foreach (var group in grouped)
                {
                    _rtbLog.AppendText(new string('-', 60) + "\n");
                    _rtbLog.SelectionColor = Color.Yellow;
                    _rtbLog.SelectionFont = new Font("Segoe UI", 12, FontStyle.Bold);
                    _rtbLog.AppendText("CONFLICT DETECTED BETWEEN:\n");
                    _rtbLog.SelectionColor = ColorText;
                    _rtbLog.SelectionFont = new Font("Consolas", 10, FontStyle.Regular);
                    
                    foreach (var line in group.Key.Split('\n'))
                    {
                        _rtbLog.AppendText($"  • {line}\n");
                    }
                    
                    _rtbLog.AppendText($"\nOverlapping Files ({group.Value.Count}):\n");
                    _rtbLog.SelectionColor = Color.Gray;
                    foreach (var f in group.Value)
                    {
                        _rtbLog.AppendText($"  -> {f}\n");
                    }
                    _rtbLog.SelectionColor = ColorText;
                    _rtbLog.AppendText("\n");
                }
            }
            _rtbLog.SelectionStart = 0;
            _rtbLog.ScrollToCaret();
        }

        private void ShowSetupView()
        {
            _pnlMain.Visible = false; _pnlSetup.Visible = true; _pnlSetup.BringToFront();
            _txtSteam.Text = _config.SteamPath; _txtKcd2.Text = _config.Kcd2Path;
            if (string.IsNullOrEmpty(_txtSteam.Text)) RunAutoDetect();
        }

        private void ShowMainView() 
        { 
            _pnlSetup.Visible = false; _pnlMain.Visible = true; _pnlMain.BringToFront(); 
        }

        private void LoadConfig()
        {
            if (!Directory.Exists(_configFolder)) Directory.CreateDirectory(_configFolder);
            
            if (File.Exists(_configFile)) { 
                try { 
                    string json = File.ReadAllText(_configFile); 
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json); 
                    if (cfg != null) _config = cfg; 
                } catch { } 
            }
        }
        private void SaveConfig() { 
            if (!Directory.Exists(_configFolder)) Directory.CreateDirectory(_configFolder);
            try { 
                string json = JsonSerializer.Serialize(_config); 
                File.WriteAllText(_configFile, json); 
            } catch { } 
        }
        private bool IsConfigValid() { return Directory.Exists(_config.SteamPath) && Directory.Exists(_config.Kcd2Path); }
    }

    public class DarkButton : Button
    {
        public DarkButton()
        {
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.BackColor = MainForm.ColorPanel;
            this.ForeColor = MainForm.ColorText;
            this.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            this.Cursor = Cursors.Hand;
            this.FlatAppearance.MouseOverBackColor = MainForm.ColorAccentHover;
        }
    }
    public class DarkTextBox : TextBox
    {
        public DarkTextBox()
        {
            this.BackColor = MainForm.ColorPanel;
            this.ForeColor = MainForm.ColorText;
            this.BorderStyle = BorderStyle.FixedSingle;
            this.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        }
    }
    
    public class DarkRichTextBox : RichTextBox
    {
        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string? pszSubIdList);

        protected override void CreateHandle()
        {
            base.CreateHandle();
            SetWindowTheme(this.Handle, "DarkMode_Explorer", null);
        }
    }
}
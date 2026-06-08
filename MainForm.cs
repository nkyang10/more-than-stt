using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CantoneseDictation;

public class MainForm : Form
{
    // ─── Theme colors ───
    private static readonly Color BgDark = Color.FromArgb(30, 30, 46);
    private static readonly Color BgCard = Color.FromArgb(42, 42, 62);
    private static readonly Color BgInput = Color.FromArgb(54, 54, 80);
    private static readonly Color FgWhite = Color.FromArgb(205, 214, 244);
    private static readonly Color FgAccent = Color.FromArgb(137, 180, 250);
    private static readonly Color FgGreen = Color.FromArgb(166, 227, 161);
    private static readonly Color FgYellow = Color.FromArgb(249, 226, 175);
    private static readonly Color FgRed = Color.FromArgb(243, 139, 168);
    private static readonly Color FgSubtle = Color.FromArgb(108, 112, 134);

    // ─── Controls ───
    private Button btnRecord;
    private Label statusLabel;
    private RichTextBox txtResult;
    private RichTextBox txtCorrection;
    private Button btnLearn;
    private Button btnLoadFile;
    private Label lblTime;
    private Label lblHotwordActive;
    private ComboBox cmbLanguage;
    private TabControl tabControl;
    private ListView historyList;
    private ListView hotwordList;

    // ─── State ───
    private HotwordManager _hotwordMgr = new();
    private SenseVoiceEngine? _engine = null;
    private bool _isRecording = false;
    private string _lastAsrText = "";
    private float _micGain = 2.0f; // default 2x amplification

    public MainForm()
    {
        // Load hotwords
        _hotwordMgr.Load();

        // Wire up AutoUpdater status callback
        AutoUpdater.SetStatusMsg = msg => SetStatus(msg);

        // Initialize engine
        var modelDir = AppDomain.CurrentDomain.BaseDirectory;
        AppLogger.Info($"Model dir: {modelDir}");
        _engine = new SenseVoiceEngine(modelDir);
        try
        {
            AppLogger.Info("Loading model...");
            _engine.Load();
            AppLogger.Info("Model loaded successfully");
        }
        catch (Exception ex) {
            AppLogger.Error("Failed to load model", ex);
            MessageBox.Show($"Failed to load model: {ex.Message}");
        }
        this.Text = "廣東話聽寫測試 v1.0 — SenseVoice + 自作學習";
        this.Size = new Size(1100, 750);
        this.MinimumSize = new Size(900, 600);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = BgDark;
        this.Font = new Font("Segoe UI", 10);

        BuildUI();
        RefreshHistoryTab();
        RefreshHotwordTab();
        RefreshStatusBar();
    }

    private void BuildUI()
    {
        // ─── Top bar ───
        var topBar = new Panel { Height = 50, Dock = DockStyle.Top, BackColor = BgDark };
        var title = new Label
        {
            Text = "🎙️ 廣東話聽寫測試",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = FgWhite,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(15, 12)
        };
        statusLabel = new Label
        {
            Text = "Ready",
            Font = new Font("Segoe UI", 9),
            ForeColor = FgSubtle,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(this.Width - 250, 15),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        topBar.Controls.Add(title);
        topBar.Controls.Add(statusLabel);
        this.Controls.Add(topBar);

        // ─── Tab control ───
        tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            ForeColor = FgWhite,
            Padding = new Point(10, 4)
        };
        tabControl.SelectedIndexChanged += (s, e) => { if (tabControl.SelectedIndex == 3) RefreshStatsTab(); };

        var tab1 = new TabPage("🎤 Dictation") { BackColor = BgDark };
        var tab2 = new TabPage("📋 History") { BackColor = BgDark };
        var tab3 = new TabPage("📚 Hotwords") { BackColor = BgDark };
        var tab4 = new TabPage("⚙️ Stats") { BackColor = BgDark };

        tabControl.TabPages.Add(tab1);
        tabControl.TabPages.Add(tab2);
        tabControl.TabPages.Add(tab3);
        tabControl.TabPages.Add(tab4);
        this.Controls.Add(tabControl);

        // ═══════ Tab 1: Dictation ═══════
        BuildDictationTab(tab1);
        BuildHistoryTab(tab2);
        BuildHotwordTab(tab3);
        BuildStatsTab(tab4);

        // Hotkeys
        this.KeyPreview = true;
        this.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.F5) ToggleRecording();
            else if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LearnFromCorrection(null, EventArgs.Empty); }
            else if (e.Control && e.KeyCode == Keys.L) { e.SuppressKeyPress = true; _ = LoadAudioFile(); }
        };
    }

    private void BuildDictationTab(TabPage tab)
    {
        // Record button row
        var recRow = new Panel { Height = 70, Dock = DockStyle.Top, BackColor = BgDark, Padding = new Padding(15, 15, 15, 5) };

        btnRecord = new Button
        {
            Text = "🔴 開始錄音 (F5)",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = FgGreen,
            BackColor = BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(200, 45),
            Location = new Point(15, 10),
            Cursor = Cursors.Hand
        };
        btnRecord.Click += (s, e) => ToggleRecording();

        cmbLanguage = new ComboBox
        {
            Location = new Point(230, 20),
            Size = new Size(100, 30),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput,
            ForeColor = FgWhite,
            FlatStyle = FlatStyle.Flat
        };
        cmbLanguage.Items.AddRange(new[] { "auto", "yue", "zh", "en" });
        cmbLanguage.SelectedIndex = 0;

        var langLabel = new Label
        {
            Text = "Lang:",
            Location = new Point(230, 0),
            Size = new Size(50, 20),
            ForeColor = FgSubtle,
            BackColor = BgDark
        };

        btnLoadFile = new Button
        {
            Text = "📂 Load Audio...",
            Font = new Font("Segoe UI", 10),
            ForeColor = FgWhite,
            BackColor = BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(130, 35),
            Location = new Point(350, 15),
            Cursor = Cursors.Hand
        };
        btnLoadFile.Click += async (s, e) => await LoadAudioFile();

        var btnOpenLog = new Button
        {
            Text = "📋 Log",
            Font = new Font("Segoe UI", 10),
            ForeColor = FgYellow,
            BackColor = BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(80, 35),
            Location = new Point(500, 15),
            Cursor = Cursors.Hand
        };
        btnOpenLog.Click += (s, e) =>
        {
            var logPath = AppLogger.GetLogPath();
            if (File.Exists(logPath))
            {
                try { System.Diagnostics.Process.Start("explorer", $"/select,\"{logPath}\""); }
                catch { System.Diagnostics.Process.Start("notepad", logPath); }
            }
            else
            {
                MessageBox.Show($"Log not found: {logPath}");
            }
        };
        recRow.Controls.Add(btnOpenLog);

        var btnUpdate = new Button
        {
            Text = "🔄 Update",
            Font = new Font("Segoe UI", 10),
            ForeColor = FgGreen,
            BackColor = BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(90, 35),
            Location = new Point(590, 15),
            Cursor = Cursors.Hand
        };
        btnUpdate.Click += async (s, e) => await CheckForUpdateAsync();
        recRow.Controls.Add(btnUpdate);

        var btnBeta = new Button
        {
            Text = AutoUpdater.UseBetaChannel ? "🧪 Beta ON" : "🧪 Beta",
            Font = new Font("Segoe UI", 9),
            ForeColor = AutoUpdater.UseBetaChannel ? FgYellow : FgSubtle,
            BackColor = AutoUpdater.UseBetaChannel ? Color.FromArgb(69, 69, 117) : BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(80, 30),
            Location = new Point(690, 18),
            Cursor = Cursors.Hand
        };
        btnBeta.Click += (s, e) =>
        {
            AutoUpdater.UseBetaChannel = !AutoUpdater.UseBetaChannel;
            btnBeta.Text = AutoUpdater.UseBetaChannel ? "🧪 Beta ON" : "🧪 Beta";
            btnBeta.ForeColor = AutoUpdater.UseBetaChannel ? FgYellow : FgSubtle;
            btnBeta.BackColor = AutoUpdater.UseBetaChannel ? Color.FromArgb(69, 69, 117) : BgCard;
            SetStatus(AutoUpdater.UseBetaChannel ? "🧪 Beta channel: CI builds" : "✅ Stable channel: releases");
        };
        recRow.Controls.Add(btnBeta);

        // ─── Mic Gain slider ───
        var gainLabel = new Label
        {
            Text = "🔊 Gain:",
            Font = new Font("Segoe UI", 9),
            ForeColor = FgSubtle,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(780, 5)
        };
        var gainTrack = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            Value = (int)(_micGain * 10),
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            Width = 120,
            Location = new Point(830, 2),
            BackColor = BgDark,
            ForeColor = FgWhite,
            Cursor = Cursors.Hand
        };
        var gainVal = new Label
        {
            Text = $"{_micGain:F1}x",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = FgGreen,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(955, 5)
        };
        gainTrack.ValueChanged += (s, e) =>
        {
            _micGain = gainTrack.Value / 10.0f;
            gainVal.Text = $"{_micGain:F1}x";
        };
        recRow.Controls.Add(gainLabel);
        recRow.Controls.Add(gainTrack);
        recRow.Controls.Add(gainVal);

        recRow.Controls.Add(btnRecord);
        recRow.Controls.Add(langLabel);
        recRow.Controls.Add(cmbLanguage);
        recRow.Controls.Add(btnLoadFile);
        tab.Controls.Add(recRow);

        // ASR Result
        var resultLabel = new Label
        {
            Text = "📝 ASR Result:",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = FgAccent,
            BackColor = BgDark,
            Location = new Point(15, 85),
            AutoSize = true
        };
        tab.Controls.Add(resultLabel);

        txtResult = new RichTextBox
        {
            Location = new Point(15, 110),
            Width = this.Width - 50,
            Height = 80,
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 10),
            ReadOnly = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        tab.Controls.Add(txtResult);

        // Info row
        lblTime = new Label
        {
            Text = "⏱ --",
            ForeColor = FgSubtle,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(15, 195)
        };
        lblHotwordActive = new Label
        {
            Text = "",
            ForeColor = FgGreen,
            BackColor = BgDark,
            AutoSize = true,
            Location = new Point(200, 195)
        };

        var btnClearResult = new Button
        {
            Text = "🗑 Clear",
            Font = new Font("Segoe UI", 9),
            ForeColor = FgRed,
            BackColor = BgCard,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(70, 24),
            Location = new Point(this.Width - 90, 193),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Cursor = Cursors.Hand
        };
        btnClearResult.Click += (s, e) => { txtResult.Clear(); txtCorrection.Clear(); };
        tab.Controls.Add(btnClearResult);

        tab.Controls.Add(lblTime);
        tab.Controls.Add(lblHotwordActive);

        // Correction
        var corrLabel = new Label
        {
            Text = "✏️ Correction (edit → click Teach Me! → it learns):",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = FgYellow,
            BackColor = BgDark,
            Location = new Point(15, 220),
            AutoSize = true
        };
        tab.Controls.Add(corrLabel);

        txtCorrection = new RichTextBox
        {
            Location = new Point(15, 245),
            Width = this.Width - 180,
            Height = 60,
            BackColor = BgInput,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        tab.Controls.Add(txtCorrection);

        btnLearn = new Button
        {
            Text = "🧠 Teach Me!",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = FgAccent,
            BackColor = Color.FromArgb(69, 69, 117),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Location = new Point(this.Width - 155, 245),
            Size = new Size(140, 60),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnLearn.Click += LearnFromCorrection;
        tab.Controls.Add(btnLearn);
    }

    private void BuildHistoryTab(TabPage tab)
    {
        historyList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            BackColor = BgCard,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            FullRowSelect = true,
            GridLines = false
        };
        historyList.Columns.Add("Time", 150);
        historyList.Columns.Add("Original", 300);
        historyList.Columns.Add("Corrected", 300);
        historyList.Columns.Add("Learned", 200);
        tab.Controls.Add(historyList);
    }

    private void BuildHotwordTab(TabPage tab)
    {
        var topRow = new Panel { Height = 40, Dock = DockStyle.Top, BackColor = BgDark, Padding = new Padding(10) };

        var lblWord = new Label { Text = "Word:", ForeColor = FgWhite, BackColor = BgDark, AutoSize = true, Location = new Point(10, 10) };
        var txtWord = new TextBox { Location = new Point(55, 7), Width = 150, BackColor = BgInput, ForeColor = FgWhite, BorderStyle = BorderStyle.FixedSingle };
        var lblWeight = new Label { Text = "Weight:", ForeColor = FgWhite, BackColor = BgDark, AutoSize = true, Location = new Point(220, 10) };
        var txtWeight = new TextBox { Location = new Point(275, 7), Width = 50, BackColor = BgInput, ForeColor = FgWhite, BorderStyle = BorderStyle.FixedSingle, Text = "20" };
        var btnAdd = new Button
        {
            Text = "➕ Add",
            Location = new Point(340, 5), Size = new Size(80, 28),
            ForeColor = FgAccent, BackColor = BgCard, FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand
        };

        var lblFilter = new Label { Text = "🔍 Filter:", ForeColor = FgWhite, BackColor = BgDark, AutoSize = true, Location = new Point(430, 10) };
        _hotwordFilterBox = new TextBox { Location = new Point(480, 7), Width = 120, BackColor = BgInput, ForeColor = FgWhite, BorderStyle = BorderStyle.FixedSingle };

        topRow.Controls.AddRange(new Control[] { lblWord, txtWord, lblWeight, txtWeight, btnAdd, lblFilter, _hotwordFilterBox });
        tab.Controls.Add(topRow);

        hotwordList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            BackColor = BgCard,
            ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            FullRowSelect = true
        };
        hotwordList.Columns.Add("Word", 250);
        hotwordList.Columns.Add("Weight", 100);
        hotwordList.Columns.Add("Source", 200);
        tab.Controls.Add(hotwordList);

        // Bottom row
        var bottomRow = new Panel { Height = 40, Dock = DockStyle.Bottom, BackColor = BgDark, Padding = new Padding(10) };

        var statLabel = new Label
        {
            Text = "",
            ForeColor = FgSubtle, BackColor = BgDark, AutoSize = true,
            Location = new Point(10, 10)
        };

        var btnRemove = new Button
        {
            Text = "🗑️ Remove Selected",
            Location = new Point(200, 5), Size = new Size(150, 28),
            ForeColor = FgRed, BackColor = BgCard, FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand
        };

        var btnClearAll = new Button
        {
            Text = "🧹 Clear All",
            Location = new Point(360, 5), Size = new Size(100, 28),
            ForeColor = FgYellow, BackColor = BgCard, FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand
        };

        bottomRow.Controls.Add(statLabel);
        bottomRow.Controls.Add(btnRemove);
        bottomRow.Controls.Add(btnClearAll);
        tab.Controls.Add(bottomRow);

        btnAdd.Click += (s, e) =>
        {
            var w = txtWord.Text.Trim();
            if (string.IsNullOrEmpty(w)) return;
            int.TryParse(txtWeight.Text, out var weight);
            if (weight <= 0) weight = 20;
            _hotwordMgr.AddManual(w, weight);
            txtWord.Clear();
            RefreshHotwordTab();
            RefreshStatusBar();
        };

        btnRemove.Click += (s, e) =>
        {
            if (hotwordList.SelectedItems.Count > 0)
            {
                var word = hotwordList.SelectedItems[0].Text;
                var confirm = MessageBox.Show($"Remove \"{word}\" from hotwords?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm == DialogResult.Yes)
                {
                    _hotwordMgr.Remove(word);
                    RefreshHotwordTab();
                    RefreshStatusBar();
                }
            }
        };

        btnClearAll.Click += (s, e) =>
        {
            if (_hotwordMgr.Hotwords.Count == 0) return;
            var confirm = MessageBox.Show($"Remove ALL {_hotwordMgr.Hotwords.Count} hotwords?\nThis cannot be undone.",
                "Clear All Hotwords", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm == DialogResult.Yes)
            {
                foreach (var w in _hotwordMgr.Hotwords.Keys.ToList())
                    _hotwordMgr.Remove(w);
                RefreshHotwordTab();
                RefreshStatusBar();
            }
        };

        _hotwordFilterBox.TextChanged += (s, e) => RefreshHotwordTab();

        _hotwordTabStatLabel = statLabel;
    }

    private void BuildStatsTab(TabPage tab)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = BgDark, Padding = new Padding(15) };
        tab.Controls.Add(panel);

        // Stats label
        var lblTitle = new Label
        {
            Text = "🧠 Learning Statistics",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = FgGreen, BackColor = BgDark, AutoSize = true,
            Location = new Point(15, 15)
        };
        panel.Controls.Add(lblTitle);

        var statsBox = new RichTextBox
        {
            Location = new Point(15, 50),
            Width = 500, Height = 300,
            BackColor = BgCard, ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Consolas", 11)
        };
        panel.Controls.Add(statsBox);

        var aboutBox = new RichTextBox
        {
            Location = new Point(15, 370),
            Width = 700, Height = 200,
            BackColor = BgCard, ForeColor = FgWhite,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Segoe UI", 10)
        };
        aboutBox.Text = @"=== 點運作（iPhone 式學習）===

你講「ComfyUI」     → ASR 出 「comefi u i」
你改做「ComfyUI」   → 按 Teach Me!
                     → 系統自動將 ComfyUI 加入 hotword list
                     → 下次再講 → 認得到 ✅

越改越準！每個 correction 加權重 +5。

技術: SenseVoiceSmall + Hotword boost
語言: 廣東話/English code-switching
速度: CPU real-time (30-50x)";
        panel.Controls.Add(aboutBox);

        _statsBox = statsBox;
    }

    private RichTextBox _statsBox;
    private Label? _hotwordTabStatLabel;
    private TextBox? _hotwordFilterBox;

    // ═══════════════════════════════════════════════════
    //  Actions
    // ═══════════════════════════════════════════════════

    private async void ToggleRecording()
    {
        if (_isRecording || _engine == null) return;

        _isRecording = true;
        AppLogger.Info("=== Recording START ===");
        btnRecord.Text = "⏹️ Recording... (F5)";
        btnRecord.ForeColor = FgRed;
        SetStatus("🎤 Recording...");

        try
        {
            var lang = cmbLanguage.SelectedItem?.ToString() ?? "auto";
            AppLogger.Info($"Language: {lang}");

            // Record using WASAPI
            var waveIn = new NAudio.Wave.WaveInEvent
            {
                WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            var recordedSamples = new List<byte>();
            waveIn.DataAvailable += (s, args) =>
            {
                recordedSamples.AddRange(args.Buffer.Take(args.BytesRecorded));
            };

            waveIn.StartRecording();
            SetStatus("🎤 Recording... (5s)");
            AppLogger.Info("Recording started (5s)...");
            await Task.Delay(5000);
            waveIn.StopRecording();
            AppLogger.Info($"Recording stopped: {recordedSamples.Count} bytes ({recordedSamples.Count / 32000.0:F2}s)");

            if (recordedSamples.Count < 16000) // less than ~0.5s of audio
            {
                AppLogger.Warn($"Recording too short: {recordedSamples.Count} bytes");
                SetStatus("⚠️ Too short! Speak longer or check mic", true);
                _isRecording = false;
                btnRecord.Text = "🔴 開始錄音 (F5)";
                btnRecord.ForeColor = FgGreen;
                return;
            }

            // Save to temp file + keep a debug copy
            var tmpPath = Path.GetTempFileName() + ".wav";
            var debugPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                $"last_recording_{DateTime.Now:HHmmss}.wav"
            );

            var shortSamples = new short[recordedSamples.Count / 2];
            Buffer.BlockCopy(recordedSamples.ToArray(), 0, shortSamples, 0, recordedSamples.Count);

            // Apply mic gain amplification
            if (Math.Abs(_micGain - 1.0f) > 0.01f)
            {
                for (int i = 0; i < shortSamples.Length; i++)
                {
                    int amplified = (int)(shortSamples[i] * _micGain);
                    shortSamples[i] = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
                }
                AppLogger.Info($"Mic gain applied: {_micGain:F1}x");
            }

            using (var writer = new NAudio.Wave.WaveFileWriter(tmpPath, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            {
                writer.WriteSamples(shortSamples, 0, shortSamples.Length);
            }
            // Debug copy
            File.Copy(tmpPath, debugPath, true);
            AppLogger.Info($"Saved audio: {tmpPath} ({new FileInfo(tmpPath).Length} bytes)");
            AppLogger.Info($"Debug copy: {debugPath}");

            SetStatus("🧠 Transcribing...");
            var result = _engine.Transcribe(tmpPath, lang, _hotwordMgr.Hotwords);
            File.Delete(tmpPath);

            if (string.IsNullOrEmpty(result.Text))
            {
                AppLogger.Warn("Empty transcription result!");
                SetStatus("❌ No speech detected. Try speaking louder/clearer", true);
                txtResult.Text = "[no speech detected]";
                txtCorrection.Text = "";
                lblTime.Text = $"⏱ {result.TimeSeconds:F3}s | lang: {lang}";
            }
            else
            {
                AppLogger.Info($"Result shown to user: \"{result.Text}\"");
                ShowResult(result.Text, result.TimeSeconds, lang, "");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Recording error", ex);
            SetStatus($"❌ Error: {ex.Message}", true);
        }
        finally
        {
            _isRecording = false;
            btnRecord.Text = "🔴 開始錄音 (F5)";
            btnRecord.ForeColor = FgGreen;
            AppLogger.Info("=== Recording END ===");
        }
    }

    private async Task LoadAudioFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Audio files (*.wav;*.mp3;*.ogg;*.m4a)|*.wav;*.mp3;*.ogg;*.m4a|All files (*.*)|*.*"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        SetStatus($"📂 Processing {Path.GetFileName(dlg.FileName)}...");

        try
        {
            var lang = cmbLanguage.SelectedItem?.ToString() ?? "auto";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => _engine!.Transcribe(dlg.FileName, lang, _hotwordMgr.Hotwords));
            sw.Stop();

            ShowResult(result.Text, result.TimeSeconds, lang, "");
        }
        catch (Exception ex)
        {
            SetStatus($"❌ Error: {ex.Message}", true);
        }
    }

    private void ShowResult(string text, double seconds, string lang, string hotword)
    {
        _lastAsrText = text;
        txtResult.Text = text;
        txtCorrection.Text = text;

        if (seconds > 0)
        {
            var ratio = 7.7 / seconds;
            lblTime.Text = $"⏱ {seconds:F3}s ({ratio:F0}x realtime) | lang: {lang}";
        }

        var hwCount = _hotwordMgr.Hotwords.Count;
        lblHotwordActive.Text = hwCount > 0 ? $"🧠 {hwCount} hotwords active" : "";
        SetStatus($"✅ Transcribed in {seconds:F3}s");
    }

    private void LearnFromCorrection(object? sender, EventArgs e)
    {
        var asrText = txtResult.Text.Trim();
        var corrected = txtCorrection.Text.Trim();

        if (string.IsNullOrEmpty(corrected))
        {
            SetStatus("✏️ Enter a correction first", true);
            return;
        }

        if (corrected == asrText)
        {
            SetStatus("No changes to learn from — edit the text first", true);
            return;
        }

        var learned = _hotwordMgr.LearnFromCorrection(asrText, corrected);
        if (learned.Count > 0)
        {
            SetStatus($"🧠 Learned: {string.Join(", ", learned)} ✅");
            RefreshHistoryTab();
            RefreshHotwordTab();
            RefreshStatusBar();

            // Update the result box to show corrected version
            txtResult.Text = corrected;
        }
        else
        {
            SetStatus("No new words to learn (common words are skipped)");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Refresh
    // ═══════════════════════════════════════════════════

    private void RefreshHistoryTab()
    {
        historyList.Items.Clear();
        foreach (var c in _hotwordMgr.GetRecentCorrections(50))
        {
            var item = new ListViewItem(c.timestamp);
            item.SubItems.Add(c.original.Length > 60 ? c.original[..60] : c.original);
            item.SubItems.Add(c.corrected.Length > 60 ? c.corrected[..60] : c.corrected);
            item.SubItems.Add(string.Join(", ", c.learned_words));
            historyList.Items.Add(item);
        }
    }

    private void RefreshHotwordTab()
    {
        var filter = _hotwordFilterBox?.Text.Trim().ToLowerInvariant() ?? "";
        hotwordList.Items.Clear();
        foreach (var (word, weight) in _hotwordMgr.Hotwords.OrderByDescending(kv => kv.Value))
        {
            if (!string.IsNullOrEmpty(filter) && !word.ToLowerInvariant().Contains(filter))
                continue;
            var item = new ListViewItem(word);
            item.SubItems.Add(weight.ToString());
            var source = _hotwordMgr.HotwordSources.TryGetValue(word, out var s) ? s : "learned";
            item.SubItems.Add(source == "manual" ? "✋ Manual" : "🧠 Learned");
            hotwordList.Items.Add(item);
        }
        if (_hotwordTabStatLabel != null)
            _hotwordTabStatLabel.Text = $"📊 {hotwordList.Items.Count} / {_hotwordMgr.Hotwords.Count} hotwords";
    }

    private void RefreshStatsTab()
    {
        if (_statsBox == null) return;
        var stats = _hotwordMgr.GetStats();
        var text = $"Total hotwords: {stats.totalHotwords}\n";
        text += $"Total corrections: {stats.totalCorrections}\n\n";
        text += "Top hotwords:\n";
        foreach (var (word, weight) in stats.topHotwords)
            text += $"  · {word}: weight={weight}\n";
        _statsBox.Text = text;
    }

    private void RefreshStatusBar()
    {
        var stats = _hotwordMgr.GetStats();
        statusLabel.Text = $"🧠 {stats.totalHotwords} words | 📋 {stats.totalCorrections} corrections";
        this.Text = $"廣東話聽寫測試 v1.0 — {stats.totalHotwords} hotwords, {stats.totalCorrections} corrections";
    }

    private void SetStatus(string msg, bool isError = false)
    {
        statusLabel.Text = msg;
        statusLabel.ForeColor = isError ? FgRed : FgSubtle;
    }

    // ═══════════════════════════════════════════════════
    //  Auto-Update
    // ═══════════════════════════════════════════════════

    private async Task CheckForUpdateAsync()
    {
        // Check if an update was already downloaded
        if (AutoUpdater.IsUpdatePending)
        {
            var msg = "An update has already been downloaded.\n\nRestart to apply?";
            var restart = MessageBox.Show(msg, "Update Ready",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (restart == DialogResult.Yes)
            {
                AutoUpdater.ApplyPendingUpdate();
            }
            return;
        }

        SetStatus("🔍 Checking for updates...");
        AppLogger.Info("Checking for updates...");

        var info = await AutoUpdater.CheckForUpdate();

        if (info == null)
        {
            SetStatus("❌ Update check failed (no network?)", true);
            return;
        }

        if (!info.IsNewer)
        {
            SetStatus($"✅ You're up to date (v{AutoUpdater.CurrentVersion})");
            MessageBox.Show($"You're on the latest version (v{AutoUpdater.CurrentVersion}).",
                "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(
            $"New version available: {info.Version}\n\n{info.ReleaseNotes}\n\nCurrent: v{AutoUpdater.CurrentVersion}\n\nDownload & install?",
            "Update Available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            SetStatus("⬇️ Downloading update...");
            await AutoUpdater.DownloadAndInstall(info, this);
            // DownloadAndInstall handles restart prompt internally
        }
    }
}

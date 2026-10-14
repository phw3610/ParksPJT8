using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenRecorder
{
    internal sealed class MainForm : Form
    {
        private const int WmHotKey = 0x0312;
        private const int HotKeyId = 1;

        // Layout is in 96-dpi units; AutoScaleMode.Dpi scales it for the monitor.
        private const int FormWidth = 440;
        private const int Gutter = 20;
        private const int ContentWidth = FormWidth - 2 * Gutter;
        private const int LabelWidth = 130;
        private const int FieldX = 150;
        private const int FieldWidth = ContentWidth - FieldX - 16;

        private static readonly int[] ScaleWidths = { 0, 1920, 1280, 960, 640 };
        private static readonly string[] ScaleNames = { "원본", "1920", "1280", "960", "640" };
        private static readonly string[] PresetNames = { "저용량", "균형", "고화질", "최고" };
        private static readonly int[] PresetKbps = { 1500, 3000, 6000, 12000 };

        private readonly AppSettings _s = AppSettings.Load();
        private readonly Recorder _recorder = new Recorder();
        private readonly ApiServer _api;
        private readonly Compressor _compressor = new Compressor();
        private readonly Icon _idleIcon = MakeIcon(false);
        private readonly Icon _recordingIcon = MakeIcon(true);
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly ToolStripMenuItem _trayToggle = new ToolStripMenuItem();

        private RecordButton _recordButton;
        private Label _statusLabel;
        private Card _recordingCard;
        private ComboBox _monitorBox;
        private Segmented _scaleSeg, _presetSeg;
        private NumericUpDown _bitrateBox, _fpsBox, _partBox, _portBox;
        private Label _folderLabel, _apiStatus;
        private Button _scheduleButton;
        private Segmented _compressSeg, _captureSeg;
        private CheckBox _hardwareCheck;
        private Button _optionsToggle;
        private Card _optionsCard, _controlCard;
        private bool _optionsOpen;
        private Label _compressCaption, _downloadHint;
        private Button _downloadButton;
        private CancellationTokenSource _download;   // ffmpeg download in progress
        private TextBox _tokenBox;
        private HotKeyBox _hotKeyBox;
        private CheckBox _apiCheck;
        private readonly ToolTip _tips = new ToolTip();

        private bool _loading = true;
        private bool _startHidden;
        private bool _exiting;
        private bool _trayHintShown;
        private bool _lastActive;
        private string _hotKeyError = string.Empty;

        // Scheduled recording (saved in the settings). _windowEnd is when the running or
        // waiting window ends; _doneThrough is the end of the last window already handled,
        // so a window that was stopped by hand is not restarted.
        private Schedule _schedule;
        private DateTime _windowEnd, _doneThrough = DateTime.MinValue;
        private bool _scheduleStarted;   // the schedule (not the user) began the running recording

        public MainForm(bool startHidden, EventWaitHandle showSignal)
        {
            _startHidden = startHidden;
            _api = new ApiServer(_recorder, () => _s.Clone());
            _schedule = Schedule.Parse(_s.ScheduleText);

            BuildUi();
            BuildTray();
            LoadIntoUi();

            _recorder.Message += OnRecorderMessage;
            _compressor.Message += OnRecorderMessage;
            _recorder.Finished += (settings, files) => { if (settings.Compress && Compressor.Available) _compressor.Enqueue(files, settings.KeepOriginal); };
            _timer.Interval = 300;
            _timer.Tick += delegate { CheckSchedule(); RefreshUi(); };
            _timer.Start();

            // A second launch of the program just brings this window forward.
            ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
            {
                try { BeginInvoke(new Action(ShowWindow)); }
                catch (InvalidOperationException) { }
            }, null, -1, false);

            _loading = false;
            RefreshUi();
        }

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            Text = "화면 녹화";
            Icon = _idleIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Ui(9F);
            BackColor = Theme.Bg;

            _recordButton = new RecordButton { Left = Gutter, Top = 16, Width = ContentWidth, Height = 56 };
            _recordButton.Click += delegate { ToggleRecording(); };
            Controls.Add(_recordButton);

            _statusLabel = new Label
            {
                Left = Gutter + 4, Top = 78, Width = ContentWidth - 8 - 104, Height = 36,
                AutoSize = false, Font = Theme.Ui(9.5F), UseMnemonic = false,
                BackColor = Theme.Bg, ForeColor = Theme.Muted
            };
            Controls.Add(_statusLabel);

            _scheduleButton = FlatButton("예약 녹화");
            _scheduleButton.SetBounds(Gutter + ContentWidth - 96, 82, 96, 28);
            _scheduleButton.Click += delegate { EditSchedule(); };
            Controls.Add(_scheduleButton);

            // ---- main recording settings (the rest is behind "기타 옵션")
            _recordingCard = new Card("녹화 설정") { Left = Gutter, Top = 122, Width = ContentWidth, Height = 204 };
            Controls.Add(_recordingCard);

            Caption(_recordingCard, "모니터", 40);
            _monitorBox = Place(_recordingCard, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, FieldX, 40, FieldWidth);
            _monitorBox.SelectedIndexChanged += delegate { if (!_loading) { _s.Monitor = _monitorBox.SelectedIndex; Save(); } };

            Caption(_recordingCard, "해상도 (가로 px)", 76);
            _scaleSeg = Place(_recordingCard, new Segmented(ScaleNames), FieldX, 76, FieldWidth);
            _scaleSeg.UserSelected += delegate
            {
                _s.ScaleWidth = ScaleWidths[Math.Max(0, _scaleSeg.SelectedIndex)];
                Save();
            };

            Caption(_recordingCard, "화질", 112);
            _presetSeg = Place(_recordingCard, new Segmented(PresetNames), FieldX, 112, FieldWidth);
            _presetSeg.UserSelected += delegate { _bitrateBox.Value = PresetKbps[_presetSeg.SelectedIndex]; };

            Caption(_recordingCard, "저장 폴더", 148);
            Button open = Place(_recordingCard, FlatButton("열기"), FieldX + FieldWidth - 58, 148, 58);
            open.Click += delegate { OpenFolder(); };
            Button browse = Place(_recordingCard, FlatButton("변경"), FieldX + FieldWidth - 122, 148, 58);
            browse.Click += delegate { ChooseFolder(); };
            _folderLabel = new Label
            {
                Left = 16, Top = 180, Width = ContentWidth - 32, Height = 18, AutoSize = false, AutoEllipsis = true,
                UseMnemonic = false, Font = Theme.Ui(8.5F), ForeColor = Theme.Muted, BackColor = Theme.Card
            };
            _recordingCard.Controls.Add(_folderLabel);

            _optionsToggle = FlatButton(string.Empty);
            _optionsToggle.SetBounds(Gutter, _recordingCard.Bottom + 10, ContentWidth, 32);
            _optionsToggle.Click += delegate { SetOptionsOpen(!_optionsOpen); };
            Controls.Add(_optionsToggle);

            // ---- other options: bitrate, frame rate, capture method, hardware encoding, compression
            _optionsCard = new Card("기타 옵션") { Left = Gutter, Top = _optionsToggle.Bottom + 10, Width = ContentWidth, Height = 228, Visible = false };
            Controls.Add(_optionsCard);

            Caption(_optionsCard, "비트레이트", 40);
            _bitrateBox = Place(_optionsCard, NewNumber(AppSettings.MinBitrateKbps, AppSettings.MaxBitrateKbps, 100), FieldX, 40, 84);
            Hint(_optionsCard, "kbps · 높을수록 선명", FieldX + 90, 40, FieldWidth - 90);
            _bitrateBox.ValueChanged += delegate
            {
                if (_loading) return;
                _s.BitrateKbps = (int)_bitrateBox.Value;
                SelectPreset();
                Save();
            };

            Caption(_optionsCard, "프레임 · 분할", 76);
            _fpsBox = Place(_optionsCard, NewNumber(AppSettings.MinFps, AppSettings.MaxFps, 1), FieldX, 76, 52);
            Hint(_optionsCard, "fps", FieldX + 56, 76, 26);
            _fpsBox.ValueChanged += delegate { if (!_loading) { _s.Fps = (int)_fpsBox.Value; Save(); } };
            _partBox = Place(_optionsCard, NewNumber(AppSettings.MinPartMegabytes, AppSettings.MaxPartMegabytes, 50), FieldX + 88, 76, 68);
            Hint(_optionsCard, "MB 분할", FieldX + 160, 76, FieldWidth - 160);
            _partBox.ValueChanged += delegate { if (!_loading) { _s.PartMegabytes = (int)_partBox.Value; Save(); } };

            // for black or broken videos: another capture route, and the GPU encoder off
            Caption(_optionsCard, "캡처 방식", 112);
            _captureSeg = Place(_optionsCard, new Segmented("자동", "Windows", "GDI"), FieldX, 112, FieldWidth);
            _tips.SetToolTip(_captureSeg,
                "화면을 가져오는 방법입니다. 일부 화면이 검게 녹화될 때 바꿔서 시험해 보세요.\n" +
                "자동: GPU 화면 복제(DXGI), 안 되면 GDI\n" +
                "Windows: Windows 그래픽 캡처. 자동에서 검게 나오는 창에 효과가 있는 경우가 있습니다.\n" +
                "GDI: 가장 오래된 방식. 다른 방식에서 검게 나올 때 쓰며, 이 PC에서 2560×1440 약 30fps까지 나옵니다.\n" +
                "저작권 보호(DRM) 영상은 어느 방식에서도 검게 나오며, 우회하지 않습니다.");
            _captureSeg.UserSelected += delegate { _s.CaptureMode = Math.Max(0, _captureSeg.SelectedIndex); Save(); };

            Caption(_optionsCard, "하드웨어 가속", 148);
            _hardwareCheck = Place(_optionsCard, new CheckBox { Text = "GPU 인코더 사용", BackColor = Theme.Card, ForeColor = Theme.Text }, FieldX, 148, FieldWidth);
            _tips.SetToolTip(_hardwareCheck,
                "영상 압축(H.264)에 그래픽카드를 씁니다. 녹화 영상이 검거나 깨지면 끄고 시험해 보세요.\n" +
                "끄면 소프트웨어 인코더를 쓰므로 메모리와 CPU를 더 사용합니다.");
            _hardwareCheck.CheckedChanged += delegate { if (!_loading) { _s.HardwareEncoder = _hardwareCheck.Checked; Save(); } };

            BuildCompressRow();

            // ---- hotkey and API (also under "기타 옵션")
            _controlCard = new Card("단축키 · API") { Left = Gutter, Top = _optionsCard.Bottom + 12, Width = ContentWidth, Height = 152, Visible = false };
            Controls.Add(_controlCard);
            Card controlCard = _controlCard;

            Caption(controlCard, "시작/중지 단축키", 40);
            _hotKeyBox = Place(controlCard, new HotKeyBox { BorderStyle = BorderStyle.FixedSingle }, FieldX, 40, FieldWidth);
            _tips.SetToolTip(_hotKeyBox, "입력칸을 클릭한 뒤 원하는 조합(Ctrl/Alt/Shift + 키)을 누르세요. Delete: 해제");
            _hotKeyBox.Changed += delegate
            {
                _s.HotKeyModifiers = _hotKeyBox.Modifiers;
                _s.HotKeyCode = _hotKeyBox.KeyCode;
                Save();
                RegisterHotKey();
                RefreshUi();
            };

            Caption(controlCard, "API (이 PC 전용)", 76);
            _apiCheck = Place(controlCard, new CheckBox { Text = "사용", BackColor = Theme.Card, ForeColor = Theme.Text }, FieldX, 76, 60);
            _apiStatus = new Label
            {
                Left = FieldX + 66, Top = 76, Width = FieldWidth - 66, Height = 28, AutoSize = false, AutoEllipsis = true,
                UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Ui(8.5F),
                ForeColor = Theme.Muted, BackColor = Theme.Card
            };
            controlCard.Controls.Add(_apiStatus);
            _apiCheck.CheckedChanged += delegate { if (!_loading) { _s.ApiEnabled = _apiCheck.Checked; Save(); ApplyApi(); } };

            Caption(controlCard, "포트 · 토큰", 112);
            _portBox = Place(controlCard, NewNumber(AppSettings.MinPort, AppSettings.MaxPort, 1), FieldX, 112, 70);
            _portBox.ValueChanged += delegate { if (!_loading) { _s.ApiPort = (int)_portBox.Value; Save(); ApplyApi(); } };
            _tokenBox = Place(controlCard, new TextBox { ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White }, FieldX + 76, 112, FieldWidth - 76 - 64);
            Button copy = Place(controlCard, FlatButton("복사"), FieldX + FieldWidth - 58, 112, 58);
            _tips.SetToolTip(_tokenBox, "API 요청에 필요한 토큰 (?token=… 또는 Authorization: Bearer)");
            copy.Click += delegate
            {
                try { Clipboard.SetText(_s.ApiToken); Notify("토큰을 복사했습니다."); }
                catch (Exception ex) { Notify("복사하지 못했습니다: " + ex.Message); }
            };

            SetOptionsOpen(false);
        }

        // "기타 옵션" (bitrate, frame rate, capture method, compression, hotkey, API) stays folded
        // away until asked for; the window grows and shrinks with it.
        private void SetOptionsOpen(bool open)
        {
            _optionsOpen = open;
            _optionsCard.Visible = open;
            _controlCard.Visible = open;
            _optionsToggle.Text = open ? "기타 옵션 닫기  ▲" : "기타 옵션 열기  ▼";

            int bottom = open ? _controlCard.Bottom : _optionsToggle.Bottom;
            if (!IsHandleCreated)
            {
                ClientSize = new Size(FormWidth, bottom + 16);   // still laid out in 96-dpi units
                return;
            }

            ClientSize = new Size(ClientSize.Width, bottom + (int)Math.Round(16 * DeviceDpi / 96.0));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
        }

        // The "추가 압축" row: the choice when ffmpeg.exe is next to the program, otherwise a download button.
        private void BuildCompressRow()
        {
            foreach (Control old in new Control[] { _compressCaption, _compressSeg, _downloadButton, _downloadHint })
            {
                if (old == null) continue;
                _optionsCard.Controls.Remove(old);
                old.Dispose();
            }

            _compressSeg = null;
            _downloadButton = null;
            _compressCaption = Caption(_optionsCard, "추가 압축", 184);

            if (Compressor.Available)
            {
                _compressSeg = Place(_optionsCard, new Segmented("안 함", "원본 교체", "원본 유지"), FieldX, 184, FieldWidth);
                _compressSeg.SelectedIndex = !_s.Compress ? 0 : _s.KeepOriginal ? 2 : 1;
                _tips.SetToolTip(_compressSeg,
                    "녹화가 끝나면 ffmpeg로 x264(CRF 28) 재인코딩해 용량을 크게 줄입니다.\n" +
                    "원본 교체: 결과가 더 작을 때만 원본을 대체합니다.\n" +
                    "원본 유지: 압축본을 \"이름-small.mp4\"로 따로 저장하고 원본은 그대로 둡니다.\n" +
                    "압축 중 프로그램을 종료하면 원본이 남습니다.");
                _compressSeg.UserSelected += delegate
                {
                    _s.Compress = _compressSeg.SelectedIndex > 0;
                    _s.KeepOriginal = _compressSeg.SelectedIndex == 2;
                    Save();
                };
                return;
            }

            _downloadButton = Place(_optionsCard, FlatButton("ffmpeg 받기"), FieldX, 184, 92);
            _downloadHint = Hint(_optionsCard, "용량 절감 · 115MB", FieldX + 98, 184, FieldWidth - 98);
            _tips.SetToolTip(_downloadButton,
                "ffmpeg.exe가 없어 추가 압축을 쓸 수 없습니다.\n" +
                "누르면 gyan.dev에서 받아 프로그램 옆에 저장합니다(약 115MB, SHA-256 확인).");
            _downloadButton.Click += delegate { DownloadFfmpeg(); };
        }

        private async void DownloadFfmpeg()
        {
            if (_download != null) return;
            string folder = Path.GetDirectoryName(FfmpegDownloader.Destination);
            DialogResult answer = MessageBox.Show(this,
                "ffmpeg를 gyan.dev에서 내려받아 프로그램 폴더에 저장합니다(약 115MB).\n" + folder +
                "\n\n받은 파일은 게시된 SHA-256과 대조해 확인합니다. 계속할까요?",
                "ffmpeg 받기", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            CancellationTokenSource cancel = new CancellationTokenSource();
            _download = cancel;
            _downloadButton.Enabled = false;
            Progress<int> progress = new Progress<int>(percent =>
            {
                if (_downloadButton != null) _downloadButton.Text = percent < 100 ? "받는 중 " + percent + "%" : "설치 중…";
            });

            string error = await Task.Run(() => FfmpegDownloader.Download(progress, cancel.Token));
            _download = null;
            cancel.Dispose();
            if (IsDisposed || _exiting) return;

            if (error != null)
            {
                _downloadButton.Text = "ffmpeg 받기";
                _downloadButton.Enabled = true;
                MessageBox.Show(this, error, "ffmpeg 받기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            BuildCompressRow();
            Notify("ffmpeg를 설치했습니다. 이제 추가 압축을 선택할 수 있습니다.");
        }

        private static Label Caption(Control parent, string text, int y)
        {
            Label label = new Label
            {
                Text = text, Left = 16, Top = y, Width = LabelWidth, Height = 28, AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, ForeColor = Theme.Text, BackColor = Theme.Card
            };
            parent.Controls.Add(label);
            return label;
        }

        private static Label Hint(Control parent, string text, int x, int y, int width)
        {
            Label label = new Label
            {
                Text = text, Left = x, Top = y, Width = width, Height = 28, AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
                Font = Theme.Ui(8.5F), ForeColor = Theme.Muted, BackColor = Theme.Card
            };
            parent.Controls.Add(label);
            return label;
        }

        private static T Place<T>(Control parent, T control, int x, int y, int width) where T : Control
        {
            control.Left = x;
            control.Top = y;
            control.Width = width;
            if (control is Segmented || control is Button) control.Height = 28;
            else control.Top = y + (28 - control.Height) / 2;
            parent.Controls.Add(control);
            return control;
        }

        private static NumericUpDown NewNumber(int min, int max, int increment)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Increment = increment, BorderStyle = BorderStyle.FixedSingle };
        }

        internal static Button FlatButton(string text)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.Text, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = Theme.Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 242, 245);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(228, 231, 236);
            return button;
        }

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("창 열기");
            show.Click += delegate { ShowWindow(); };
            _trayToggle.Click += delegate { ToggleRecording(); };
            ToolStripMenuItem folder = new ToolStripMenuItem("저장 폴더 열기");
            folder.Click += delegate { OpenFolder(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("종료");
            exit.Click += delegate { ExitApp(); };
            menu.Items.AddRange(new ToolStripItem[] { show, _trayToggle, folder, new ToolStripSeparator(), exit });

            _tray.ContextMenuStrip = menu;
            _tray.Icon = _idleIcon;
            _tray.Text = "화면 녹화";
            _tray.DoubleClick += delegate { ShowWindow(); };
            _tray.Visible = true;
        }

        private void LoadIntoUi()
        {
            _monitorBox.Items.Clear();
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                _monitorBox.Items.Add("모니터 " + (i + 1) + " (" + b.Width + "×" + b.Height + ")" + (screens[i].Primary ? " · 주" : ""));
            }

            _monitorBox.SelectedIndex = Math.Min(_s.Monitor, screens.Length - 1);
            _scaleSeg.SelectedIndex = Math.Max(0, Array.IndexOf(ScaleWidths, _s.ScaleWidth));
            _bitrateBox.Value = _s.BitrateKbps;
            SelectPreset();
            _fpsBox.Value = _s.Fps;
            _partBox.Value = _s.PartMegabytes;
            _folderLabel.Text = _s.OutputFolder;
            _captureSeg.SelectedIndex = _s.CaptureMode;
            _hardwareCheck.Checked = _s.HardwareEncoder;
            _hotKeyBox.SetValue(_s.HotKeyModifiers, _s.HotKeyCode);
            _apiCheck.Checked = _s.ApiEnabled;
            _portBox.Value = _s.ApiPort;
            _tokenBox.Text = _s.ApiToken;
        }

        // Highlights the preset matching the bitrate; none when it was typed in by hand.
        private void SelectPreset()
        {
            _presetSeg.SelectedIndex = Array.IndexOf(PresetKbps, (int)_bitrateBox.Value);
        }

        private void Save()
        {
            string error = _s.Save();
            if (error != null) Notify(error);
        }

        private void RefreshUi()
        {
            bool active = _recorder.IsActive;
            _scheduleButton.Text = _schedule != null ? "예약됨" : "예약 녹화";
            _scheduleButton.ForeColor = _schedule != null ? Theme.Accent : Theme.Text;
            _scheduleButton.Enabled = !_scheduleStarted;
            if (active != _lastActive)
            {
                _lastActive = active;
                _tray.Icon = active ? _recordingIcon : _idleIcon;
                Icon = active ? _recordingIcon : _idleIcon;
                _recordingCard.Enabled = !active;
                _optionsCard.Enabled = !active;
            }

            string hotKey = HotKey.Format(_s.HotKeyModifiers, _s.HotKeyCode);

            if (!active)
            {
                _recordButton.SetState(RecordButton.Mode.Idle, "녹화 시작", string.Empty);
                _trayToggle.Text = "녹화 시작";
                _trayToggle.Enabled = true;
                _statusLabel.ForeColor = Theme.Muted;
                _statusLabel.Text = hotKey.Length > 0 ? "대기 중 · " + hotKey + " 로 시작/중지" : "대기 중";
                if (_hotKeyError.Length > 0)
                {
                    _statusLabel.ForeColor = Theme.Record;
                    _statusLabel.Text += "\n" + _hotKeyError;
                }
                else if (_compressor.Current.Length > 0)
                {
                    int waiting = _compressor.Pending;
                    _statusLabel.Text += "\n압축 중 · " + _compressor.Current + (waiting > 0 ? " (+" + waiting + ")" : string.Empty);
                }
                else if (_schedule != null)
                {
                    _statusLabel.Text += "\n예약 " + _schedule.Describe();
                }

                _tray.Text = _schedule != null ? "화면 녹화 — 예약 " + _schedule.Describe() : "화면 녹화 — 대기 중";
                return;
            }

            string time = FormatTime(_recorder.Elapsed);
            bool stopping = _recorder.IsStopping;
            _recordButton.SetState(stopping ? RecordButton.Mode.Stopping : RecordButton.Mode.Recording,
                stopping ? "저장 중…" : "녹화 중지", stopping ? string.Empty : time);
            _trayToggle.Text = "녹화 중지";
            _trayToggle.Enabled = !stopping;
            _statusLabel.ForeColor = Theme.Record;
            // GDI is the fallback (잠금 화면, 원격 세션 등) or a chosen mode; show it, since it behaves differently from DXGI.
            string slow = _recorder.Backend == "GDI" ? " · GDI 캡처" : string.Empty;
            string until = _scheduleStarted ? " · " + _windowEnd.ToString(_windowEnd.Date == DateTime.Today ? "HH:mm" : "M월 d일 HH:mm") + " 종료 예정" : string.Empty;
            _statusLabel.Text = "● 녹화 중 · " + FormatSize(_recorder.Bytes) + until + slow + "\n" + Path.GetFileName(_recorder.CurrentFile);
            _tray.Text = "화면 녹화 — 녹화 중 " + time;
        }

        private static string FormatTime(TimeSpan t)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private static string FormatSize(long bytes)
        {
            return bytes >= 1024L * 1024 * 1024
                ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB"
                : (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
        }

        // ------------------------------------------------------------ actions

        private void EditSchedule()
        {
            using (ScheduleDialog dialog = new ScheduleDialog(_schedule))
            {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.OK)
                {
                    SetSchedule(dialog.Result);
                    Notify("예약했습니다: " + dialog.Result.Describe());
                }
                else if (result == DialogResult.Abort)
                {
                    SetSchedule(null);
                }
            }

            CheckSchedule();
            RefreshUi();
        }

        private void SetSchedule(Schedule schedule)
        {
            _schedule = schedule;
            _scheduleStarted = false;
            _doneThrough = DateTime.MinValue;
            _s.ScheduleText = schedule != null ? schedule.Serialize() : string.Empty;
            Save();
        }

        // This window is over (finished, stopped by hand, or skipped); a one-off is then used up.
        private void FinishWindow()
        {
            _scheduleStarted = false;
            _doneThrough = _windowEnd;
            if (!_schedule.Weekly) SetSchedule(null);
        }

        // Compares the Windows clock with the schedule on every timer tick.
        private void CheckSchedule()
        {
            if (_schedule == null) return;
            DateTime now = DateTime.Now;
            bool active = _recorder.IsActive;

            if (_scheduleStarted)
            {
                if (!active) { FinishWindow(); return; }   // stopped by hand, or by a failure
                if (now >= _windowEnd)
                {
                    _recorder.Stop();
                    FinishWindow();
                    Notify("예약한 종료 시각이 되어 녹화를 중지했습니다.");
                }

                return;
            }

            DateTime start, end;
            if (!_schedule.Next(now, _doneThrough, out start, out end))
            {
                SetSchedule(null);
                Notify("예약한 시간이 지나 녹화하지 못했습니다. (PC가 꺼져 있거나 절전 상태였을 수 있습니다)");
                return;
            }

            if (now < start) return;

            _windowEnd = end;
            if (active)
            {
                // Never stop a recording the user started by hand.
                FinishWindow();
                Notify("이미 녹화 중이라 이번 예약 녹화는 건너뜁니다.");
                return;
            }

            string error = _recorder.Start(_s);
            if (error != null)
            {
                FinishWindow();
                Notify("예약 녹화를 시작하지 못했습니다: " + error);
                return;
            }

            _scheduleStarted = true;
        }

        private void ToggleRecording()
        {
            string error = _recorder.IsActive ? _recorder.Stop() : _recorder.Start(_s);
            if (error != null) Notify(error);
            RefreshUi();
        }

        private void ChooseFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "녹화 파일을 저장할 폴더", SelectedPath = _s.OutputFolder })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _s.OutputFolder = dialog.SelectedPath;
                _folderLabel.Text = _s.OutputFolder;
                Save();
            }
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_s.OutputFolder);
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + _s.OutputFolder + "\"", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Notify("폴더를 열지 못했습니다: " + ex.Message);
            }
        }

        private void RegisterHotKey()
        {
            if (!IsHandleCreated) return;
            ScreenRecorder.HotKey.UnregisterHotKey(Handle, HotKeyId);
            _hotKeyError = string.Empty;
            if (!ScreenRecorder.HotKey.IsValid(_s.HotKeyModifiers, _s.HotKeyCode)) return;

            if (!ScreenRecorder.HotKey.RegisterHotKey(Handle, HotKeyId, _s.HotKeyModifiers | ScreenRecorder.HotKey.NoRepeat, (uint)_s.HotKeyCode))
            {
                _hotKeyError = ScreenRecorder.HotKey.Format(_s.HotKeyModifiers, _s.HotKeyCode)
                             + " 은(는) 다른 프로그램이 사용 중입니다. 다른 조합을 지정하세요.";
            }
        }

        private void ApplyApi()
        {
            _api.Stop();
            if (!_s.ApiEnabled)
            {
                _apiStatus.Text = "꺼짐";
                return;
            }

            string error = _api.Start(_s.ApiPort, _s.ApiToken);
            _apiStatus.Text = error ?? "대기 중 · 127.0.0.1:" + _s.ApiPort;
            if (error != null) Notify(error);
        }

        private void OnRecorderMessage(string message)
        {
            try { BeginInvoke(new Action(() => Notify(message))); }
            catch (InvalidOperationException) { }   // window already gone
        }

        private void Notify(string message)
        {
            _tray.ShowBalloonTip(3000, "화면 녹화", message, ToolTipIcon.Info);
        }

        private void ShowWindow()
        {
            _startHidden = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _exiting = true;
            Close();
        }

        // ---------------------------------------------------------- lifecycle

        protected override void SetVisibleCore(bool value)
        {
            // Started with --tray: create the window (the hotkey needs its handle) but keep it hidden.
            if (_startHidden)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }

            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey();
            ApplyApi();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ScreenRecorder.HotKey.UnregisterHotKey(Handle, HotKeyId);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyId)
            {
                ToggleRecording();
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The X button hides the window; the hotkey and API keep working from the tray.
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    Notify("창을 닫아도 트레이에서 계속 실행됩니다. 종료하려면 트레이 아이콘을 우클릭하세요.");
                }
                return;
            }

            // Finalize the file first: an MP4 that was never closed cannot be played.
            _recorder.Stop();
            _recorder.WaitForStop(10000);
            _compressor.Cancel();   // the originals stay; an unfinished compression is dropped
            if (_download != null) _download.Cancel();
            _api.Stop();
            _timer.Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _timer.Dispose();
                _tips.Dispose();
                _api.Dispose();
                _compressor.Dispose();
            }

            base.Dispose(disposing);
        }

        // Red dot while recording, grey with a red centre otherwise.
        private static Icon MakeIcon(bool recording)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (Brush outer = new SolidBrush(recording ? Color.FromArgb(220, 38, 38) : Color.FromArgb(75, 85, 99)))
                    {
                        g.FillEllipse(outer, 1, 1, 30, 30);
                    }

                    if (!recording)
                    {
                        using (Brush inner = new SolidBrush(Color.FromArgb(239, 68, 68)))
                        {
                            g.FillEllipse(inner, 9, 9, 14, 14);
                        }
                    }
                    else
                    {
                        using (Brush inner = new SolidBrush(Color.White))
                        {
                            g.FillRectangle(inner, 10, 10, 12, 12);
                        }
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}

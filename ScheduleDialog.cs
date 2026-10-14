using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenRecorder
{
    // Pick when recording should start and stop: once, or every week on chosen weekdays.
    // OK = use the schedule, Abort = remove the existing one, Cancel = leave things as they were.
    internal sealed class ScheduleDialog : Form
    {
        private const int FormWidth = 380;
        private const int Gutter = 20;
        private const int ButtonsTop = 210;

        // Monday first; the value is the DayOfWeek.
        private static readonly string[] DayNames = { "월", "화", "수", "목", "금", "토", "일" };
        private static readonly DayOfWeek[] DayValues =
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        };

        private readonly Segmented _mode;
        private readonly DateTimePicker _startDate, _startTime, _endDate, _endTime;   // one-off
        private readonly CheckBox[] _days = new CheckBox[7];                          // weekly
        private readonly DateTimePicker _from, _to;                                   // weekly
        private readonly Control[] _oncePart, _weeklyPart;
        private readonly Label _message;

        public Schedule Result { get; private set; }

        public ScheduleDialog(Schedule current)
        {
            Text = "예약 녹화";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Ui(9F);
            BackColor = Theme.Card;

            // Defaults: one-off from the next full hour for an hour; weekly on weekdays, 09:00-18:00.
            DateTime now = DateTime.Now;
            DateTime nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
            DateTime onceFrom = nextHour, onceTo = nextHour.AddHours(1);
            int days = 0x3E;   // Monday..Friday
            TimeSpan from = TimeSpan.FromHours(9), to = TimeSpan.FromHours(18);
            if (current != null)
            {
                if (current.Weekly) { days = current.Days; from = current.From; to = current.To; }
                else { onceFrom = current.OnceStart; onceTo = current.OnceEnd; }
            }

            _mode = new Segmented("한 번", "요일 반복");
            _mode.SetBounds(Gutter, 16, FormWidth - 2 * Gutter, 28);
            _mode.SelectedIndex = current != null && current.Weekly ? 1 : 0;
            _mode.UserSelected += delegate { ApplyMode(); };
            Controls.Add(_mode);

            // ---- one-off
            Label c1 = Caption("시작", 60), c2 = Caption("종료", 96);
            _startDate = AddDate(onceFrom, 60);
            _startTime = AddTime(onceFrom, 248, 60);
            _endDate = AddDate(onceTo, 96);
            _endTime = AddTime(onceTo, 248, 96);
            _oncePart = new Control[] { c1, c2, _startDate, _startTime, _endDate, _endTime };

            // ---- weekly
            Label c3 = Caption("요일", 60), c4 = Caption("시간", 108);
            int left = 70, gap = 4, width = (FormWidth - Gutter - left - 6 * gap) / 7;
            for (int i = 0; i < 7; i++)
            {
                CheckBox box = new CheckBox
                {
                    Text = DayNames[i], Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                    Left = left + i * (width + gap), Top = 60, Width = width, Height = 32,
                    Checked = Schedule.HasDay(days, DayValues[i])
                };
                box.FlatAppearance.BorderColor = Theme.Border;
                box.FlatAppearance.CheckedBackColor = Theme.Accent;
                box.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 235, 240);
                StyleDay(box);
                box.CheckedChanged += delegate { StyleDay(box); ShowCheck(); };
                Controls.Add(box);
                _days[i] = box;
            }

            _from = AddTime(DateTime.Today + from, 70, 106);
            Label tilde = new Label
            {
                Text = "~", Left = 156, Top = 108, Width = 24, Height = 28, AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Card
            };
            Controls.Add(tilde);
            _to = AddTime(DateTime.Today + to, 184, 106);
            List<Control> weeklyControls = new List<Control> { c3, c4, _from, _to, tilde };
            weeklyControls.AddRange(_days);
            _weeklyPart = weeklyControls.ToArray();

            _message = new Label
            {
                Left = Gutter, Top = 144, Width = FormWidth - 2 * Gutter, Height = 58, AutoSize = false,
                UseMnemonic = false, Font = Theme.Ui(8.5F), BackColor = Theme.Card
            };
            Controls.Add(_message);

            Button schedule = new Button
            {
                Text = "저장", Left = FormWidth - Gutter - 80, Top = ButtonsTop, Width = 80, Height = 32,
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White,
                Cursor = Cursors.Hand, UseVisualStyleBackColor = false
            };
            schedule.FlatAppearance.BorderColor = Theme.Accent;
            schedule.Click += delegate
            {
                if (ShowCheck()) { Result = Build(); DialogResult = DialogResult.OK; }
            };
            Controls.Add(schedule);

            Button close = MainForm.FlatButton("닫기");
            close.SetBounds(FormWidth - Gutter - 80 - 8 - 72, ButtonsTop, 72, 32);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);

            if (current != null)
            {
                Button clear = MainForm.FlatButton("예약 해제");
                clear.SetBounds(Gutter, ButtonsTop, 88, 32);
                clear.DialogResult = DialogResult.Abort;
                Controls.Add(clear);
            }

            AcceptButton = schedule;
            CancelButton = close;
            ClientSize = new Size(FormWidth, ButtonsTop + 32 + 16);

            EventHandler changed = delegate { ShowCheck(); };
            _startDate.ValueChanged += changed;
            _startTime.ValueChanged += changed;
            _endDate.ValueChanged += changed;
            _endTime.ValueChanged += changed;
            _from.ValueChanged += changed;
            _to.ValueChanged += changed;
            ApplyMode();
        }

        private bool IsWeekly { get { return _mode.SelectedIndex == 1; } }

        private Schedule Build()
        {
            if (!IsWeekly)
            {
                return Schedule.Once(_startDate.Value.Date + _startTime.Value.TimeOfDay,
                                     _endDate.Value.Date + _endTime.Value.TimeOfDay);
            }

            int days = 0;
            for (int i = 0; i < 7; i++)
            {
                if (_days[i].Checked) days |= 1 << (int)DayValues[i];
            }

            return Schedule.Every(days, new TimeSpan(_from.Value.Hour, _from.Value.Minute, 0),
                                        new TimeSpan(_to.Value.Hour, _to.Value.Minute, 0));
        }

        private void ApplyMode()
        {
            foreach (Control c in _oncePart) c.Visible = !IsWeekly;
            foreach (Control c in _weeklyPart) c.Visible = IsWeekly;
            ShowCheck();
        }

        // Shows the problem (red) or a hint (grey); returns whether the input can be saved.
        private bool ShowCheck()
        {
            if (_message == null) return true;   // still building the form
            string error = Build().Problem(DateTime.Now);
            _message.ForeColor = error != null ? Theme.Record : Theme.Muted;
            _message.Text = error ?? (IsWeekly
                ? "선택한 요일마다 이 시간에 녹화합니다. 종료가 시작보다 이르면 다음 날 종료합니다.\nPC가 잠겨 있으면 검은 화면이 녹화되고, 절전 중에는 녹화되지 않습니다."
                : "시작 시각이 이미 지났다면 바로 시작합니다.\nPC가 잠겨 있으면 검은 화면이 녹화되고, 절전 중에는 녹화되지 않습니다.");
            return error == null;
        }

        private static void StyleDay(CheckBox box)
        {
            box.BackColor = box.Checked ? Theme.Accent : Color.White;
            box.ForeColor = box.Checked ? Color.White : Theme.Text;
        }

        private Label Caption(string text, int y)
        {
            Label label = new Label
            {
                Text = text, Left = Gutter, Top = y, Width = 44, Height = 28, AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, ForeColor = Theme.Text, BackColor = Theme.Card
            };
            Controls.Add(label);
            return label;
        }

        private DateTimePicker AddDate(DateTime value, int y)
        {
            DateTimePicker picker = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd (ddd)",
                Left = 70, Width = 170, Value = value
            };
            picker.Top = y + (28 - picker.Height) / 2;
            Controls.Add(picker);
            return picker;
        }

        private DateTimePicker AddTime(DateTime value, int x, int y)
        {
            DateTimePicker picker = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true,
                Left = x, Width = 80, Value = value
            };
            picker.Top = y + (28 - picker.Height) / 2;
            Controls.Add(picker);
            return picker;
        }
    }
}

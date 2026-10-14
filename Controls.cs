using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenRecorder
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(244, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(224, 227, 232);
        public static readonly Color Text = Color.FromArgb(31, 35, 40);
        public static readonly Color Muted = Color.FromArgb(107, 114, 128);
        public static readonly Color Accent = Color.FromArgb(47, 111, 237);
        public static readonly Color Record = Color.FromArgb(229, 72, 77);
        public static readonly Color RecordHover = Color.FromArgb(211, 55, 60);
        public static readonly Color Ink = Color.FromArgb(31, 35, 40);
        public static readonly Color InkHover = Color.FromArgb(52, 58, 66);
        public static readonly Color Disabled = Color.FromArgb(176, 181, 189);

        public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", size, style);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // White rounded panel with a small heading.
    internal sealed class Card : Panel
    {
        private readonly string _title;
        private readonly Font _font = Theme.Ui(9.5F, FontStyle.Bold);

        public Card(string title)
        {
            _title = title;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96F;
            using (GraphicsPath path = Theme.Round(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 10 * s))
            using (Brush fill = new SolidBrush(Theme.Card))
            using (Pen border = new Pen(Theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            TextRenderer.DrawText(g, _title, _font, new Point((int)(16 * s), (int)(11 * s)), Theme.Text,
                TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }

    // The big start/stop button.
    internal sealed class RecordButton : Control
    {
        public enum Mode { Idle, Recording, Stopping }

        private readonly Font _font = Theme.Ui(12F, FontStyle.Bold);
        private Mode _mode = Mode.Idle;
        private string _detail = string.Empty;
        private bool _hover;

        public RecordButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public void SetState(Mode mode, string text, string detail)
        {
            if (_mode == mode && Text == text && _detail == detail) return;
            _mode = mode;
            Text = text;
            AccessibleName = text;
            _detail = detail;
            Cursor = mode == Mode.Stopping ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnClick(EventArgs e)
        {
            if (_mode != Mode.Stopping) base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96F;

            Color fill = _mode == Mode.Recording ? (_hover ? Theme.InkHover : Theme.Ink)
                       : _mode == Mode.Stopping ? Theme.Disabled
                       : (_hover ? Theme.RecordHover : Theme.Record);

            RectangleF rect = new RectangleF(0.5F, 0.5F, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(rect, 12 * s))
            using (Brush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            if (Focused && ShowFocusCues)
            {
                using (GraphicsPath ring = Theme.Round(new RectangleF(3 * s, 3 * s, Width - 6 * s, Height - 6 * s), 10 * s))
                using (Pen pen = new Pen(Color.FromArgb(150, Color.White)))
                {
                    g.DrawPath(pen, ring);
                }
            }

            Rectangle bounds = ClientRectangle;
            if (_mode == Mode.Recording)
            {
                // stop square, caption, elapsed time on the right
                float box = 14 * s;
                using (GraphicsPath square = Theme.Round(new RectangleF(22 * s, (Height - box) / 2, box, box), 3 * s))
                using (Brush red = new SolidBrush(Theme.Record))
                {
                    g.FillPath(red, square);
                }

                TextRenderer.DrawText(g, Text, _font, new Rectangle((int)(46 * s), 0, bounds.Width, bounds.Height),
                    Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, _detail, _font, new Rectangle(0, 0, bounds.Width - (int)(22 * s), bounds.Height),
                    Color.FromArgb(190, 196, 205), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                string caption = _mode == Mode.Idle ? "●   " + Text : Text;
                TextRenderer.DrawText(g, caption, _font, bounds, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }

    // Segmented choice, e.g. 저용량 | 균형 | 고화질 | 최고.
    internal sealed class Segmented : Control
    {
        private readonly string[] _items;
        private readonly Font _font = Theme.Ui(9F);
        private int _selected = -1;
        private int _hover = -1;

        // Raised only when the user picks a segment, not when the code sets SelectedIndex.
        public event EventHandler UserSelected;

        public Segmented(params string[] items)
        {
            _items = items;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = IndexAt(e.X);
            if (index != _hover) { _hover = index; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;

            int index = IndexAt(e.X);
            if (index < 0 || index == _selected) return;
            _selected = index;
            Invalidate();
            EventHandler handler = UserSelected;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private int IndexAt(int x)
        {
            if (x < 0 || x >= Width) return -1;
            return Math.Min(_items.Length - 1, x * _items.Length / Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96F;
            float w = Width / (float)_items.Length;

            using (GraphicsPath outer = Theme.Round(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 7 * s))
            using (Brush back = new SolidBrush(Color.FromArgb(247, 248, 250)))
            using (Pen border = new Pen(Theme.Border))
            {
                g.FillPath(back, outer);
                g.DrawPath(border, outer);
            }

            for (int i = 0; i < _items.Length; i++)
            {
                RectangleF cell = new RectangleF(i * w + 2 * s, 2 * s, w - 4 * s, Height - 4 * s);
                bool selected = i == _selected;
                if (selected || (i == _hover && Enabled))
                {
                    Color fill = selected ? (Enabled ? Theme.Accent : Theme.Disabled) : Color.FromArgb(232, 235, 240);
                    using (GraphicsPath path = Theme.Round(cell, 5 * s))
                    using (Brush brush = new SolidBrush(fill))
                    {
                        g.FillPath(brush, path);
                    }
                }

                Color text = selected ? Color.White : (Enabled ? Theme.Text : Theme.Disabled);
                TextRenderer.DrawText(g, _items[i], _font, Rectangle.Round(cell), text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}

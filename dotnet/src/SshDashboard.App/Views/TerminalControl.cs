using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace SshDashboard.App.Views;

/// <summary>
/// A compact VT100/xterm-style terminal surface: fixed colour palette, cursor
/// movement, erase and SGR support, scrollback and keyboard translation.
/// </summary>
public sealed class TerminalControl : Control
{
    private struct Cell
    {
        public char Ch;
        public byte Fg;
        public byte Bg;
        public bool Bold;
    }

    private const byte DefaultFg = 7;
    private const byte DefaultBg = 0;
    private static readonly Color[] Palette = BuildPalette();
    private static readonly Dictionary<int, IBrush> BrushCache = new();

    private readonly List<Cell[]> _history = new();
    private List<Cell[]> _screen = new();
    private int _cols = 100;
    private int _rows = 30;
    private int _cx;
    private int _cy;
    private int _scrollOffset;
    private int _savedX;
    private int _savedY;
    private byte _fg = DefaultFg;
    private byte _bg = DefaultBg;
    private bool _bold;

    private int _state; // 0 text, 1 escape, 2 csi, 3 osc
    private readonly StringBuilder _csi = new();
    private readonly Typeface _typeface = new("Menlo, Monaco, Consolas, monospace");
    private readonly DispatcherTimer _blink;
    private bool _cursorOn = true;
    private double _charWidth = 8;
    private double _lineHeight = 17;
    private bool _sizeReady;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) => { _cursorOn = !_cursorOn; InvalidateVisual(); };
        _blink.Start();
        Reset();
    }

    public event Action<byte[]>? Input;

    public string PlainText =>
        string.Join("\n", _screen.Select(row => new string(row.Select(c => c.Ch).ToArray()).TrimEnd()));

    public void Reset()
    {
        _screen = Enumerable.Range(0, _rows).Select(_ => BlankRow()).ToList();
        _history.Clear();
        _cx = _cy = 0;
        _fg = DefaultFg;
        _bg = DefaultBg;
        _bold = false;
        _scrollOffset = 0;
        InvalidateVisual();
    }

    private Cell[] BlankRow() => Enumerable.Range(0, _cols)
        .Select(_ => new Cell { Ch = ' ', Fg = DefaultFg, Bg = DefaultBg }).ToArray();

    public void ResizeTo(double width, double height)
    {
        MeasureFont();
        var cols = Math.Max(20, (int)(width / _charWidth));
        var rows = Math.Max(5, (int)(height / _lineHeight));
        if (cols == _cols && rows == _rows) return;
        var old = _screen;
        _cols = cols;
        _rows = rows;
        _screen = Enumerable.Range(0, rows).Select(_ => BlankRow()).ToList();
        for (var r = 0; r < Math.Min(old.Count, rows); r++)
            for (var c = 0; c < Math.Min(old[r].Length, cols); c++)
                _screen[r][c] = old[r][c];
        _cx = Math.Min(_cx, cols - 1);
        _cy = Math.Min(_cy, rows - 1);
        _sizeReady = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeTo(e.NewSize.Width, e.NewSize.Height);
        InvalidateVisual();
    }

    private void MeasureFont()
    {
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, 13, Brushes.White);
        _charWidth = Math.Max(6, probe.Width);
        _lineHeight = Math.Max(12, probe.Height * 1.08);
    }

    // ---------------------------------------------------------------- parser

    public void Feed(string text)
    {
        foreach (var ch in text) Consume(ch);
        InvalidateVisual();
    }

    public void WriteSystem(string message)
    {
        foreach (var ch in "\r\n" + message + "\r\n") Consume(ch);
        InvalidateVisual();
    }

    private void Consume(char ch)
    {
        switch (_state)
        {
            case 0:
                if (ch == '\u001b') { _state = 1; return; }
                HandleText(ch);
                return;
            case 1:
                if (ch == '[') { _state = 2; _csi.Clear(); return; }
                if (ch == ']') { _state = 3; return; }
                if (ch == '(' || ch == ')' || ch == '#' || ch == '=' || ch == '>') { _state = 0; return; }
                if (ch == '7') { _savedX = _cx; _savedY = _cy; _state = 0; return; }
                if (ch == '8') { _cx = _savedX; _cy = _savedY; _state = 0; return; }
                if (ch == 'M') { ReverseIndex(); _state = 0; return; }
                _state = 0;
                return;
            case 2:
                if (ch >= '@' && ch <= '~') { ApplyCsi(ch, _csi.ToString()); _state = 0; }
                else if (_csi.Length < 64) _csi.Append(ch);
                return;
            case 3:
                if (ch == '\a') { _state = 0; }
                else if (ch == '\u001b') { _state = 1; }
                return;
        }
    }

    private void HandleText(char ch)
    {
        switch (ch)
        {
            case '\r': _cx = 0; return;
            case '\n': NewLine(); return;
            case '\b': _cx = Math.Max(0, _cx - 1); return;
            case '\t': _cx = Math.Min(_cols - 1, (_cx / 8 + 1) * 8); return;
            case '\a': return;
            case '\u007f': return;
        }
        if (ch < ' ') return;
        if (_cx >= _cols) { _cx = 0; NewLine(); }
        _screen[_cy][_cx] = new Cell { Ch = ch, Fg = _fg, Bg = _bg, Bold = _bold };
        _cx++;
    }

    private void NewLine()
    {
        _cy++;
        if (_cy < _rows) return;
        _history.Add(_screen[0]);
        if (_history.Count > 4000) _history.RemoveAt(0);
        _screen.RemoveAt(0);
        _screen.Add(BlankRow());
        _cy = _rows - 1;
        _scrollOffset = 0;
    }

    private void ReverseIndex()
    {
        if (_cy > 0) { _cy--; return; }
        _screen.Insert(0, BlankRow());
        _screen.RemoveAt(_screen.Count - 1);
    }

    private static int Param(string body, int index, int fallback)
    {
        var parts = body.Split(';');
        if (index >= parts.Length) return fallback;
        return int.TryParse(parts[index].TrimStart('?', '>'), out var value) ? value : fallback;
    }

    private void ApplyCsi(char final, string body)
    {
        var n = Math.Max(1, Param(body, 0, 1));
        switch (final)
        {
            case 'A': _cy = Math.Max(0, _cy - n); break;
            case 'B': _cy = Math.Min(_rows - 1, _cy + n); break;
            case 'C': _cx = Math.Min(_cols - 1, _cx + n); break;
            case 'D': _cx = Math.Max(0, _cx - n); break;
            case 'G': _cx = Math.Clamp(n - 1, 0, _cols - 1); break;
            case 'd': _cy = Math.Clamp(n - 1, 0, _rows - 1); break;
            case 'H':
            case 'f':
                _cy = Math.Clamp(Param(body, 0, 1) - 1, 0, _rows - 1);
                _cx = Math.Clamp(Param(body, 1, 1) - 1, 0, _cols - 1);
                break;
            case 'J':
                ClearScreen(Param(body, 0, 0));
                break;
            case 'K':
                ClearLine(Param(body, 0, 0));
                break;
            case 'm':
                ApplySgr(body);
                break;
            case 's': _savedX = _cx; _savedY = _cy; break;
            case 'u': _cx = _savedX; _cy = _savedY; break;
        }
    }

    private void ClearScreen(int mode)
    {
        if (mode == 2 || mode == 3)
        {
            for (var r = 0; r < _rows; r++) _screen[r] = BlankRow();
            if (mode == 3) _history.Clear();
            _cx = _cy = 0;
            return;
        }
        if (mode == 1)
        {
            for (var r = 0; r <= _cy; r++)
                for (var c = 0; c < (r == _cy ? _cx : _cols); c++) _screen[r][c] = Blank();
            return;
        }
        for (var c = _cx; c < _cols; c++) _screen[_cy][c] = Blank();
        for (var r = _cy + 1; r < _rows; r++) _screen[r] = BlankRow();
    }

    private void ClearLine(int mode)
    {
        var from = mode == 1 ? 0 : mode == 2 ? 0 : _cx;
        var to = mode == 0 ? _cols : mode == 2 ? _cols : _cx + 1;
        for (var c = from; c < to && c < _cols; c++) _screen[_cy][c] = Blank();
    }

    private Cell Blank() => new() { Ch = ' ', Fg = _fg, Bg = _bg };

    private void ApplySgr(string body)
    {
        if (body.Length == 0) { _fg = DefaultFg; _bg = DefaultBg; _bold = false; return; }
        var parts = body.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].TrimStart('?'), out var code)) continue;
            switch (code)
            {
                case 0: _fg = DefaultFg; _bg = DefaultBg; _bold = false; break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case 39: _fg = DefaultFg; break;
                case 49: _bg = DefaultBg; break;
                case >= 30 and <= 37: _fg = (byte)(code - 30); break;
                case >= 40 and <= 47: _bg = (byte)(code - 40); break;
                case >= 90 and <= 97: _fg = (byte)(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = (byte)(code - 100 + 8); break;
                case 38 or 48 when i + 2 < parts.Length && parts[i + 1] == "5":
                    if (int.TryParse(parts[i + 2], out var idx))
                    {
                        if (code == 38) _fg = (byte)Math.Clamp(idx, 0, 255);
                        else _bg = (byte)Math.Clamp(idx, 0, 255);
                        i += 2;
                    }
                    break;
                case 38 or 48 when i + 4 < parts.Length && parts[i + 1] == "2":
                    if (int.TryParse(parts[i + 2], out var rr) && int.TryParse(parts[i + 3], out var gg) &&
                        int.TryParse(parts[i + 4], out var bb))
                    {
                        var color = new Color(255, (byte)Math.Clamp(rr, 0, 255), (byte)Math.Clamp(gg, 0, 255),
                            (byte)Math.Clamp(bb, 0, 255));
                        var brush = new SolidColorBrush(color);
                        BrushCache[1000 + (code == 38 ? 0 : 1)] = brush;
                        if (code == 38) _fg = 16; else _bg = 17;
                        i += 4;
                    }
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush(0), new Rect(Bounds.Size));
        if (!_sizeReady)
        {
            MeasureFont();
            _cols = Math.Max(20, (int)(Bounds.Width / _charWidth));
            _rows = Math.Max(5, (int)(Bounds.Height / _lineHeight));
            _screen = Enumerable.Range(0, _rows).Select(_ => BlankRow()).ToList();
            _sizeReady = true;
        }

        var y = 0.0;
        for (var r = 0; r < _screen.Count; r++)
        {
            var row = _screen[r];
            var c = 0;
            while (c < row.Length)
            {
                var bg = row[c].Bg;
                var start = c;
                if (bg != DefaultBg)
                {
                    while (c < row.Length && row[c].Bg == bg) c++;
                    context.FillRectangle(Brush(bg, true),
                        new Rect(start * _charWidth, y, (c - start) * _charWidth, _lineHeight));
                }
                else c++;
            }

            c = 0;
            while (c < row.Length)
            {
                if (row[c].Ch == ' ' && row[c].Fg == DefaultFg) { c++; continue; }
                var fg = row[c].Fg;
                var bold = row[c].Bold;
                var start = c;
                var text = new StringBuilder();
                while (c < row.Length && row[c].Fg == fg && row[c].Bold == bold)
                {
                    text.Append(row[c].Ch);
                    c++;
                }
                var formatted = new FormattedText(text.ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _typeface, 13,
                    Brush(bold && fg < 8 ? (byte)(fg + 8) : fg));
                context.DrawText(formatted, new Point(start * _charWidth, y));
            }
            y += _lineHeight;
        }

        if (_cursorOn && IsFocused && _cy < _screen.Count)
        {
            var cursor = new Rect(_cx * _charWidth, _cy * _lineHeight, _charWidth, _lineHeight);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 122, 162, 247)), cursor);
        }
    }

    private static IBrush Brush(int index, bool background = false)
    {
        var key = background ? 500 + index : index;
        if (BrushCache.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(Palette[Math.Clamp(index, 0, Palette.Length - 1)]);
        BrushCache[key] = brush;
        return brush;
    }

    private static Color[] BuildPalette()
    {
        var list = new List<Color>
        {
            Color.Parse("#0B0F14"), Color.Parse("#F7768E"), Color.Parse("#9ECE6A"), Color.Parse("#E0AF68"),
            Color.Parse("#7AA2F7"), Color.Parse("#BB9AF7"), Color.Parse("#7DCFFF"), Color.Parse("#C0CAF5"),
            Color.Parse("#414868"), Color.Parse("#FF9E9E"), Color.Parse("#B9F27C"), Color.Parse("#FFD08A"),
            Color.Parse("#9AB8FF"), Color.Parse("#D0B9FF"), Color.Parse("#A4DDFF"), Color.Parse("#FFFFFF"),
        };
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (var r = 0; r < 6; r++)
            for (var g = 0; g < 6; g++)
                for (var b = 0; b < 6; b++)
                    list.Add(Color.FromRgb((byte)steps[r], (byte)steps[g], (byte)steps[b]));
        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            list.Add(Color.FromRgb(v, v, v));
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        Send(e.Text);
        e.Handled = true;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (meta && e.Key == Key.V)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                var text = await clipboard.GetTextAsync();
                if (!string.IsNullOrEmpty(text)) Send(text);
            }
            e.Handled = true;
            return;
        }

        string? sequence = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\u007f",
            Key.Tab => "\t",
            Key.Escape => "\u001b",
            Key.Up => "\u001b[A",
            Key.Down => "\u001b[B",
            Key.Right => "\u001b[C",
            Key.Left => "\u001b[D",
            Key.Home => "\u001b[H",
            Key.End => "\u001b[F",
            Key.Delete => "\u001b[3~",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            _ => null,
        };

        if (sequence is not null)
        {
            Send(sequence);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key is >= Key.A and <= Key.Z)
        {
            Send(((char)(e.Key - Key.A + 1)).ToString());
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Space)
        {
            Send("\0");
            e.Handled = true;
        }
    }

    private void Send(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Input?.Invoke(Encoding.UTF8.GetBytes(text));
        _scrollOffset = 0;
        InvalidateVisual();
    }
}

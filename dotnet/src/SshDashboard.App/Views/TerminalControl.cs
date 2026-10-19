using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VtNetCore.VirtualTerminal;
using VtNetCore.XTermParser;

namespace SshDashboard.App.Views;

/// <summary>
/// Terminal surface backed by VtNetCore: alternate screen, scroll regions,
/// truecolor, wide characters and cursor addressing are handled by the engine.
/// </summary>
public sealed class TerminalControl : Control
{
    private VirtualTerminalController _terminal = new()
    {
        VisibleColumns = 100,
        VisibleRows = 30,
        MaximumHistoryLines = 4000,
    };
    private DataConsumer _consumer;
    private readonly DispatcherTimer _blink;
    private readonly Typeface _typeface = new("Menlo, Monaco, Consolas, monospace");
    private readonly Dictionary<string, IBrush> _brushes = new();
    private bool _cursorOn = true;
    private double _charWidth = 8;
    private double _lineHeight = 17;
    private (int Row, int Col)? _selStart;
    private (int Row, int Col)? _selEnd;
    private bool _selecting;
    private int _mouseMode;
    private int _mouseButton = -1;
    private bool _bracketedPaste;

    public TerminalControl()
    {
        _consumer = new DataConsumer(_terminal);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) => { _cursorOn = !_cursorOn; InvalidateVisual(); };
        _blink.Start();
    }

    public event Action<byte[]>? Input;
    public event Action<int, int>? Resized;

    public int Columns => _terminal.VisibleColumns;
    public int Rows => _terminal.VisibleRows;
    public string PlainText => _terminal.GetScreenText();

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Contains("\u001b[?1000h") || text.Contains("\u001b[?1002h") || text.Contains("\u001b[?1003h"))
            _mouseMode = text.Contains("1003h") ? 1003 : text.Contains("1002h") ? 1002 : 1000;
        if (text.Contains("\u001b[?1000l") || text.Contains("\u001b[?1002l") || text.Contains("\u001b[?1003l"))
            _mouseMode = 0;
        if (text.Contains("\u001b[?2004h")) _bracketedPaste = true;
        if (text.Contains("\u001b[?2004l")) _bracketedPaste = false;
        try
        {
            _consumer.Push(Encoding.UTF8.GetBytes(text));
        }
        catch
        {
            // never let a malformed sequence take the window down
        }
        InvalidateVisual();
    }

    public void WriteSystem(string message) => Feed("\r\n" + message + "\r\n");

    public void Reset()
    {
        _terminal = new VirtualTerminalController
        {
            VisibleColumns = 100,
            VisibleRows = 30,
            MaximumHistoryLines = 4000,
        };
        _consumer = new DataConsumer(_terminal);
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- layout

    private void MeasureFont()
    {
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, 13, Brushes.White);
        _charWidth = Math.Max(6, probe.Width);
        _lineHeight = Math.Max(12, probe.Height * 1.08);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        MeasureFont();
        var cols = Math.Max(20, (int)(e.NewSize.Width / _charWidth));
        var rows = Math.Max(5, (int)(e.NewSize.Height / _lineHeight));
        if (cols != _terminal.VisibleColumns || rows != _terminal.VisibleRows)
        {
            _terminal.VisibleColumns = cols;
            _terminal.VisibleRows = rows;
            Resized?.Invoke(cols, rows);
        }
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        if (_charWidth <= 0) MeasureFont();
        context.FillRectangle(Brush("#0B0F14"), new Rect(Bounds.Size));

        var columns = _terminal.VisibleColumns;
        for (var row = 0; row < _terminal.VisibleRows; row++)
        {
            var line = _terminal.ViewPort.GetVisibleLine(row);
            if (line is null) continue;
            var y = row * _lineHeight;

            var col = 0;
            while (col < columns)
            {
                var cell = col < line.Count ? line[col] : null;
                var bgHex = cell?.Attributes.BackgroundWebColor ?? "#000000";
                var start = col;
                while (col < columns)
                {
                    var current = col < line.Count ? line[col] : null;
                    if ((current?.Attributes.BackgroundWebColor ?? "#000000") != bgHex) break;
                    col++;
                }
                if (bgHex is not ("#000000" or "#0B0F14"))
                    context.FillRectangle(Brush(bgHex),
                        new Rect(start * _charWidth, y, (col - start) * _charWidth, _lineHeight));
            }

            col = 0;
            while (col < columns)
            {
                var cell = col < line.Count ? line[col] : null;
                var fgHex = cell?.Attributes.WebColor ?? "#CDCDCD";
                if (cell?.Attributes.Reverse == true) fgHex = cell.Attributes.BackgroundWebColor;
                var start = col;
                var text = new StringBuilder();
                while (col < columns)
                {
                    var current = col < line.Count ? line[col] : null;
                    var currentFg = current?.Attributes.WebColor ?? "#CDCDCD";
                    if (current?.Attributes.Reverse == true) currentFg = current.Attributes.BackgroundWebColor;
                    if (currentFg != fgHex) break;
                    text.Append(current?.Char ?? ' ');
                    col++;
                }
                if (text.Length == 0) { col = start + 1; continue; }
                var formatted = new FormattedText(text.ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _typeface, 13, Brush(fgHex));
                context.DrawText(formatted, new Point(start * _charWidth, y));
            }
        }

        if (_selStart is { } anchor && _selEnd is { } cursorEnd)
        {
            var (r1, c1) = anchor;
            var (r2, c2) = cursorEnd;
            if (r1 > r2 || (r1 == r2 && c1 > c2)) { (r1, r2) = (r2, r1); (c1, c2) = (c2, c1); }
            r1 = Math.Clamp(r1, 0, _terminal.VisibleRows - 1);
            r2 = Math.Clamp(r2, 0, _terminal.VisibleRows - 1);
            for (var row = r1; row <= r2; row++)
            {
                var from = row == r1 ? Math.Clamp(c1, 0, columns - 1) : 0;
                var to = row == r2 ? Math.Clamp(c2, 0, columns - 1) : columns - 1;
                if (to < from) continue;
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(80, 76, 141, 255)),
                    new Rect(from * _charWidth, row * _lineHeight, (to - from + 1) * _charWidth, _lineHeight));
            }
        }

        if (_cursorOn && IsFocused && _terminal.CursorState.ShowCursor)
        {
            var position = _terminal.ViewPort.ScreenCursorPosition;
            var row = Math.Clamp(position.Row - 1, 0, Math.Max(0, _terminal.VisibleRows - 1));
            var col = Math.Clamp(position.Column, 0, Math.Max(0, columns - 1));
            var cursor = new Rect(col * _charWidth, row * _lineHeight, _charWidth, _lineHeight);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(150, 122, 162, 247)), cursor);
        }
    }

    private IBrush Brush(string webColor)
    {
        if (_brushes.TryGetValue(webColor, out var brush)) return brush;
        Color color;
        try { color = Color.Parse(webColor); }
        catch { color = Color.Parse("#CDCDCD"); }
        brush = new SolidColorBrush(color);
        _brushes[webColor] = brush;
        return brush;
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_mouseMode > 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _mouseButton = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.RightButtonPressed => 2,
                PointerUpdateKind.MiddleButtonPressed => 1,
                _ => 0,
            };
            SendMouse(_mouseButton, false, e.GetPosition(this), e.KeyModifiers);
            e.Handled = true;
            return;
        }
        var cell = CellAt(e.GetPosition(this));
        _selStart = cell;
        _selEnd = cell;
        _selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mouseMode is 1002 or 1003)
        {
            var button = _mouseMode == 1003 ? 35 : Math.Max(0, _mouseButton) + 32;
            SendMouse(button, false, e.GetPosition(this), e.KeyModifiers);
            return;
        }
        if (!_selecting) return;
        _selEnd = CellAt(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mouseMode > 0 && _mouseButton >= 0)
        {
            SendMouse(_mouseButton, true, e.GetPosition(this), e.KeyModifiers);
            _mouseButton = -1;
            e.Handled = true;
            return;
        }
        if (_selecting)
        {
            _selEnd = CellAt(e.GetPosition(this));
            _selecting = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_mouseMode <= 0) return;
        var button = e.Delta.Y >= 0 ? 64 : 65;
        SendMouse(button, false, e.GetPosition(this), e.KeyModifiers);
        e.Handled = true;
    }

    private void SendMouse(int button, bool release, Point point, KeyModifiers modifiers)
    {
        var (row, col) = CellAt(point);
        var code = button;
        if (modifiers.HasFlag(KeyModifiers.Shift)) code += 4;
        if (modifiers.HasFlag(KeyModifiers.Alt)) code += 8;
        if (modifiers.HasFlag(KeyModifiers.Control)) code += 16;
        Send($"\u001b[<{code};{col + 1};{row + 1}{(release ? 'm' : 'M')}");
    }

    private (int Row, int Col) CellAt(Point point)
    {
        var row = Math.Clamp((int)(point.Y / Math.Max(1, _lineHeight)), 0, Math.Max(0, _terminal.VisibleRows - 1));
        var col = Math.Clamp((int)(point.X / Math.Max(1, _charWidth)), 0, Math.Max(0, _terminal.VisibleColumns - 1));
        return (row, col);
    }

    private string SelectedText()
    {
        if (_selStart is not { } anchor || _selEnd is not { } end) return "";
        var (r1, c1) = anchor;
        var (r2, c2) = end;
        if (r1 > r2 || (r1 == r2 && c1 > c2)) { (r1, r2) = (r2, r1); (c1, c2) = (c2, c1); }
        if (r1 == r2 && c1 == c2) return "";
        var lines = new List<string>();
        for (var row = r1; row <= r2; row++)
        {
            var line = _terminal.ViewPort.GetVisibleLine(row);
            if (line is null) { lines.Add(""); continue; }
            var from = row == r1 ? c1 : 0;
            var to = row == r2 ? c2 : Math.Min(line.Count - 1, _terminal.VisibleColumns - 1);
            var builder = new StringBuilder();
            for (var col = from; col <= to && col < line.Count; col++) builder.Append(line[col]?.Char ?? ' ');
            lines.Add(builder.ToString().TrimEnd());
        }
        return string.Join("\n", lines).TrimEnd();
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

        if ((meta || (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift))) && e.Key == Key.C)
        {
            var selected = SelectedText();
            if (selected.Length > 0)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(selected);
                e.Handled = true;
                return;
            }
        }

        if (meta && e.Key == Key.V)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                var text = await clipboard.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                    Send(_bracketedPaste ? "\u001b[200~" + text + "\u001b[201~" : text);
            }
            e.Handled = true;
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (shift && e.Key == Key.Tab)
        {
            Send("\u001b[Z");
            e.Handled = true;
            return;
        }

        string? sequence = e.Key switch
        {
            Key.Enter => alt ? "\u001b\r" : "\r",
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
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
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
            return;
        }
        if (alt && e.Key is >= Key.A and <= Key.Z)
        {
            Send("\u001b" + (char)('a' + (e.Key - Key.A)));
            e.Handled = true;
        }
    }

    private void Send(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Input?.Invoke(Encoding.UTF8.GetBytes(text));
        InvalidateVisual();
    }
}

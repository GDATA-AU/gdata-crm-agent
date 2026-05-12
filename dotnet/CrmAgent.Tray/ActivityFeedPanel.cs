namespace CrmAgent.Tray;

/// <summary>
/// Double-buffered panel that renders a scrollable log-entry feed with a
/// custom dark scrollbar and hover tooltips. Replaces the ListBox-based
/// activity list to eliminate owner-draw rendering artifacts.
/// </summary>
internal sealed class ActivityFeedPanel : Panel
{
    private const int ItemHeight = 28;
    private const int MaxItems = 200;
    private const int ScrollBarWidth = 8;
    private const int ScrollBarPadding = 2;
    private const int ScrollWheelDelta = 3; // items per wheel notch

    private readonly List<LogTailer.LogEntry> _items = new();
    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ReshowDelay = 200 };
    private int _scrollOffset;  // first visible item index
    private int _hoveredIndex = -1;

    // Scrollbar drag state
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartOffset;

    public ActivityFeedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable, true);

        TabStop = true;

        BackColor = Theme.LogBackground;
        Font = Theme.Mono;
    }

    /// <summary>Number of items currently in the feed.</summary>
    public int ItemCount => _items.Count;

    /// <summary>Append entries and auto-scroll to the bottom.</summary>
    public void AddEntries(IReadOnlyList<LogTailer.LogEntry> entries)
    {
        if (entries.Count == 0) return;

        var wasAtBottom = IsScrolledToBottom();

        foreach (var entry in entries)
        {
            _items.Add(entry);
            while (_items.Count > MaxItems)
            {
                _items.RemoveAt(0);
                _scrollOffset = Math.Max(0, _scrollOffset - 1);

                // Keep hover state aligned when trimming oldest items.
                if (_hoveredIndex == 0)
                {
                    _hoveredIndex = -1;
                    _toolTip.Hide(this);
                    _toolTip.SetToolTip(this, null);
                }
                else if (_hoveredIndex > 0)
                {
                    _hoveredIndex--;
                }
            }
        }

        if (wasAtBottom)
            ScrollToBottom();

        ClampScroll();
        Invalidate();
    }

    // ── Painting ─────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.LogBackground);

        var visibleCount = VisibleItemCount();
        var contentWidth = Math.Max(0, ClientSize.Width - ScrollBarWidth - ScrollBarPadding * 2);

        if (contentWidth == 0)
        {
            PaintScrollBar(g);
            return;
        }

        for (var i = 0; i < visibleCount; i++)
        {
            var itemIndex = _scrollOffset + i;
            if (itemIndex >= _items.Count) break;

            var entry = _items[itemIndex];
            var bounds = new Rectangle(0, i * ItemHeight, contentWidth, ItemHeight);

            // Subtle hover highlight
            if (itemIndex == _hoveredIndex)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
                g.FillRectangle(hoverBrush, bounds);
            }

            PaintEntry(g, entry, bounds);
        }

        PaintScrollBar(g);
    }

    private void PaintEntry(Graphics g, LogTailer.LogEntry entry, Rectangle bounds)
    {
        var time = entry.Timestamp.ToString("HH:mm:ss");
        var (levelTag, levelColor) = entry.Level switch
        {
            "Error" or "Fatal" => ("ERR", Theme.Error),
            "Warning" => ("WRN", Theme.Warning),
            "Debug" or "Verbose" => ("DBG", Theme.TextDim),
            _ => ("INF", Theme.Info),
        };

        var x = bounds.X + 10;
        var y = bounds.Y;
        var h = bounds.Height;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        // Timestamp
        TextRenderer.DrawText(g, time, Font, new Rectangle(x, y, 80, h), Theme.TextDim, flags);
        x += 88;

        // Level badge
        TextRenderer.DrawText(g, levelTag, Font, new Rectangle(x, y, 38, h), levelColor, flags);
        x += 48;

        // Separator
        TextRenderer.DrawText(g, "│", Font, new Rectangle(x, y, 14, h), Theme.Border, flags);
        x += 24;

        // Message (with ellipsis for overflow)
        var msgWidth = bounds.Right - x - 6;
        if (msgWidth > 0)
        {
            TextRenderer.DrawText(g, entry.Message, Font,
                new Rectangle(x, y, msgWidth, h), Theme.LogText,
                flags | TextFormatFlags.EndEllipsis);
        }
    }

    // ── Custom scrollbar ─────────────────────────────────────────

    private void PaintScrollBar(Graphics g)
    {
        if (_items.Count <= VisibleItemCount()) return; // no scrollbar needed

        var (trackRect, thumbRect) = GetScrollBarGeometry();

        // Track
        using var trackBrush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
        g.FillRectangle(trackBrush, trackRect);

        // Thumb
        var thumbColor = _dragging
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(80, 255, 255, 255);
        using var thumbBrush = new SolidBrush(thumbColor);
        var radius = ScrollBarWidth / 2;
        FillRoundedRect(g, thumbBrush, thumbRect, radius);
    }

    private (Rectangle track, Rectangle thumb) GetScrollBarGeometry()
    {
        var trackX = ClientSize.Width - ScrollBarWidth - ScrollBarPadding;
        var trackHeight = ClientSize.Height;
        var trackRect = new Rectangle(trackX, 0, ScrollBarWidth, trackHeight);

        var totalItems = _items.Count;
        var visible = VisibleItemCount();
        var thumbHeight = Math.Max(20, (int)((double)visible / totalItems * trackHeight));
        var maxScroll = Math.Max(1, totalItems - visible);
        var thumbY = (int)((double)_scrollOffset / maxScroll * (trackHeight - thumbHeight));

        var thumbRect = new Rectangle(trackX, thumbY, ScrollBarWidth, thumbHeight);
        return (trackRect, thumbRect);
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        var previousSmoothingMode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = previousSmoothingMode;
    }

    // ── Mouse interaction ────────────────────────────────────────

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused)
            Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var delta = e.Delta > 0 ? -ScrollWheelDelta : ScrollWheelDelta;
        _scrollOffset += delta;
        ClampScroll();
        UpdateHoveredIndex(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            HandleScrollBarDrag(e.Y);
            return;
        }

        UpdateHoveredIndex(e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (!Focused)
            Focus();

        if (_items.Count > VisibleItemCount())
        {
            var (_, thumbRect) = GetScrollBarGeometry();
            var trackX = ClientSize.Width - ScrollBarWidth - ScrollBarPadding - 4; // generous hit area
            if (e.X >= trackX)
            {
                if (thumbRect.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStartY = e.Y;
                    _dragStartOffset = _scrollOffset;
                    Capture = true;
                }
                else
                {
                    // Click on track — jump to that position
                    JumpScrollToY(e.Y);
                }
                Invalidate();
                return;
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            _toolTip.Hide(this);
            Invalidate();
        }
    }

    private void UpdateHoveredIndex(Point location)
    {
        var newIndex = HitTest(location);
        if (newIndex == _hoveredIndex) return;

        _hoveredIndex = newIndex;

        if (_hoveredIndex >= 0 && _hoveredIndex < _items.Count)
        {
            var entry = _items[_hoveredIndex];
            var tipText = $"[{entry.Timestamp:HH:mm:ss}] {entry.Level}: {entry.Message}";
            _toolTip.SetToolTip(this, tipText);
        }
        else
        {
            _toolTip.Hide(this);
            _toolTip.SetToolTip(this, null);
        }

        Invalidate();
    }

    private int HitTest(Point location)
    {
        // Don't hit-test over the scrollbar area
        if (location.X >= ClientSize.Width - ScrollBarWidth - ScrollBarPadding * 2)
            return -1;

        if (location.Y < 0) return -1;
        var row = location.Y / ItemHeight;
        var index = _scrollOffset + row;
        return index < _items.Count ? index : -1;
    }

    private void HandleScrollBarDrag(int mouseY)
    {
        var trackHeight = ClientSize.Height;
        var visible = VisibleItemCount();
        var totalItems = _items.Count;
        var maxScroll = Math.Max(1, totalItems - visible);
        var thumbHeight = Math.Max(20, (int)((double)visible / totalItems * trackHeight));
        var scrollableTrack = trackHeight - thumbHeight;

        if (scrollableTrack <= 0) return;

        var deltaY = mouseY - _dragStartY;
        var deltaScroll = (int)((double)deltaY / scrollableTrack * maxScroll);
        _scrollOffset = _dragStartOffset + deltaScroll;
        ClampScroll();
        Invalidate();
    }

    private void JumpScrollToY(int mouseY)
    {
        var trackHeight = ClientSize.Height;
        var visible = VisibleItemCount();
        var totalItems = _items.Count;
        var maxScroll = Math.Max(1, totalItems - visible);

        _scrollOffset = (int)((double)mouseY / trackHeight * maxScroll);
        ClampScroll();
        Invalidate();
    }

    // ── Scroll helpers ───────────────────────────────────────────

    private int VisibleItemCount() => Math.Max(1, ClientSize.Height / ItemHeight);

    private bool IsScrolledToBottom()
    {
        var visible = VisibleItemCount();
        return _items.Count <= visible || _scrollOffset >= _items.Count - visible;
    }

    private void ScrollToBottom()
    {
        _scrollOffset = Math.Max(0, _items.Count - VisibleItemCount());
    }

    private void ClampScroll()
    {
        var maxScroll = Math.Max(0, _items.Count - VisibleItemCount());
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        var previousOffset = _scrollOffset;
        ClampScroll();
        if (_scrollOffset != previousOffset)
            Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}

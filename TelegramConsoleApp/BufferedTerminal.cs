using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TelegramConsoleApp;

/// <summary>
/// AvalonEdit-based, append-only terminal surface with bounded buffering.
/// It avoids FlowDocument layout and coalesces burst traffic into one UI update.
/// </summary>
public sealed class BufferedTerminal : TextEditor
{
    private const int DefaultMaximumPendingLines = 3000;
    private const int DefaultMaximumBatchLines = 200;
    private readonly Queue<PendingLine> _pending = new();
    private readonly Queue<object> _recentKeyOrder = new();
    private readonly HashSet<object> _recentKeys = [];
    private readonly object _pendingSync = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly TextSegmentCollection<ColoredSegment> _segments;
    private int _droppedCount;
    private int _flushRequested;

    public int MaximumLines { get; set; } = 1000;
    public int MaximumCharacters { get; set; } = 500_000;
    public int MaximumPendingLines { get; set; } = DefaultMaximumPendingLines;
    public int MaximumBatchLines { get; set; } = DefaultMaximumBatchLines;
    public bool AutoScroll { get; set; } = true;

    public BufferedTerminal()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        WordWrap = true;
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        Options.EnableEmailHyperlinks = false;
        Options.EnableHyperlinks = false;
        Options.EnableTextDragDrop = false;

        _segments = new TextSegmentCollection<ColoredSegment>(Document);
        TextArea.TextView.LineTransformers.Add(new SegmentColorizer(_segments));
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _flushTimer.Tick += FlushTimer_Tick;
        Unloaded += (_, _) => _flushTimer.Stop();
        Loaded += (_, _) =>
        {
            lock (_pendingSync)
            {
                if (_pending.Count > 0) RequestFlush();
            }
        };
    }

    public void AppendLine(
        string text,
        Brush? foreground = null,
        object? tag = null,
        object? deduplicationKey = null)
    {
        var color = Freeze(foreground ?? Foreground ?? Brushes.White);
        lock (_pendingSync)
        {
            if (deduplicationKey is not null)
            {
                if (!_recentKeys.Add(deduplicationKey)) return;
                _recentKeyOrder.Enqueue(deduplicationKey);
                var maximumRecentKeys = Math.Max(100, MaximumLines * 2);
                while (_recentKeyOrder.Count > maximumRecentKeys)
                    _recentKeys.Remove(_recentKeyOrder.Dequeue());
            }
            _pending.Enqueue(new PendingLine(Normalize(text), color, tag));
            while (_pending.Count > Math.Max(1, MaximumPendingLines))
            {
                _pending.Dequeue();
                _droppedCount++;
            }
        }
        RequestFlush();
    }

    public void ClearOutput()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ClearOutput);
            return;
        }

        _flushTimer.Stop();
        Interlocked.Exchange(ref _flushRequested, 0);
        lock (_pendingSync)
        {
            _pending.Clear();
            _recentKeyOrder.Clear();
            _recentKeys.Clear();
            _droppedCount = 0;
        }
        Document.Text = string.Empty;
        _segments.Clear();
    }

    public T? GetTagAtVisualPosition<T>(System.Windows.Point visualPosition) where T : class
    {
        var view = TextArea.TextView;
        var position = view.GetPosition(visualPosition);
        if (position is null || position.Value.Line < 1 || position.Value.Line > Document.LineCount) return null;
        var offset = Document.GetOffset(position.Value.Location);
        if (SelectionLength > 0 && offset >= SelectionStart && offset <= SelectionStart + SelectionLength)
            offset = SelectionStart;
        return GetTagAtOffset<T>(offset);
    }

    public T? GetTagAtOffset<T>(int offset) where T : class
    {
        if (Document.TextLength == 0) return null;
        offset = Math.Clamp(offset, 0, Document.TextLength - 1);
        return _segments.FindSegmentsContaining(offset)
            .OrderByDescending(x => x.StartOffset)
            .Select(x => x.Tag)
            .OfType<T>()
            .FirstOrDefault();
    }

    private void RequestFlush()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _flushRequested, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_flushTimer.IsEnabled) _flushTimer.Start();
        }));
    }

    private void FlushTimer_Tick(object? sender, EventArgs e)
    {
        _flushTimer.Stop();
        Interlocked.Exchange(ref _flushRequested, 0);

        var batch = new List<PendingLine>(Math.Max(1, MaximumBatchLines) + 1);
        bool hasPending;
        lock (_pendingSync)
        {
            if (_droppedCount > 0)
            {
                batch.Add(new PendingLine(
                    $"[性能保护] 已跳过 {_droppedCount} 条积压消息",
                    Freeze(Brushes.Goldenrod), null));
                _droppedCount = 0;
            }

            var limit = Math.Max(1, MaximumBatchLines);
            while (batch.Count < limit && _pending.Count > 0)
                batch.Add(_pending.Dequeue());
            hasPending = _pending.Count > 0;
        }

        if (batch.Count > 0)
        {
            Document.BeginUpdate();
            try
            {
                foreach (var line in batch) AppendToDocument(line);
                EnforceLimits();
            }
            finally
            {
                Document.EndUpdate();
            }

            if (AutoScroll) ScrollToEnd();
        }

        if (hasPending) RequestFlush();
    }

    private void AppendToDocument(PendingLine line)
    {
        var content = line.Text + Environment.NewLine;
        var offset = Document.TextLength;
        Document.Insert(offset, content);
        _segments.Add(new ColoredSegment
        {
            StartOffset = offset,
            Length = line.Text.Length,
            Foreground = line.Foreground,
            Tag = line.Tag
        });
    }

    private void EnforceLimits()
    {
        var maximumLines = Math.Max(10, MaximumLines);
        var excessLines = Document.LineCount - maximumLines - 1;
        if (excessLines > 0)
        {
            var firstRetainedLine = Document.GetLineByNumber(excessLines + 1);
            if (firstRetainedLine.Offset > 0) Document.Remove(0, firstRetainedLine.Offset);
        }

        var maximumCharacters = Math.Max(10_000, MaximumCharacters);
        if (Document.TextLength <= maximumCharacters) return;
        var targetOffset = Document.TextLength - maximumCharacters;
        var line = Document.GetLineByOffset(Math.Min(targetOffset, Document.TextLength - 1));
        var removeLength = line.EndOffset + line.DelimiterLength;
        if (removeLength > 0) Document.Remove(0, removeLength);
    }

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static Brush Freeze(Brush brush)
    {
        if (brush.IsFrozen) return brush;
        var clone = brush.CloneCurrentValue();
        if (clone.CanFreeze) clone.Freeze();
        return clone;
    }

    private sealed record PendingLine(string Text, Brush Foreground, object? Tag);

    private sealed class ColoredSegment : TextSegment
    {
        public required Brush Foreground { get; init; }
        public object? Tag { get; init; }
    }

    private sealed class SegmentColorizer(TextSegmentCollection<ColoredSegment> segments)
        : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            foreach (var segment in segments.FindOverlappingSegments(line.Offset, line.Length))
            {
                var start = Math.Max(line.Offset, segment.StartOffset);
                var end = Math.Min(line.EndOffset, segment.EndOffset);
                if (start >= end) continue;
                ChangeLinePart(start, end, element =>
                    element.TextRunProperties.SetForegroundBrush(segment.Foreground));
            }
        }
    }
}

// AlbumWall — the wall's own virtualising layout.
//
// THIS EXISTS TO DELETE A CLASS OF BUG, NOT TO SAVE A DEPENDENCY.
//
// ItemsRepeater ESTIMATES its total extent from the average height of the rows
// it happens to have realised. Every scroll bug in this app came out of that
// single fact: panels opening at the wrong position, an album opening completely
// offscreen, the wall going blank below an open panel, the open album sliding
// out of view when the transport appeared. Each was a different symptom of the
// scroll offset being an absolute number into a guess that moves.
//
// This wall never has to guess. Every cover row is exactly the same height —
// CoverPx plus the label block — and there is AT MOST ONE panel, which is kept
// realised and measured. So the extent is arithmetic:
//
//     rows * rowHeight + panelHeight + spacing between them
//
// exact at every scroll position, for a library of any size. Virtualisation
// still applies: only the rows the viewport touches are built.
//
// The pooling rule that ItemsRepeater got wrong is kept, because it was right:
// an element built for a row of covers is only ever reused for another row of
// covers. The panel is never pooled at all — there is one.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Avalonia.VisualTree;

namespace AlbumWall.App;

public sealed class WallElementEventArgs(Control element) : EventArgs
{
    public Control Element { get; } = element;
}

public sealed class WallView : Panel
{
    /// The gap between rows.
    public double Spacing { get; set; } = 15;

    /// The height of one row of covers, which the window computes: the cover
    /// plus its two lines of label. Supplied rather than measured because it is
    /// known exactly and the same for every row — which is the whole point.
    public double RowHeight
    {
        get => _rowHeight;
        set { if (Math.Abs(_rowHeight - value) < 0.01) return; _rowHeight = value; InvalidateMeasure(); }
    }

    [Content]
    public IDataTemplate? Rows { get; set; }
    public IDataTemplate? PanelTemplate { get; set; }

    /// Raised when a row becomes real — the moment to ask for its covers.
    public event EventHandler<WallElementEventArgs>? ElementPrepared;

    private double _rowHeight = 227;
    private IList<WallItem>? _items;
    private ScrollViewer? _scroller;

    private readonly Dictionary<int, Control> _realised = [];
    private readonly Stack<Control> _rowPool = [];

    public IList<WallItem>? ItemsSource
    {
        get => _items;
        set
        {
            if (ReferenceEquals(_items, value)) return;

            if (_items is System.Collections.Specialized.INotifyCollectionChanged old)
                old.CollectionChanged -= OnItemsChanged;

            _items = value;

            if (_items is System.Collections.Specialized.INotifyCollectionChanged now)
                now.CollectionChanged += OnItemsChanged;

            Reset();
        }
    }

    /// The realised element for a row, or null if it is not on screen.
    ///
    /// Same shape as ItemsRepeater's, because the window already speaks it — but
    /// with no GetOrCreateElement counterpart. That method was a trap there (it
    /// pinned an element outside the virtualisation flow and stopped the realised
    /// window extending past it) and there is no need for one here: the anchor
    /// can compute any row's exact position without the element existing.
    public Control? TryGetElement(int index) => _realised.GetValueOrDefault(index);

    /// Where a row sits in the wall's own coordinates. EXACT, and available for
    /// every row whether or not it has been built.
    public double OffsetOf(int index)
    {
        var y = 0.0;
        for (var i = 0; i < index && i < (_items?.Count ?? 0); i++)
            y += HeightOf(i) + Spacing;
        return y;
    }

    public double HeightOf(int index)
    {
        if (_items is null || index < 0 || index >= _items.Count) return 0;
        if (_items[index] is not PanelRow) return _rowHeight;

        // The panel is the only row whose height is not known in advance, so it
        // is the only one that has to be asked. It is kept realised precisely so
        // the answer is always available — including while it is animating open,
        // and including when it has been scrolled past.
        return _realised.TryGetValue(index, out var el) ? el.DesiredSize.Height : 0;
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Reset();

    /// Drops every realised row, ready to lay the wall out again.
    ///
    /// It does NOT clear Children. Retire() puts row elements in the pool and
    /// they stay in the visual tree, hidden; clearing Children as well orphans
    /// them, so the next reuse hands back an element that is in the pool and in
    /// no tree — which draws nothing. That is a blank band where a row should
    /// be, and it appears after any change to the row list, which is to say
    /// every time an album opens.
    private void Reset()
    {
        foreach (var (_, el) in _realised) Retire(el);
        _realised.Clear();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var count = _items?.Count ?? 0;
        if (count == 0) return new Size(width, 0);

        // The panel must be measured before the extent can be stated, so realise
        // it first and keep it. There is one, so this costs nothing.
        for (var i = 0; i < count; i++)
        {
            if (_items![i] is not PanelRow) continue;
            var el = Realise(i);
            el.Measure(new Size(width, double.PositiveInfinity));
            break;
        }

        var total = 0.0;
        for (var i = 0; i < count; i++) total += HeightOf(i) + (i < count - 1 ? Spacing : 0);

        // Measure the rows that are already real, so their own layout is current.
        foreach (var (i, el) in _realised)
            if (_items![i] is not PanelRow)
                el.Measure(new Size(width, _rowHeight));

        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = _items?.Count ?? 0;
        if (count == 0) return finalSize;

        AttachScroller();

        // NOT finalSize.Height. Inside a ScrollViewer that is the WHOLE EXTENT of
        // the wall, not the window onto it, so using it as the overscan realised
        // every row in the library — 194 covers decoded at startup instead of a
        // screenful, which is the exact cost virtualisation exists to avoid.
        var offset = _scroller?.Offset.Y ?? 0;
        var viewport = _scroller?.Viewport.Height is > 0 ? _scroller.Viewport.Height : 1000;

        // One screen of overscan either side, so a flick does not arrive at a
        // blank strip before the next row is built.
        var top = offset - viewport;
        var bottom = offset + viewport * 2;

        var y = 0.0;
        var wanted = new List<int>();
        var offsets = new Dictionary<int, double>();

        for (var i = 0; i < count; i++)
        {
            var h = HeightOf(i);
            var isPanel = _items![i] is PanelRow;

            // The panel stays realised wherever it is: its height is part of the
            // extent, and the window animates it by hand.
            if (isPanel || (y + h >= top && y <= bottom))
            {
                wanted.Add(i);
                offsets[i] = y;
            }

            y += h + Spacing;
        }

        foreach (var i in _realised.Keys.Where(i => !wanted.Contains(i)).ToList())
        {
            Retire(_realised[i]);
            _realised.Remove(i);
        }

        foreach (var i in wanted)
        {
            var el = Realise(i);
            var h = HeightOf(i);
            el.Measure(new Size(finalSize.Width, _items![i] is PanelRow ? double.PositiveInfinity : h));
            el.Arrange(new Rect(0, offsets[i], finalSize.Width, _items[i] is PanelRow ? el.DesiredSize.Height : h));
        }

        return finalSize;
    }

    /// Builds or reuses the element for a row.
    private Control Realise(int index)
    {
        if (_realised.TryGetValue(index, out var existing)) return existing;

        var data = _items![index];
        var isPanel = data is PanelRow;

        Control el;
        if (!isPanel && _rowPool.Count > 0)
        {
            // A pooled row takes the new data directly: its bindings resolve
            // against the same type it was built for, always.
            el = _rowPool.Pop();
            el.DataContext = data;
            el.IsVisible = true;
        }
        else
        {
            el = (isPanel ? PanelTemplate : Rows)?.Build(data)
                 ?? throw new InvalidOperationException($"No template for {data.GetType().Name}");
            el.DataContext = data;
            Children.Add(el);
        }

        _realised[index] = el;
        ElementPrepared?.Invoke(this, new WallElementEventArgs(el));
        return el;
    }

    /// Returns a row element to the pool, undoing what the animations left on it.
    private void Retire(Control el)
    {
        if (el.DataContext is PanelRow)
        {
            // Never pooled. The panel is built fresh each time it opens, so it
            // can never come back wearing the last album's colours.
            Children.Remove(el);
            return;
        }

        el.Transitions = null;
        el.Height = double.NaN;
        el.Opacity = 1;
        el.IsVisible = false;
        _rowPool.Push(el);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachScroller();
    }

    /// The ScrollViewer is what says which rows are worth building, so the wall
    /// has to re-arrange whenever it moves.
    private void AttachScroller()
    {
        if (_scroller is not null) return;

        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is null) return;

        _scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty)
                InvalidateArrange();
        };
    }
}

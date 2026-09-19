// AlbumWall — builds and pools the wall's rows.
//
// The wall holds two different kinds of item: a run of covers, or an open panel.
// Getting ItemsRepeater to cope with that is not a matter of picking a template
// per item, which is what the first two attempts assumed.
//
// ItemsRepeater does not build an element per item. It POOLS them: when a row
// scrolls out of view the element is kept and handed to whatever item comes next,
// with only its DataContext swapped. Its default pool is a single bucket shared by
// every item, so an element built for a row of covers gets handed a PanelRow, the
// row's `ItemsSource` binding tries to cast PanelRow to AlbumRow, and the whole
// panel renders as nothing. Implementing IDataTemplate did not help, and neither
// did IRecyclingDataTemplate: the pooling happens above both of them, in the
// element factory, so the factory is where it has to be fixed.
//
// One pool per kind. An element is only ever reused for the kind of row it was
// built for.

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace AlbumWall.App;

public sealed class WallItemTemplate : IDataTemplate, IElementFactory
{
    [Content]
    public IDataTemplate? Rows { get; set; }

    public IDataTemplate? Panel { get; set; }

    private readonly Stack<Control> _rowPool = new();
    private readonly Stack<Control> _panelPool = new();

    public bool Match(object? data) => data is WallItem;

    public Control? Build(object? data) => Create(data);

    public Control GetElement(ElementFactoryGetArgs args)
    {
        var panel = args.Data is PanelRow;
        var pool = panel ? _panelPool : _rowPool;

        // A pooled element of the right kind can take the new data directly; its
        // bindings all resolve against the same type they were built for.
        if (pool.Count > 0)
        {
            var reused = pool.Pop();
            reused.DataContext = args.Data;
            return reused;
        }

        var built = Create(args.Data)
            ?? throw new InvalidOperationException($"No template for {args.Data?.GetType().Name}");
        built.DataContext = args.Data;
        return built;
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is not { } el) return;

        // Reset what the wall's animations left behind. The unfold and fold-away
        // set an explicit Height, an Opacity and a Transitions collection on the
        // element; a pooled element carrying those comes back collapsed and
        // invisible, and every open/close made the wall a little more broken.
        el.Transitions = null;
        el.Height = double.NaN;
        el.Opacity = 1;

        // Tag, not the DataContext: by recycle time ItemsRepeater may already have
        // cleared the DataContext, so the element itself has to remember what it
        // was built as.
        (el.Tag as string == "panel" ? _panelPool : _rowPool).Push(el);
    }

    private Control? Create(object? data)
    {
        var panel = data is PanelRow;
        if (!panel && data is not AlbumRow) return null;

        var built = (panel ? Panel : Rows)?.Build(data);
        if (built is not null) built.Tag = panel ? "panel" : "row";
        return built;
    }
}

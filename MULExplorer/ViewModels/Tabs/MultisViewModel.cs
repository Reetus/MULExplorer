using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A multi in the list: "  100 (0x64) Small Stone and Plaster House".</summary>
public sealed record MultiRow( int Id, string Label, string Kind );

/// <summary>One component line of the selected multi.</summary>
public sealed record MultiComponentRow( string Item, int X, int Y, int Z, int Flags, int Unk1, string Visible );

/// <summary>
///     Multis tab: list of multis (named from UOFiddler's Multilist), the composited image with a
///     max-height cut, and the component list. Shows MultiCollection.uop or multi.mul (switchable when
///     both exist, like UOFiddler's two sub-tabs).
/// </summary>
public sealed partial class MultisViewModel : TabViewModel
{
    private IReadOnlyList<MultiRow> _allRows = [];
    private MultiComponents? _multi;
    private bool _updating;

    public MultisViewModel() : base( "Multis", Strings.TabMultis )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<MultiRow> VisibleRows { get; private set; } = [];

    [ObservableProperty]
    public partial MultiRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Sources present in the client (UOP first).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MultiSource> Sources { get; private set; } = [];

    [ObservableProperty]
    public partial MultiSource Source { get; set; }

    [ObservableProperty]
    public partial bool HasSourceChoice { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    public partial int MaxHeightLimit { get; private set; }

    /// <summary>Components above this z are hidden (UOFiddler's height slider).</summary>
    [ObservableProperty]
    public partial int HeightCut { get; set; }

    [ObservableProperty]
    public partial bool ShowInvisible { get; set; }

    /// <summary>0 = graphic, 1 = component list.</summary>
    [ObservableProperty]
    public partial int PageIndex { get; set; }

    /// <summary>Scale large multis down to the preview (UOFiddler's "Fit preview to window", on by default).</summary>
    [ObservableProperty]
    public partial bool FitToWindow { get; set; } = true;

    [ObservableProperty]
    public partial string HeightLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string MultiStatus { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasNoImage { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<MultiComponentRow> Components { get; private set; } = [];

    public int SelectedId => SelectedRow?.Id ?? -1;

    public override IReadOnlyList<string> Actions => ["set_max_height", "set_source", "set_show_invisible", "set_fit_to_window", "show_graphic", "show_components"];

    protected override async Task LoadAsync( UOData data )
    {
        var multis = await Task.Run( () => data.Multis );
        Sources = multis.Sources;
        HasSourceChoice = Sources.Count > 1;
        _updating = true;
        Source = multis.DefaultSource;
        _updating = false;
        RebuildRows( data );
    }

    /// <summary>Cheap (array lookups), so it runs inline and source switches settle synchronously.</summary>
    private void RebuildRows( UOData data )
    {
        var previous = SelectedRow?.Id ?? -1;
        _allRows = data.Multis.GetIds( Source ).Select( CreateRow ).ToList();
        ApplyFilter();
        _updating = true;
        SelectedRow = VisibleRows.FirstOrDefault( r => r.Id == previous ) ?? VisibleRows.FirstOrDefault();
        _updating = false;
        UpdateSelection();
    }

    private static MultiRow CreateRow( int id )
    {
        var name = MultiNames.GetName( id );
        var label = $"{id,5} (0x{id:X}) {name}".TrimEnd();
        var kind = MultiNames.GetKind( id ) switch
        {
            MultiKind.Boat => MultisStrings.KindBoat,
            MultiKind.House => MultisStrings.KindHouse,
            MultiKind.Decoration => MultisStrings.KindDecoration,
            MultiKind.Other => MultisStrings.KindOther,
            _ => ""
        };

        return new MultiRow( id, label, kind );
    }

    protected override void Reset()
    {
        _allRows = [];
        _multi = null;
        VisibleRows = [];
        SelectedRow = null;
        Sources = [];
        HasSourceChoice = false;
        Image = null;
        Components = [];
        MultiStatus = "";
        Summary = "";
        HasNoImage = false;
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedRowChanged( MultiRow? value )
    {
        if ( !_updating )
        {
            UpdateSelection();
        }
    }

    partial void OnSourceChanged( MultiSource value )
    {
        if ( !_updating && Data is { } data && _allRows.Count > 0 )
        {
            RebuildRows( data );
        }
    }

    partial void OnHeightCutChanged( int value )
    {
        if ( !_updating )
        {
            Render();
        }
    }

    partial void OnShowInvisibleChanged( bool value ) => Render();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

        VisibleRows = text.Length == 0
            ? _allRows
            : _allRows.Where( r => r.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                                   r.Id.ToString( "X", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) ||
                                   r.Kind.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();

        Summary = string.Format( Strings.EntriesShown, VisibleRows.Count, _allRows.Count );
    }

    private void UpdateSelection()
    {
        _multi = Data is { } data && SelectedRow is { } row ? data.Multis.Get( row.Id, Source ) : null;

        if ( _multi is not { } multi )
        {
            Image = null;
            Components = [];
            MultiStatus = "";
            MaxHeightLimit = 0;
            HasNoImage = false;
            return;
        }

        _updating = true;
        MaxHeightLimit = multi.MaxHeight;
        HeightCut = multi.MaxHeight;
        _updating = false;

        MultiStatus = string.Format( CultureInfo.CurrentCulture, MultisStrings.Status, multi.Width, multi.Height, multi.MaxHeight,
            multi.MinX, multi.MinY, multi.MaxX, multi.MaxY, multi.Surface, multi.Tiles.Length );

        Components = multi.Tiles.Select( t => new MultiComponentRow( $"0x{t.ItemId:X4}", t.X, t.Y, t.Z, t.Flags, t.Unk1,
            t.IsVisible ? MultisStrings.Yes : MultisStrings.No ) ).ToList();

        Render();
    }

    private void Render()
    {
        HeightLabel = string.Format( CultureInfo.CurrentCulture, MultisStrings.MaxHeight, HeightCut );

        if ( _multi is null || Data is not { } data )
        {
            Image = null;
            return;
        }

        var pixmap = MultiRenderer.Render( _multi, data.Art, data.TryGet( d => d.TileData ), HeightCut, ShowInvisible );
        Image = pixmap.ToBitmap();
        HasNoImage = Image is null;
    }

    public override bool SelectEntry( int id )
    {
        if ( Data is null || !Data.Multis.IsValid( id, Source ) )
        {
            return false;
        }

        if ( VisibleRows.All( r => r.Id != id ) )
        {
            SearchText = "";
        }

        SelectedRow = VisibleRows.First( r => r.Id == id );
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleRows.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_max_height":
                HeightCut = Math.Clamp( args["height"]?.GetValue<int>() ?? MaxHeightLimit, 0, Math.Max( 0, MaxHeightLimit ) );
                return GetState();

            case "set_show_invisible":
                ShowInvisible = args["value"]?.GetValue<bool>() ?? true;
                return GetState();

            case "set_fit_to_window":
                FitToWindow = args["value"]?.GetValue<bool>() ?? true;
                return GetState();

            case "show_graphic":
                PageIndex = 0;
                return GetState();

            case "show_components":
                PageIndex = 1;
                return GetState();

            case "set_source":
                var name = args["source"]?.GetValue<string>();

                if ( !Enum.TryParse<MultiSource>( name, true, out var source ) || !Sources.Contains( source ) )
                {
                    throw new ArgumentException( $"Unknown or unavailable source '{name}'. Available: {string.Join( ", ", Sources )}" );
                }

                Source = source;
                return GetState();
        }

        return base.Invoke( action, args );
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["source"] = Source.ToString();
        state["sources"] = new JsonArray( Sources.Select( s => ( JsonNode? )s.ToString() ).ToArray() );
        state["total"] = _allRows.Count;
        state["visible"] = VisibleRows.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedId;
        state["selectedName"] = SelectedRow is { } row ? MultiNames.GetName( row.Id ) : null;
        state["components"] = Components.Count;
        state["maxHeight"] = MaxHeightLimit;
        state["heightCut"] = HeightCut;
        state["showInvisible"] = ShowInvisible;
        state["fitToWindow"] = FitToWindow;
        state["page"] = PageIndex == 1 ? "components" : "graphic";
        state["imageSize"] = Image is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        state["status"] = MultiStatus;
        return state;
    }
}

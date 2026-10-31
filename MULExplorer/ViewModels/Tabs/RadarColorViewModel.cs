using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Imaging;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A radarcol.mul entry: tile id + name with its color swatch.</summary>
public sealed record RadarRow( int Id, string Label, IBrush Swatch );

/// <summary>
///     RadarColor tab: read-only view of radarcol.mul (UOFiddler's RadarColorControl without the
///     editing/averaging tools). Items and Land Tiles sub-views list every tile with its radar color;
///     the selection shows the art, a large swatch and the color's values. <see cref="SelectEntry" />
///     and <see cref="Search" /> act on the active sub-view (<c>show_items</c>/<c>show_land</c>).
/// </summary>
public sealed partial class RadarColorViewModel : TabViewModel
{
    public const int ItemsView = 0;
    public const int LandView = 1;

    private IReadOnlyList<RadarRow> _allItems = [];
    private IReadOnlyList<RadarRow> _allLand = [];

    public RadarColorViewModel() : base( "RadarColor", Strings.TabRadarColor )
    {
    }

    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<RadarRow> VisibleItems { get; private set; } = [];

    [ObservableProperty]
    public partial RadarRow? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string ItemSearch { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<RadarRow> VisibleLand { get; private set; } = [];

    [ObservableProperty]
    public partial RadarRow? SelectedLand { get; set; }

    [ObservableProperty]
    public partial string LandSearch { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? ArtPreview { get; private set; }

    [ObservableProperty]
    public partial IBrush? ColorBrush { get; private set; }

    [ObservableProperty]
    public partial string ColorHex { get; private set; } = "";

    [ObservableProperty]
    public partial string ColorRgb5 { get; private set; } = "";

    [ObservableProperty]
    public partial string ColorRgb8 { get; private set; } = "";

    [ObservableProperty]
    public partial string SelectionTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public override IReadOnlyList<string> Actions => ["show_items", "show_land"];

    protected override async Task LoadAsync( UOData data )
    {
        var (items, land) = await Task.Run( () =>
        {
            var radar = data.RadarColors;
            var tiles = data.TryGet( d => d.TileData );
            var brushes = new Dictionary<ushort, IBrush>();

            IBrush Brush( ushort c )
            {
                if ( !brushes.TryGetValue( c, out var b ) )
                {
                    brushes[c] = b = new ImmutableSolidColorBrush( Color.FromUInt32( Color16.ToArgb( c ) ) );
                }

                return b;
            }

            var itemRows = new List<RadarRow>( radar.ItemCount );

            for ( var id = 0; id < radar.ItemCount; id++ )
            {
                itemRows.Add( new RadarRow( id, Label( id, tiles?.GetItem( id ).Name ), Brush( radar.GetItemColor( id ) ) ) );
            }

            var landRows = new List<RadarRow>( radar.Colors.Length < 0x4000 ? radar.Colors.Length : 0x4000 );

            for ( var id = 0; id < Math.Min( 0x4000, radar.Colors.Length ); id++ )
            {
                landRows.Add( new RadarRow( id, Label( id, tiles?.GetLand( id ).Name ), Brush( radar.GetLandColor( id ) ) ) );
            }

            return ( itemRows, landRows );
        } );

        _allItems = items;
        _allLand = land;
        VisibleItems = Filter( _allItems, ItemSearch );
        VisibleLand = Filter( _allLand, LandSearch );
        SelectedItem ??= VisibleItems.FirstOrDefault();
        SelectedLand ??= VisibleLand.FirstOrDefault();
        UpdateSelection();
    }

    private static string Label( int id, string? name ) => $"0x{id:X4}  {name}".TrimEnd();

    protected override void Reset()
    {
        _allItems = [];
        _allLand = [];
        VisibleItems = [];
        VisibleLand = [];
        SelectedItem = null;
        SelectedLand = null;
        ArtPreview = null;
        ColorBrush = null;
        ColorHex = ColorRgb5 = ColorRgb8 = SelectionTitle = Summary = "";
    }

    partial void OnViewIndexChanged( int value ) => UpdateSelection();

    partial void OnItemSearchChanged( string value )
    {
        VisibleItems = Filter( _allItems, value );
        UpdateSummary();
    }

    partial void OnLandSearchChanged( string value )
    {
        VisibleLand = Filter( _allLand, value );
        UpdateSummary();
    }

    partial void OnSelectedItemChanged( RadarRow? value ) => UpdateSelection();

    partial void OnSelectedLandChanged( RadarRow? value ) => UpdateSelection();

    private static IReadOnlyList<RadarRow> Filter( IReadOnlyList<RadarRow> rows, string search )
    {
        var text = search.Trim();
        return text.Length == 0 ? rows : rows.Where( r => r.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();
    }

    private void UpdateSummary()
    {
        Summary = ViewIndex == LandView
            ? string.Format( Strings.EntriesShown, VisibleLand.Count, _allLand.Count )
            : string.Format( Strings.EntriesShown, VisibleItems.Count, _allItems.Count );
    }

    private void UpdateSelection()
    {
        UpdateSummary();
        var land = ViewIndex == LandView;

        if ( Data is not { } data || ( land ? SelectedLand : SelectedItem ) is not { } row || _allItems.Count + _allLand.Count == 0 )
        {
            ArtPreview = null;
            ColorBrush = null;
            ColorHex = ColorRgb5 = ColorRgb8 = SelectionTitle = "";
            return;
        }

        var radar = data.RadarColors;
        var color = land ? radar.GetLandColor( row.Id ) : radar.GetItemColor( row.Id );
        var argb = Color16.ToArgb( color );
        var art = data.TryGet( d => d.Art );

        ArtPreview = ( land ? art?.GetLand( row.Id ) : art?.GetItem( row.Id ) ).ToBitmap();
        ColorBrush = row.Swatch;
        SelectionTitle = row.Label;
        ColorHex = $"0x{color:X4}";
        ColorRgb5 = string.Format( CultureInfo.InvariantCulture, "{0}, {1}, {2}", ( color >> 10 ) & 0x1F, ( color >> 5 ) & 0x1F, color & 0x1F );
        ColorRgb8 = string.Format( CultureInfo.InvariantCulture, "{0}, {1}, {2}  (#{3:X6})", ( argb >> 16 ) & 0xFF, ( argb >> 8 ) & 0xFF, argb & 0xFF, argb & 0xFFFFFF );
    }

    public override bool SelectEntry( int id )
    {
        if ( ViewIndex == LandView )
        {
            if ( ( uint )id >= ( uint )_allLand.Count )
            {
                return false;
            }

            if ( VisibleLand.All( r => r.Id != id ) )
            {
                LandSearch = "";
            }

            SelectedLand = _allLand[id];
            return true;
        }

        if ( ( uint )id >= ( uint )_allItems.Count )
        {
            return false;
        }

        if ( VisibleItems.All( r => r.Id != id ) )
        {
            ItemSearch = "";
        }

        SelectedItem = _allItems[id];
        return true;
    }

    public override int Search( string text )
    {
        if ( ViewIndex == LandView )
        {
            LandSearch = text;
            return VisibleLand.Count;
        }

        ItemSearch = text;
        return VisibleItems.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "show_items":
                ViewIndex = ItemsView;
                return GetState();

            case "show_land":
                ViewIndex = LandView;
                return GetState();
        }

        return base.Invoke( action, args );
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        var land = ViewIndex == LandView;
        state["view"] = land ? "land" : "items";
        state["total"] = land ? _allLand.Count : _allItems.Count;
        state["visible"] = land ? VisibleLand.Count : VisibleItems.Count;
        state["search"] = land ? LandSearch : ItemSearch;
        state["selectedId"] = ( land ? SelectedLand : SelectedItem )?.Id ?? -1;
        state["color"] = ColorHex;
        state["rgb"] = ColorRgb8;
        state["previewSize"] = ArtPreview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}

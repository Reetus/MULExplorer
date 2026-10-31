using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A tiledata entry in a list: "0x0EED gold coin".</summary>
public sealed record TileRow( int Id, string Label );

/// <summary>One tiledata flag and whether the selected entry has it.</summary>
public sealed record FlagRow( string Name, bool IsSet );

/// <summary>
///     TileData tab: read-only version of UOFiddler's TileDataControl. An Items and a Land Tiles
///     sub-view, each a searchable list with the art, every field and every flag of the selection.
///     <see cref="SelectEntry" /> and <see cref="Search" /> act on the active sub-view; switch it with
///     the <c>show_items</c>/<c>show_land</c> actions.
/// </summary>
public sealed partial class TileDataViewModel : TabViewModel
{
    public const int ItemsView = 0;
    public const int LandView = 1;

    private IReadOnlyList<TileRow> _allItems = [];
    private IReadOnlyList<TileRow> _allLand = [];

    public TileDataViewModel() : base( "TileData", Strings.TabTileData )
    {
    }

    /// <summary>0 = items, 1 = land (the inner TabControl's index).</summary>
    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<TileRow> VisibleItems { get; private set; } = [];

    [ObservableProperty]
    public partial TileRow? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string ItemSearch { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? ItemPreview { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<TileRow> VisibleLand { get; private set; } = [];

    [ObservableProperty]
    public partial TileRow? SelectedLand { get; set; }

    [ObservableProperty]
    public partial string LandSearch { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? LandPreview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> ItemDetails { get; } = [];

    public ObservableCollection<FlagRow> ItemFlags { get; } = [];

    public ObservableCollection<PropertyRow> LandDetails { get; } = [];

    public ObservableCollection<FlagRow> LandFlags { get; } = [];

    public override IReadOnlyList<string> Actions => ["show_items", "show_land"];

    protected override async Task LoadAsync( UOData data )
    {
        var (items, land) = await Task.Run( () =>
        {
            var tiles = data.TileData;
            return (
                tiles.ItemTable.Select( i => new TileRow( i.Id, Label( i.Id, i.Name ) ) ).ToList(),
                tiles.LandTable.Select( l => new TileRow( l.Id, Label( l.Id, l.Name ) ) ).ToList() );
        } );

        _allItems = items;
        _allLand = land;
        ApplyItemFilter();
        ApplyLandFilter();
        SelectedItem ??= VisibleItems.FirstOrDefault();
        SelectedLand ??= VisibleLand.FirstOrDefault();
        UpdateItem();
        UpdateLand();
        UpdateSummary();
    }

    private static string Label( int id, string name ) => $"0x{id:X4}  {name}";

    protected override void Reset()
    {
        _allItems = [];
        _allLand = [];
        VisibleItems = [];
        VisibleLand = [];
        SelectedItem = null;
        SelectedLand = null;
        ItemPreview = null;
        LandPreview = null;
        ItemDetails.Clear();
        ItemFlags.Clear();
        LandDetails.Clear();
        LandFlags.Clear();
        Summary = "";
    }

    partial void OnViewIndexChanged( int value ) => UpdateSummary();

    partial void OnItemSearchChanged( string value ) => ApplyItemFilter();

    partial void OnLandSearchChanged( string value ) => ApplyLandFilter();

    partial void OnSelectedItemChanged( TileRow? value ) => UpdateItem();

    partial void OnSelectedLandChanged( TileRow? value ) => UpdateLand();

    private void ApplyItemFilter()
    {
        VisibleItems = Filter( _allItems, ItemSearch );
        UpdateSummary();
    }

    private void ApplyLandFilter()
    {
        VisibleLand = Filter( _allLand, LandSearch );
        UpdateSummary();
    }

    private static IReadOnlyList<TileRow> Filter( IReadOnlyList<TileRow> rows, string search )
    {
        var text = search.Trim();

        if ( text.Length == 0 )
        {
            return rows;
        }

        // Labels start with "0x" + hex id, so one substring test covers names and ids.
        return rows.Where( r => r.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();
    }

    private void UpdateSummary()
    {
        Summary = ViewIndex == LandView
            ? string.Format( Strings.EntriesShown, VisibleLand.Count, _allLand.Count )
            : string.Format( Strings.EntriesShown, VisibleItems.Count, _allItems.Count );
    }

    private void UpdateItem()
    {
        ItemDetails.Clear();
        ItemFlags.Clear();

        if ( Data is not { } data || SelectedItem is not { } row || _allItems.Count == 0 )
        {
            ItemPreview = null;
            return;
        }

        var item = data.TileData.GetItem( row.Id );
        var art = data.TryGet( d => d.Art );
        var size = art?.GetItemSize( row.Id );
        ItemPreview = art?.GetItem( row.Id ).ToBitmap();

        string N( int v ) => v.ToString( CultureInfo.CurrentCulture );

        ItemDetails.Add( new PropertyRow( Strings.Id, $"0x{row.Id:X4} ({row.Id})" ) );
        ItemDetails.Add( new PropertyRow( Strings.Name, item.Name ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.ArtSize, size is { } s ? $"{s.Width} × {s.Height}" : "-" ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Weight, N( item.Weight ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Layer, N( item.Quality ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Quantity, N( item.Quantity ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Animation, $"0x{item.Animation:X4} ({item.Animation})" ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.TileHue, N( item.Hue ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.StackingOffset, N( item.StackingOffset ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Value, N( item.Value ) ) );
        ItemDetails.Add( new PropertyRow( Strings.Height, N( item.Height ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.MiscData, N( item.MiscData ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Unknown2, N( item.Unknown2 ) ) );
        ItemDetails.Add( new PropertyRow( TileDataStrings.Unknown3, N( item.Unknown3 ) ) );
        ItemDetails.Add( new PropertyRow( Strings.Flags, $"0x{( ulong )item.Flags:X}" ) );
        AddFlags( ItemFlags, item.Flags );
    }

    private void UpdateLand()
    {
        LandDetails.Clear();
        LandFlags.Clear();

        if ( Data is not { } data || SelectedLand is not { } row || _allLand.Count == 0 )
        {
            LandPreview = null;
            return;
        }

        var land = data.TileData.GetLand( row.Id );
        LandPreview = data.TryGet( d => d.Art )?.GetLand( row.Id ).ToBitmap();

        LandDetails.Add( new PropertyRow( Strings.Id, $"0x{row.Id:X4} ({row.Id})" ) );
        LandDetails.Add( new PropertyRow( Strings.Name, land.Name ) );
        LandDetails.Add( new PropertyRow( TileDataStrings.TextureId, $"0x{land.TextureId:X4} ({land.TextureId})" ) );
        LandDetails.Add( new PropertyRow( Strings.Flags, $"0x{( ulong )land.Flags:X}" ) );
        AddFlags( LandFlags, land.Flags );
    }

    private static void AddFlags( ObservableCollection<FlagRow> target, TileFlag flags )
    {
        foreach ( var flag in Enum.GetValues<TileFlag>() )
        {
            if ( flag != TileFlag.None )
            {
                target.Add( new FlagRow( flag.ToString(), flags.HasFlag( flag ) ) );
            }
        }
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
        var details = land ? LandDetails : ItemDetails;
        state["view"] = land ? "land" : "items";
        state["format"] = Data?.TryGet( d => d.TileData ) is { } t ? t.IsNewFormat ? "new" : "old" : null;
        state["total"] = land ? _allLand.Count : _allItems.Count;
        state["visible"] = land ? VisibleLand.Count : VisibleItems.Count;
        state["search"] = land ? LandSearch : ItemSearch;
        state["selectedId"] = ( land ? SelectedLand : SelectedItem )?.Id ?? -1;
        state["selectedName"] = details.FirstOrDefault( d => d.Label == Strings.Name )?.Value;
        state["selectedFlags"] = new JsonArray( ( land ? LandFlags : ItemFlags ).Where( f => f.IsSet ).Select( f => ( JsonNode? )f.Name ).ToArray() );
        state["previewSize"] = ( land ? LandPreview : ItemPreview ) is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}

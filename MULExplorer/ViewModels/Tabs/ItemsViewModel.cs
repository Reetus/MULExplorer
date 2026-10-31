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

/// <summary>One label/value line in a details panel.</summary>
public sealed record PropertyRow( string Label, string Value );

/// <summary>
///     Items tab: every static with art, a thumbnail grid, a large preview (optionally hued) and
///     the item's tiledata. This is the reference implementation other tabs follow.
/// </summary>
public sealed partial class ItemsViewModel : TabViewModel
{
    private IReadOnlyList<int> _allIds = [];
    private BitmapCache? _thumbnails;

    public ItemsViewModel() : base( "Items", Strings.TabItems )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<int> VisibleIds { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedId { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Game hue applied to the preview (0 = none).</summary>
    [ObservableProperty]
    public partial int PreviewHue { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public ObservableCollection<string> SelectedFlags { get; } = [];

    public Func<int, Bitmap?> ThumbnailProvider => id => _thumbnails?.Get( id );

    public override IReadOnlyList<string> Actions => ["set_preview_hue"];

    protected override async Task LoadAsync( UOData data )
    {
        var ids = await Task.Run( () =>
        {
            var art = data.Art;
            var count = Math.Min( art.ItemCount, data.TileData.ItemTable.Length );
            var list = new List<int>( count );

            for ( var id = 0; id < count; id++ )
            {
                if ( art.IsValidItem( id ) )
                {
                    list.Add( id );
                }
            }

            return list;
        } );

        _thumbnails = new BitmapCache( id => data.Art.GetItem( id ).ToBitmap() );
        _allIds = ids;
        ApplyFilter();
        OnPropertyChanged( nameof( ThumbnailProvider ) );

        if ( SelectedId < 0 && ids.Count > 0 )
        {
            SelectedId = ids[0];
        }
        else
        {
            UpdateSelection();
        }
    }

    protected override void Reset()
    {
        _thumbnails?.Clear();
        _thumbnails = null;
        _allIds = [];
        VisibleIds = [];
        Preview = null;
        Details.Clear();
        SelectedFlags.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedIdChanged( int value ) => UpdateSelection();

    partial void OnPreviewHueChanged( int value ) => UpdateSelection();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();

        if ( text.Length == 0 || Data is null )
        {
            VisibleIds = _allIds;
        }
        else
        {
            var tiles = Data.TileData;
            var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

            VisibleIds = _allIds
                .Where( id => tiles.GetItem( id ).Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                              id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) )
                .ToList();
        }

        Summary = string.Format( Strings.EntriesShown, VisibleIds.Count, _allIds.Count );
    }

    private void UpdateSelection()
    {
        Details.Clear();
        SelectedFlags.Clear();

        if ( Data is not { } data || SelectedId < 0 || !IsLoadedOrLoading )
        {
            Preview = null;
            return;
        }

        var item = data.TileData.GetItem( SelectedId );
        var pixmap = data.Art.GetItem( SelectedId );

        if ( pixmap is not null && PreviewHue > 0 )
        {
            pixmap = data.Hues.Apply( pixmap, PreviewHue, item.Flags.HasFlag( TileFlag.PartialHue ) );
        }

        Preview = pixmap.ToBitmap();

        Details.Add( new PropertyRow( Strings.Id, $"0x{SelectedId:X4} ({SelectedId})" ) );
        Details.Add( new PropertyRow( Strings.Name, item.Name ) );
        Details.Add( new PropertyRow( ItemsStrings.ArtSize, pixmap is null ? "-" : $"{pixmap.Width} × {pixmap.Height}" ) );
        Details.Add( new PropertyRow( ItemsStrings.Weight, item.Weight.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( Strings.Height, item.Height.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Layer, item.Quality.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Quantity, item.Quantity.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Animation, $"0x{item.Animation:X4} ({item.Animation})" ) );
        Details.Add( new PropertyRow( ItemsStrings.TileHue, item.Hue.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.StackingOffset, item.StackingOffset.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Value, item.Value.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.MiscData, item.MiscData.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Unknown2, item.Unknown2.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ItemsStrings.Unknown3, item.Unknown3.ToString( CultureInfo.CurrentCulture ) ) );

        foreach ( var flag in Enum.GetValues<TileFlag>() )
        {
            if ( flag != TileFlag.None && item.Flags.HasFlag( flag ) )
            {
                SelectedFlags.Add( flag.ToString() );
            }
        }
    }

    private bool IsLoadedOrLoading => _allIds.Count > 0;

    public override bool SelectEntry( int id )
    {
        if ( Data is null || !Data.Art.IsValidItem( id ) )
        {
            return false;
        }

        if ( TileGridIndexOf( VisibleIds, id ) < 0 )
        {
            SearchText = "";
        }

        SelectedId = id;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleIds.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        if ( action == "set_preview_hue" )
        {
            PreviewHue = args["hue"]?.GetValue<int>() ?? 0;
            return GetState();
        }

        return base.Invoke( action, args );
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _allIds.Count;
        state["visible"] = VisibleIds.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedId;
        state["previewHue"] = PreviewHue;
        state["selectedName"] = Details.FirstOrDefault( d => d.Label == Strings.Name )?.Value;
        state["selectedFlags"] = new JsonArray( SelectedFlags.Select( f => ( JsonNode? )f ).ToArray() );
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }

    private static int TileGridIndexOf( IReadOnlyList<int> ids, int id ) => Controls.TileGrid.IndexOf( ids, id );
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using BitmapCache = MULExplorer.Services.BitmapCache;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>
///     Land Tiles tab: the 44x44 land art (ids 0..0x3FFF) in a grid, with the tile's tiledata name,
///     flags and texture id, plus its texture and radar color (UOFiddler's LandTilesControl, read-only).
/// </summary>
public sealed partial class LandTilesViewModel : TabViewModel
{
    private IReadOnlyList<int> _validIds = [];
    private BitmapCache? _thumbnails;

    public LandTilesViewModel() : base( "LandTiles", Strings.TabLandTiles )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<int> VisibleIds { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedId { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>UOFiddler's "Show Free Slots": also list ids without art (drawn empty).</summary>
    [ObservableProperty]
    public partial bool ShowFreeSlots { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial Bitmap? TexturePreview { get; private set; }

    [ObservableProperty]
    public partial IBrush? RadarBrush { get; private set; }

    [ObservableProperty]
    public partial string RadarText { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public ObservableCollection<string> SelectedFlags { get; } = [];

    public Func<int, Bitmap?> ThumbnailProvider => id => _thumbnails?.Get( id );

    public override IReadOnlyList<string> Actions => ["set_show_free_slots"];

    protected override async Task LoadAsync( UOData data )
    {
        var ids = await Task.Run( () =>
        {
            var art = data.Art;
            _ = data.TileData;
            var list = new List<int>( Art.LandCount );

            for ( var id = 0; id < Art.LandCount; id++ )
            {
                if ( art.IsValidLand( id ) )
                {
                    list.Add( id );
                }
            }

            return list;
        } );

        _thumbnails = new BitmapCache( id => data.Art.GetLand( id ).ToBitmap() );
        _validIds = ids;
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
        _validIds = [];
        VisibleIds = [];
        Preview = null;
        TexturePreview = null;
        RadarBrush = null;
        Details.Clear();
        SelectedFlags.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnShowFreeSlotsChanged( bool value ) => ApplyFilter();

    partial void OnSelectedIdChanged( int value ) => UpdateSelection();

    private IReadOnlyList<int> AllIds => ShowFreeSlots && _validIds.Count > 0 ? Enumerable.Range( 0, Art.LandCount ).ToList() : _validIds;

    private void ApplyFilter()
    {
        var all = AllIds;
        var text = SearchText.Trim();

        if ( text.Length == 0 || Data is null )
        {
            VisibleIds = all;
        }
        else
        {
            var tiles = Data.TileData;
            var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

            VisibleIds = all
                .Where( id => tiles.GetLand( id ).Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                              id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) )
                .ToList();
        }

        Summary = string.Format( Strings.EntriesShown, VisibleIds.Count, all.Count );
    }

    private void UpdateSelection()
    {
        Details.Clear();
        SelectedFlags.Clear();

        if ( Data is not { } data || ( uint )SelectedId >= Art.LandCount || _validIds.Count == 0 )
        {
            Preview = null;
            TexturePreview = null;
            RadarBrush = null;
            return;
        }

        var land = data.TileData.GetLand( SelectedId );
        var textures = data.TryGet( d => d.Textures );
        var hasTexture = land.TextureId != 0 && textures is not null && textures.IsValid( land.TextureId );

        Preview = data.Art.GetLand( SelectedId ).ToBitmap();
        TexturePreview = hasTexture ? textures!.Get( land.TextureId ).ToBitmap() : null;

        var radar = data.TryGet( d => d.RadarColors );
        RadarBrush = radar is null ? null : new Avalonia.Media.Immutable.ImmutableSolidColorBrush( Color.FromUInt32( radar.GetLandArgb( SelectedId ) ) );

        Details.Add( new PropertyRow( Strings.Id, $"0x{SelectedId:X4} ({SelectedId})" ) );
        Details.Add( new PropertyRow( Strings.Name, land.Name ) );
        Details.Add( new PropertyRow( LandTilesStrings.TextureId, hasTexture
            ? $"0x{land.TextureId:X4} ({land.TextureId})"
            : land.TextureId == 0 ? LandTilesStrings.NoTexture : $"0x{land.TextureId:X4} ({land.TextureId}) - {LandTilesStrings.NoTexture}" ) );

        RadarText = radar is null ? "" : $"0x{radar.GetLandColor( SelectedId ):X4}";

        foreach ( var flag in Enum.GetValues<TileFlag>() )
        {
            if ( flag != TileFlag.None && land.Flags.HasFlag( flag ) )
            {
                SelectedFlags.Add( flag.ToString() );
            }
        }
    }

    public override bool SelectEntry( int id )
    {
        if ( Data is null || ( uint )id >= Art.LandCount || ( !ShowFreeSlots && !Data.Art.IsValidLand( id ) ) )
        {
            return false;
        }

        if ( Controls.TileGrid.IndexOf( VisibleIds, id ) < 0 )
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
        if ( action == "set_show_free_slots" )
        {
            ShowFreeSlots = args["value"]?.GetValue<bool>() ?? true;
            return GetState();
        }

        return base.Invoke( action, args );
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        var land = Data is not null && ( uint )SelectedId < Art.LandCount ? Data.TileData.GetLand( SelectedId ) : default;
        state["total"] = _validIds.Count;
        state["visible"] = VisibleIds.Count;
        state["search"] = SearchText;
        state["showFreeSlots"] = ShowFreeSlots;
        state["selectedId"] = SelectedId;
        state["selectedName"] = land.Name;
        state["textureId"] = land.TextureId;
        state["selectedFlags"] = new JsonArray( SelectedFlags.Select( f => ( JsonNode? )f ).ToArray() );
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        state["hasTexture"] = TexturePreview is not null;
        return state;
    }
}

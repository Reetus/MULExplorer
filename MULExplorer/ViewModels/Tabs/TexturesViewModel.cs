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

/// <summary>
///     Textures tab: texmaps.mul land textures (64x64 or 128x128) in a grid with a full-size preview
///     and the land tiles that reference each texture (UOFiddler's TexturesControl, read-only).
///     The search matches the texture id or the name of a land tile using it.
/// </summary>
public sealed partial class TexturesViewModel : TabViewModel
{
    private const int MaxListedUsers = 12;

    private IReadOnlyList<int> _validIds = [];
    private Dictionary<int, List<int>> _usedBy = [];
    private BitmapCache? _thumbnails;

    public TexturesViewModel() : base( "Textures", Strings.TabTextures )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<int> VisibleIds { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedId { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowFreeSlots { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public Func<int, Bitmap?> ThumbnailProvider => id => _thumbnails?.Get( id );

    public override IReadOnlyList<string> Actions => ["set_show_free_slots"];

    protected override async Task LoadAsync( UOData data )
    {
        var (ids, usedBy) = await Task.Run( () =>
        {
            var textures = data.Textures;
            var list = Enumerable.Range( 0, textures.Count ).Where( textures.IsValid ).ToList();
            var users = new Dictionary<int, List<int>>();

            if ( data.TryGet( d => d.TileData ) is { } tiles )
            {
                foreach ( var land in tiles.LandTable )
                {
                    if ( land.TextureId == 0 )
                    {
                        continue;
                    }

                    if ( !users.TryGetValue( land.TextureId, out var l ) )
                    {
                        users[land.TextureId] = l = [];
                    }

                    l.Add( land.Id );
                }
            }

            return ( list, users );
        } );

        _thumbnails = new BitmapCache( id => data.Textures.Get( id ).ToBitmap(), 512 );
        _validIds = ids;
        _usedBy = usedBy;
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
        _usedBy = [];
        VisibleIds = [];
        Preview = null;
        Details.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnShowFreeSlotsChanged( bool value ) => ApplyFilter();

    partial void OnSelectedIdChanged( int value ) => UpdateSelection();

    private IReadOnlyList<int> AllIds =>
        ShowFreeSlots && Data?.TryGet( d => d.Textures ) is { } t && _validIds.Count > 0 ? Enumerable.Range( 0, t.Count ).ToList() : _validIds;

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
            var tiles = Data.TryGet( d => d.TileData );
            var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

            VisibleIds = all
                .Where( id => id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) ||
                              ( tiles is not null && _usedBy.TryGetValue( id, out var users ) &&
                                users.Exists( l => tiles.GetLand( l ).Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ) ) )
                .ToList();
        }

        Summary = string.Format( Strings.EntriesShown, VisibleIds.Count, all.Count );
    }

    private void UpdateSelection()
    {
        Details.Clear();

        if ( Data is not { } data || SelectedId < 0 || _validIds.Count == 0 )
        {
            Preview = null;
            return;
        }

        var textures = data.Textures;
        var size = textures.GetSize( SelectedId );
        Preview = textures.Get( SelectedId ).ToBitmap();

        Details.Add( new PropertyRow( Strings.Id, $"0x{SelectedId:X4} ({SelectedId})" ) );
        Details.Add( new PropertyRow( Strings.Size, size == 0 ? "-" : $"{size} × {size}" ) );

        if ( textures.GetAlias( SelectedId ) is { } alias )
        {
            Details.Add( new PropertyRow( TexturesStrings.Alias, string.Format( CultureInfo.CurrentCulture, TexturesStrings.AliasValue, alias ) ) );
        }

        var users = _usedBy.GetValueOrDefault( SelectedId ) ?? [];
        var tiles = data.TryGet( d => d.TileData );
        var lines = users.Take( MaxListedUsers ).Select( l => $"0x{l:X4} {tiles?.GetLand( l ).Name}".TrimEnd() ).ToList();

        if ( users.Count > MaxListedUsers )
        {
            lines.Add( string.Format( CultureInfo.CurrentCulture, TexturesStrings.More, users.Count - MaxListedUsers ) );
        }

        Details.Add( new PropertyRow( TexturesStrings.UsedBy, lines.Count == 0 ? TexturesStrings.NotUsed : string.Join( Environment.NewLine, lines ) ) );
    }

    public override bool SelectEntry( int id )
    {
        if ( Data?.TryGet( d => d.Textures ) is not { } textures || ( uint )id >= ( uint )textures.Count || ( !ShowFreeSlots && !textures.IsValid( id ) ) )
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
        state["total"] = _validIds.Count;
        state["visible"] = VisibleIds.Count;
        state["search"] = SearchText;
        state["showFreeSlots"] = ShowFreeSlots;
        state["selectedId"] = SelectedId;
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        state["usedByLand"] = new JsonArray( ( _usedBy.GetValueOrDefault( SelectedId ) ?? [] ).Take( 50 ).Select( l => ( JsonNode? )l ).ToArray() );
        return state;
    }
}

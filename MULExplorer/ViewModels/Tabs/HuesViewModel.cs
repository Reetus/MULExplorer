using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Mcp.Tools;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>One hue row: number, name and its ramp as a 32x1 bitmap (stretched by the view).</summary>
public sealed class HueRow( Hue hue, bool gameNumbers )
{
    public Hue Hue { get; } = hue;

    /// <summary>In-game hue number (file index + 1); the id used by automation.</summary>
    public int Number => Hue.Index + 1;

    public string NumberText
    {
        get
        {
            var n = gameNumbers ? Number : Hue.Index;
            return $"{n} (0x{n:X})";
        }
    }

    public string Name => Hue.Name;

    public Bitmap? Ramp => field ??= HuesTools.RampImage( Hue, 1, 1 ).ToBitmap();
}

/// <summary>One of the 32 colors of the selected hue.</summary>
public sealed record HueColorRow( int Index, string Color16, string Rgb, IBrush Brush );

/// <summary>
///     Hues tab, after UOFiddler's HuesControl: every hue as a color-ramp row (search by name or
///     number), the selected hue's colors and table range, and an item and a gump drawn with it.
/// </summary>
public sealed partial class HuesViewModel : TabViewModel
{
    private List<HueRow> _all = [];

    public HuesViewModel() : base( "Hues", Strings.TabHues )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<HueRow> VisibleRows { get; private set; } = [];

    [ObservableProperty]
    public partial HueRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>UOFiddler's "Show ingame numbers": list numbers are 1-based instead of file indexes.</summary>
    [ObservableProperty]
    public partial bool ShowGameNumbers { get; set; }

    /// <summary>Item drawn with the selected hue (UOFiddler's hue editor previews art the same way).</summary>
    [ObservableProperty]
    public partial int PreviewItemId { get; set; } = 0x1F03;

    [ObservableProperty]
    public partial int PreviewGumpId { get; set; } = 0xC72B;

    [ObservableProperty]
    public partial bool OnlyGray { get; set; }

    [ObservableProperty]
    public partial Bitmap? ItemPreview { get; private set; }

    [ObservableProperty]
    public partial Bitmap? GumpPreview { get; private set; }

    [ObservableProperty]
    public partial Bitmap? SelectedRamp { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public ObservableCollection<HueColorRow> Colors { get; } = [];

    public int SelectedHue => SelectedRow?.Number ?? 0;

    public override IReadOnlyList<string> Actions => ["set_preview_item", "set_preview_gump", "set_only_gray", "show_game_numbers"];

    protected override async Task LoadAsync( UOData data )
    {
        var hues = await Task.Run( () => data.Hues.List );

        // Default gump: the preview item's male paperdoll gump (50000 + animation), as worn in game.
        if ( data.TryGet( d => d.TileData ) is { } tiles && ( uint )PreviewItemId < ( uint )tiles.ItemTable.Length &&
             data.TryGet( d => d.Gumps ) is { } gumps && gumps.IsValid( 50000 + tiles.GetItem( PreviewItemId ).Animation ) )
        {
            PreviewGumpId = 50000 + tiles.GetItem( PreviewItemId ).Animation;
        }

        BuildRows( hues );
        SelectedRow ??= VisibleRows.FirstOrDefault();
        UpdateSelection();
    }

    protected override void Reset()
    {
        _all = [];
        VisibleRows = [];
        SelectedRow = null;
        Details.Clear();
        Colors.Clear();
        ItemPreview = null;
        GumpPreview = null;
        SelectedRamp = null;
        Summary = "";
    }

    private void BuildRows( IReadOnlyList<Hue> hues )
    {
        var selected = SelectedRow?.Number;
        _all = hues.Select( h => new HueRow( h, ShowGameNumbers ) ).ToList();
        ApplyFilter();
        SelectedRow = selected is { } n ? VisibleRows.FirstOrDefault( r => r.Number == n ) : null;
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnShowGameNumbersChanged( bool value )
    {
        if ( Data is { } data && IsLoaded )
        {
            BuildRows( data.Hues.List );
        }
    }

    partial void OnSelectedRowChanged( HueRow? value ) => UpdateSelection();

    partial void OnPreviewItemIdChanged( int value ) => UpdatePreviews();

    partial void OnPreviewGumpIdChanged( int value ) => UpdatePreviews();

    partial void OnOnlyGrayChanged( bool value ) => UpdatePreviews();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var selected = SelectedRow;

        if ( text.Length == 0 )
        {
            VisibleRows = _all;
        }
        else
        {
            var number = ParseNumber( text );
            // Numbers typed in the box follow the list's numbering (file index or in-game number).
            var target = number is { } n ? ShowGameNumbers ? n : n + 1 : -1;
            VisibleRows = _all.Where( r => r.Number == target || r.Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();
        }

        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleRows.Count, _all.Count );

        // The list may drop its selection when its source is replaced; restore it.
        SelectedRow = selected is not null && VisibleRows.Contains( selected ) ? selected : VisibleRows.FirstOrDefault();
    }

    private void UpdateSelection()
    {
        Details.Clear();
        Colors.Clear();
        OnPropertyChanged( nameof( SelectedHue ) );

        if ( SelectedRow is not { } row )
        {
            SelectedRamp = null;
            UpdatePreviews();
            return;
        }

        var hue = row.Hue;
        SelectedRamp = row.Ramp;
        Details.Add( new PropertyRow( HuesStrings.GameNumber, $"{row.Number} (0x{row.Number:X})" ) );
        Details.Add( new PropertyRow( HuesStrings.FileIndex, $"{hue.Index} (0x{hue.Index:X})" ) );
        Details.Add( new PropertyRow( Strings.Name, hue.Name ) );
        Details.Add( new PropertyRow( HuesStrings.TableStart, $"0x{hue.TableStart:X4} ({hue.TableStart})" ) );
        Details.Add( new PropertyRow( HuesStrings.TableEnd, $"0x{hue.TableEnd:X4} ({hue.TableEnd})" ) );

        for ( var i = 0; i < 32; i++ )
        {
            var argb = hue.ArgbAt( i );
            Colors.Add( new HueColorRow( i, $"0x{hue.Colors[i]:X4}", $"#{argb & 0xFFFFFF:X6}", new SolidColorBrush( Color.FromUInt32( argb ) ) ) );
        }

        UpdatePreviews();
    }

    private void UpdatePreviews()
    {
        if ( Data is not { } data || !IsLoadedOrLoading )
        {
            ItemPreview = GumpPreview = null;
            return;
        }

        var hue = SelectedHue;
        ItemPreview = Hued( data.TryGet( d => d.Art )?.GetItem( PreviewItemId ), data, hue, OnlyGray || IsPartialHueItem( data, PreviewItemId ) );
        GumpPreview = Hued( data.TryGet( d => d.Gumps )?.Get( PreviewGumpId ), data, hue, OnlyGray );
    }

    private static bool IsPartialHueItem( UOData data, int id ) =>
        data.TryGet( d => d.TileData ) is { } tiles && ( uint )id < ( uint )tiles.ItemTable.Length && tiles.GetItem( id ).Flags.HasFlag( TileFlag.PartialHue );

    private static Bitmap? Hued( Pixmap? pixmap, UOData data, int hue, bool onlyGray ) =>
        ( pixmap is not null && hue > 0 ? data.Hues.Apply( pixmap, hue, onlyGray ) : pixmap ).ToBitmap();

    private bool IsLoadedOrLoading => _all.Count > 0;

    private static int? ParseNumber( string text ) => Mcp.ToolArgs.ParseInt( text );

    /// <summary>Selects by in-game hue number (1-based).</summary>
    public override bool SelectEntry( int id )
    {
        if ( _all.Find( r => r.Number == id ) is not { } row )
        {
            return false;
        }

        if ( !VisibleRows.Contains( row ) )
        {
            SearchText = "";
        }

        SelectedRow = row;
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
            case "set_preview_item":
                PreviewItemId = Mcp.ToolArgs.ParseInt( args["id"]?.ToString() ) ?? PreviewItemId;
                return GetState();
            case "set_preview_gump":
                PreviewGumpId = Mcp.ToolArgs.ParseInt( args["id"]?.ToString() ) ?? PreviewGumpId;
                return GetState();
            case "set_only_gray":
                OnlyGray = args["value"]?.GetValue<bool>() ?? true;
                return GetState();
            case "show_game_numbers":
                ShowGameNumbers = args["value"]?.GetValue<bool>() ?? true;
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleRows.Count;
        state["search"] = SearchText;
        state["selectedHue"] = SelectedHue;
        state["selectedName"] = SelectedRow?.Name;
        state["showGameNumbers"] = ShowGameNumbers;
        state["previewItem"] = PreviewItemId;
        state["previewGump"] = PreviewGumpId;
        state["onlyGray"] = OnlyGray;
        state["itemPreviewSize"] = ItemPreview is { } i ? $"{i.PixelSize.Width}x{i.PixelSize.Height}" : null;
        state["gumpPreviewSize"] = GumpPreview is { } g ? $"{g.PixelSize.Width}x{g.PixelSize.Height}" : null;
        state["colors"] = new JsonArray( Colors.Select( c => ( JsonNode? )c.Rgb ).ToArray() );
        return state;
    }
}

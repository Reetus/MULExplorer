using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MULExplorer.Controls;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A facet in the selector.</summary>
public sealed record FacetOption( int Index, string Name );

/// <summary>One static in the tile-info list.</summary>
public sealed record MapStaticRow( string Id, string Name, string Z, string Hue );

/// <summary>
///     Map tab: UOFiddler's MapControl, read-only. Radar view of a facet (land plus the highest
///     static per cell), pan/zoom, cursor coordinates, tile info on click, go-to, statics and
///     diff-patch toggles.
/// </summary>
public sealed partial class MapViewModel : TabViewModel
{
    public const double MinZoom = 0.125;
    public const double MaxZoom = 8;

    private Maps? _maps;

    public MapViewModel() : base( "Map", Strings.TabMap )
    {
    }

    public ObservableCollection<FacetOption> Facets { get; } = [];

    [ObservableProperty]
    public partial FacetOption? SelectedFacet { get; set; }

    [ObservableProperty]
    public partial bool ShowStatics { get; set; } = true;

    [ObservableProperty]
    public partial bool UsePatches { get; set; }

    [ObservableProperty]
    public partial bool UseAlternate { get; set; }

    [ObservableProperty]
    public partial bool ShowCenterCross { get; set; }

    [ObservableProperty]
    public partial bool HasPatches { get; private set; }

    [ObservableProperty]
    public partial bool HasAlternate { get; private set; }

    [ObservableProperty]
    public partial IMapImageSource? Source { get; private set; }

    [ObservableProperty]
    public partial double CenterX { get; set; }

    [ObservableProperty]
    public partial double CenterY { get; set; }

    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    [ObservableProperty]
    public partial PixelPoint? HoverTile { get; set; }

    [ObservableProperty]
    public partial PixelPoint? InspectedTile { get; set; }

    [ObservableProperty]
    public partial string GoToText { get; set; } = "";

    [ObservableProperty]
    public partial string CoordsText { get; private set; } = MapStrings.CoordsNone;

    [ObservableProperty]
    public partial string ZoomText { get; private set; } = "";

    [ObservableProperty]
    public partial string FacetText { get; private set; } = "";

    [ObservableProperty]
    public partial string PatchText { get; private set; } = "";

    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    [ObservableProperty]
    public partial string InspectedText { get; private set; } = MapStrings.ClickHint;

    public ObservableCollection<PropertyRow> LandDetails { get; } = [];

    public ObservableCollection<MapStaticRow> Statics { get; } = [];

    public FacetMap? Facet => ( Source as FacetImageSource )?.Facet;

    public override IReadOnlyList<string> Actions => ["set_facet", "go_to", "set_zoom", "zoom_in", "zoom_out", "toggle_statics", "toggle_patches", "toggle_alternate", "inspect"];

    protected override async Task LoadAsync( UOData data )
    {
        var (maps, available) = await Task.Run( () =>
        {
            var m = data.Maps;
            var list = Enumerable.Range( 0, Maps.FacetCount ).Where( i => m.IsAvailable( i ) ).ToList();

            // Open the first facet and the radar tables now so the first paint does not stall.
            if ( list.Count > 0 )
            {
                m.GetFacet( list[0] )?.GetRadarColor( 0, 0 );
            }

            return ( m, list );
        } );

        _maps = maps;
        Facets.Clear();

        foreach ( var index in available )
        {
            Facets.Add( new FacetOption( index, FacetName( index ) ) );
        }

        var initial = Facets.FirstOrDefault( f => f.Index == ( SelectedFacet?.Index ?? 0 ) ) ?? Facets.FirstOrDefault();
        var firstLoad = SelectedFacet is null;
        SelectedFacet = initial;
        UpdateSource();

        if ( firstLoad && initial?.Index is 0 or 1 )
        {
            // Britain, like the client's default view, rather than open sea at 0,0.
            CenterX = 1440;
            CenterY = 1690;
        }
        else if ( Facet is { } facet )
        {
            CenterX = facet.Width / 2.0;
            CenterY = facet.Height / 2.0;
        }

        UpdateZoomText();
    }

    protected override void Reset()
    {
        _maps = null;
        Source = null;
        Facets.Clear();
        SelectedFacet = null;
        InspectedTile = null;
        HoverTile = null;
        LandDetails.Clear();
        Statics.Clear();
        FacetText = "";
        PatchText = "";
        Message = "";
    }

    public static string FacetName( int index ) => index switch
    {
        0 => MapStrings.FacetFelucca,
        1 => MapStrings.FacetTrammel,
        2 => MapStrings.FacetIlshenar,
        3 => MapStrings.FacetMalas,
        4 => MapStrings.FacetTokuno,
        5 => MapStrings.FacetTerMur,
        _ => Maps.GetName( index )
    };

    partial void OnSelectedFacetChanged( FacetOption? value ) => UpdateSource();

    partial void OnShowStaticsChanged( bool value ) => UpdateSource();

    partial void OnUsePatchesChanged( bool value ) => UpdateSource();

    partial void OnUseAlternateChanged( bool value ) => UpdateSource();

    partial void OnZoomChanged( double value ) => UpdateZoomText();

    partial void OnHoverTileChanged( PixelPoint? value ) =>
        CoordsText = value is { } p ? string.Format( CultureInfo.CurrentCulture, MapStrings.Coords, p.X, p.Y ) : MapStrings.CoordsNone;

    partial void OnInspectedTileChanged( PixelPoint? value ) => UpdateInspection();

    private void UpdateSource()
    {
        if ( _maps is null || SelectedFacet is not { } option )
        {
            Source = null;
            return;
        }

        HasAlternate = _maps.HasAlternate( option.Index );
        var facet = _maps.GetFacet( option.Index, UseAlternate && HasAlternate );

        if ( facet is null )
        {
            Source = null;
            return;
        }

        HasPatches = facet.HasPatches;
        Source = new FacetImageSource( facet, ShowStatics, UsePatches && HasPatches );
        CenterX = Math.Clamp( CenterX, 0, facet.Width );
        CenterY = Math.Clamp( CenterY, 0, facet.Height );

        FacetText = string.Format( CultureInfo.CurrentCulture, MapStrings.FacetInfo, option.Name, facet.Width, facet.Height, Path.GetFileName( facet.MapPath ) );
        PatchText = facet.HasPatches ? string.Format( CultureInfo.CurrentCulture, MapStrings.PatchInfo, facet.LandPatchCount, facet.StaticPatchCount ) : MapStrings.NoPatches;

        if ( InspectedTile is { } tile && !facet.Contains( tile.X, tile.Y ) )
        {
            InspectedTile = null;
        }
        else
        {
            UpdateInspection();
        }
    }

    private void UpdateZoomText() => ZoomText = string.Format( CultureInfo.CurrentCulture, MapStrings.ZoomLabel, Zoom.ToString( "0.###", CultureInfo.CurrentCulture ) );

    private void UpdateInspection()
    {
        LandDetails.Clear();
        Statics.Clear();

        if ( Data is not { } data || Facet is not { } facet || InspectedTile is not { } tile || !facet.Contains( tile.X, tile.Y ) )
        {
            InspectedText = MapStrings.ClickHint;
            return;
        }

        var patched = UsePatches && facet.HasPatches;
        var land = facet.GetLand( tile.X, tile.Y, patched );
        var tiles = data.TileData;
        var landPatched = patched && facet.IsLandBlockPatched( tile.X >> 3, tile.Y >> 3 );
        var staticsPatched = patched && facet.IsStaticBlockPatched( tile.X >> 3, tile.Y >> 3 );

        InspectedText = string.Format( CultureInfo.CurrentCulture, MapStrings.Coords, tile.X, tile.Y );
        LandDetails.Add( new PropertyRow( MapStrings.Land, $"0x{land.Id:X4} {tiles.GetLand( land.Id ).Name}" + ( landPatched ? $" ({MapStrings.Patched})" : "" ) ) );
        LandDetails.Add( new PropertyRow( MapStrings.Altitude, land.Z.ToString( CultureInfo.CurrentCulture ) ) );

        if ( staticsPatched )
        {
            LandDetails.Add( new PropertyRow( MapStrings.Statics, MapStrings.Patched ) );
        }

        foreach ( var s in facet.GetStatics( tile.X, tile.Y, patched ) )
        {
            Statics.Add( new MapStaticRow( $"0x{s.Id:X4}", tiles.GetItem( s.Id ).Name, s.Z.ToString( CultureInfo.CurrentCulture ), s.Hue.ToString( CultureInfo.CurrentCulture ) ) );
        }
    }

    [RelayCommand]
    private void ZoomIn() => SetZoom( Zoom * 2 );

    [RelayCommand]
    private void ZoomOut() => SetZoom( Zoom / 2 );

    [RelayCommand]
    private void GoTo() => TryGoTo( GoToText );

    private void SetZoom( double zoom ) => Zoom = Math.Clamp( zoom, MinZoom, MaxZoom );

    /// <summary>Centres on "x, y" (also "x y" or "x,y,z"); false if unparsable or outside the facet.</summary>
    public bool TryGoTo( string text )
    {
        var parts = text.Split( [',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

        if ( Facet is { } facet && parts.Length >= 2 &&
             int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x ) &&
             int.TryParse( parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y ) &&
             facet.Contains( x, y ) )
        {
            GoTo( x, y );
            Message = "";
            return true;
        }

        Message = string.Format( CultureInfo.CurrentCulture, MapStrings.GoToInvalid, Facet?.Width ?? 0, Facet?.Height ?? 0 );
        return false;
    }

    /// <summary>Centres the view on a cell and inspects it.</summary>
    public void GoTo( int x, int y )
    {
        CenterX = x + 0.5;
        CenterY = y + 0.5;
        InspectedTile = new PixelPoint( x, y );
    }

    public override bool SelectEntry( int id )
    {
        if ( Facets.FirstOrDefault( f => f.Index == id ) is not { } facet )
        {
            return false;
        }

        SelectedFacet = facet;
        return true;
    }

    /// <summary>The map has no list to filter: the search box is a go-to. Returns 1 on success, 0 otherwise.</summary>
    public override int Search( string text )
    {
        GoToText = text;
        return TryGoTo( text ) ? 1 : 0;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_facet":
                if ( !SelectEntry( RequireInt( args, "facet" ) ) )
                {
                    throw new ArgumentException( $"Facet {args["facet"]} is not available. Available: {string.Join( ", ", Facets.Select( f => f.Index ) )}" );
                }

                break;
            case "go_to":
            case "inspect":
                var x = RequireInt( args, "x" );
                var y = RequireInt( args, "y" );

                if ( Facet is not { } facet || !facet.Contains( x, y ) )
                {
                    throw new ArgumentException( $"({x}, {y}) is outside the facet." );
                }

                if ( action == "go_to" )
                {
                    GoTo( x, y );
                }
                else
                {
                    InspectedTile = new PixelPoint( x, y );
                }

                break;
            case "set_zoom":
                SetZoom( args["zoom"]?.GetValue<double>() ?? 1 );
                break;
            case "zoom_in":
                ZoomIn();
                break;
            case "zoom_out":
                ZoomOut();
                break;
            case "toggle_statics":
                ShowStatics = args["value"]?.GetValue<bool>() ?? !ShowStatics;
                break;
            case "toggle_patches":
                UsePatches = args["value"]?.GetValue<bool>() ?? !UsePatches;
                break;
            case "toggle_alternate":
                UseAlternate = args["value"]?.GetValue<bool>() ?? !UseAlternate;
                break;
            default:
                return base.Invoke( action, args );
        }

        return GetState();
    }

    private static int RequireInt( JsonObject args, string name ) =>
        args[name] is { } node ? node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? Mcp.ToolArgs.ParseInt( node.GetValue<string>() ) ?? throw new ArgumentException( $"Invalid '{name}'." )
            : node.GetValue<int>()
            : throw new ArgumentException( $"Missing '{name}'." );

    public override JsonObject GetState()
    {
        var state = base.GetState();
        var facet = Facet;
        state["facet"] = SelectedFacet?.Index;
        state["facetName"] = SelectedFacet?.Name;
        state["facets"] = new JsonArray( Facets.Select( f => ( JsonNode? )new JsonObject { ["index"] = f.Index, ["name"] = f.Name } ).ToArray() );
        state["width"] = facet?.Width;
        state["height"] = facet?.Height;
        state["centerX"] = Math.Round( CenterX, 2 );
        state["centerY"] = Math.Round( CenterY, 2 );
        state["zoom"] = Zoom;
        state["showStatics"] = ShowStatics;
        state["usePatches"] = UsePatches;
        state["hasPatches"] = HasPatches;
        state["useAlternate"] = UseAlternate;
        state["hasAlternate"] = HasAlternate;
        state["cursor"] = HoverTile is { } h ? new JsonObject { ["x"] = h.X, ["y"] = h.Y } : null;
        state["message"] = Message.Length > 0 ? Message : null;

        if ( InspectedTile is { } t && facet is not null )
        {
            var patched = UsePatches && facet.HasPatches;
            var land = facet.GetLand( t.X, t.Y, patched );
            state["inspected"] = new JsonObject
            {
                ["x"] = t.X,
                ["y"] = t.Y,
                ["landId"] = land.Id,
                ["landName"] = Data?.TileData.GetLand( land.Id ).Name,
                ["z"] = land.Z,
                ["statics"] = new JsonArray( Statics.Select( s => ( JsonNode? )new JsonObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name,
                    ["z"] = s.Z,
                    ["hue"] = s.Hue
                } ).ToArray() )
            };
        }
        else
        {
            state["inspected"] = null;
        }

        return state;
    }
}

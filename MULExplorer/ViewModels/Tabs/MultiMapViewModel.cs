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

/// <summary>An image choice: <see cref="Id" /> -1 is Multimap.rle, otherwise facet0N.mul.</summary>
public sealed record MultiMapOption( int Id, string Name );

/// <summary>
///     MultiMap/Facets tab: UOFiddler's MultiMapControl, read-only. Shows Multimap.rle or one of
///     the facet0N.mul images, zoomable and scrollable, with the image size.
/// </summary>
public sealed partial class MultiMapViewModel : TabViewModel
{
    public const int MultiMapId = -1;
    public const double MinZoom = 0.0625;
    public const double MaxZoom = 8;

    private readonly Dictionary<int, MultiMapImage> _images = new();
    private MultiMap? _multiMap;

    public MultiMapViewModel() : base( "MultiMap", Strings.TabMultiMap )
    {
    }

    public ObservableCollection<MultiMapOption> Images { get; } = [];

    [ObservableProperty]
    public partial MultiMapOption? SelectedImage { get; set; }

    [ObservableProperty]
    public partial IMapImageSource? Source { get; private set; }

    [ObservableProperty]
    public partial double CenterX { get; set; }

    [ObservableProperty]
    public partial double CenterY { get; set; }

    [ObservableProperty]
    public partial double Zoom { get; set; } = 0.5;

    [ObservableProperty]
    public partial PixelPoint? HoverPixel { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; private set; } = "";

    [ObservableProperty]
    public partial string CoordsText { get; private set; } = MultiMapStrings.CoordsNone;

    [ObservableProperty]
    public partial string ZoomText { get; private set; } = "";

    /// <summary>Drawing-area size, set by the view so "fit" can pick a zoom.</summary>
    public Size ViewportSize { get; set; }

    public int ImageWidth => ( Source as MultiMapImageSource )?.Width ?? 0;

    public int ImageHeight => ( Source as MultiMapImageSource )?.Height ?? 0;

    public override IReadOnlyList<string> Actions => ["set_image", "set_zoom", "zoom_in", "zoom_out", "zoom_fit", "center"];

    protected override async Task LoadAsync( UOData data )
    {
        var multiMap = await Task.Run( () => data.MultiMap );
        _multiMap = multiMap;
        _images.Clear();
        Images.Clear();

        if ( multiMap.HasMultiMap )
        {
            Images.Add( new MultiMapOption( MultiMapId, MultiMapStrings.MultiMapRle ) );
        }

        foreach ( var id in multiMap.FacetIds )
        {
            Images.Add( new MultiMapOption( id, string.Format( CultureInfo.CurrentCulture, MultiMapStrings.FacetImage, id ) ) );
        }

        var choice = Images.FirstOrDefault( i => i.Id == SelectedImage?.Id ) ?? Images.FirstOrDefault();

        if ( choice is not null )
        {
            // Decode off the UI thread; facet00 is a 7168x4096 image.
            await Task.Run( () => GetImage( choice.Id ) );
        }

        SelectedImage = null;
        SelectedImage = choice;
        UpdateZoomText();
    }

    protected override void Reset()
    {
        _multiMap = null;
        _images.Clear();
        Images.Clear();
        SelectedImage = null;
        Source = null;
        SizeText = "";
    }

    private MultiMapImage? GetImage( int id )
    {
        lock ( _images )
        {
            if ( _images.TryGetValue( id, out var cached ) )
            {
                return cached;
            }
        }

        var image = id == MultiMapId ? _multiMap?.GetMultiMap() : _multiMap?.GetFacetImage( id );

        if ( image is not null )
        {
            lock ( _images )
            {
                _images[id] = image;
            }
        }

        return image;
    }

    partial void OnSelectedImageChanged( MultiMapOption? value )
    {
        if ( value is null || GetImage( value.Id ) is not { } image )
        {
            Source = null;
            SizeText = "";
            return;
        }

        lock ( _images )
        {
            // Facet images are up to 56 MB decoded; keep only the one on screen.
            foreach ( var key in _images.Keys.Where( k => k != value.Id ).ToList() )
            {
                _images.Remove( key );
            }
        }

        Source = new MultiMapImageSource( image );
        SizeText = string.Format( CultureInfo.CurrentCulture, MultiMapStrings.ImageSize, image.Width, image.Height );
        CenterX = image.Width / 2.0;
        CenterY = image.Height / 2.0;
        ZoomFit();
    }

    partial void OnZoomChanged( double value ) => UpdateZoomText();

    partial void OnHoverPixelChanged( PixelPoint? value ) =>
        CoordsText = value is { } p ? string.Format( CultureInfo.CurrentCulture, MultiMapStrings.Coords, p.X, p.Y ) : MultiMapStrings.CoordsNone;

    private void UpdateZoomText() =>
        ZoomText = string.Format( CultureInfo.CurrentCulture, MultiMapStrings.ZoomLabel, Zoom.ToString( "0.####", CultureInfo.CurrentCulture ) );

    private void SetZoom( double zoom ) => Zoom = Math.Clamp( zoom, MinZoom, MaxZoom );

    [RelayCommand]
    private void ZoomIn() => SetZoom( Zoom * 2 );

    [RelayCommand]
    private void ZoomOut() => SetZoom( Zoom / 2 );

    /// <summary>Largest power-of-two zoom at which the whole image fits the view.</summary>
    [RelayCommand]
    private void ZoomFit()
    {
        if ( ImageWidth == 0 )
        {
            return;
        }

        var view = ViewportSize.Width > 0 ? ViewportSize : new Size( 1000, 700 );
        var fit = Math.Min( view.Width / ImageWidth, view.Height / ImageHeight );
        var zoom = MinZoom;

        while ( zoom * 2 <= fit && zoom * 2 <= MaxZoom )
        {
            zoom *= 2;
        }

        Zoom = zoom;
        CenterX = ImageWidth / 2.0;
        CenterY = ImageHeight / 2.0;
    }

    /// <summary>Selects an image: -1 = Multimap.rle, N = facet0N.mul.</summary>
    public override bool SelectEntry( int id )
    {
        if ( Images.FirstOrDefault( i => i.Id == id ) is not { } option )
        {
            return false;
        }

        SelectedImage = option;
        return true;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_image":
                var id = args["id"]?.GetValue<int>() ?? throw new ArgumentException( "Missing 'id' (-1 = Multimap.rle, N = facet0N.mul)." );

                if ( !SelectEntry( id ) )
                {
                    throw new ArgumentException( $"Image {id} is not available. Available: {string.Join( ", ", Images.Select( i => i.Id ) )}" );
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
            case "zoom_fit":
                ZoomFit();
                break;
            case "center":
                CenterX = args["x"]?.GetValue<double>() ?? CenterX;
                CenterY = args["y"]?.GetValue<double>() ?? CenterY;
                break;
            default:
                return base.Invoke( action, args );
        }

        return GetState();
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["images"] = new JsonArray( Images.Select( i => ( JsonNode? )new JsonObject { ["id"] = i.Id, ["name"] = i.Name } ).ToArray() );
        state["selectedImage"] = SelectedImage?.Id;
        state["selectedName"] = SelectedImage?.Name;
        state["width"] = ImageWidth;
        state["height"] = ImageHeight;
        state["zoom"] = Zoom;
        state["centerX"] = Math.Round( CenterX, 2 );
        state["centerY"] = Math.Round( CenterY, 2 );
        state["cursor"] = HoverPixel is { } p ? new JsonObject { ["x"] = p.X, ["y"] = p.Y } : null;
        return state;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MULExplorer.Services;
using UODataAccess.Imaging;
using UODataAccess.Loaders;

namespace MULExplorer.Controls;

/// <summary>A large image that can render any window of itself, possibly downsampled.</summary>
public interface IMapImageSource
{
    int Width { get; }

    int Height { get; }

    /// <summary>True if rendering a tile at this step is slow enough to do off the UI thread.</summary>
    bool IsSlow( int step );

    /// <summary>Pixel (px, py) of <paramref name="target" /> shows source pixel <c>(x + px * step, y + py * step)</c>.</summary>
    void Render( Pixmap target, int x, int y, int step );
}

/// <summary>Radar view of a facet (land + optionally the highest static).</summary>
public sealed class FacetImageSource( FacetMap facet, bool statics, bool patched ) : IMapImageSource
{
    public FacetMap Facet { get; } = facet;

    public int Width => Facet.Width;

    public int Height => Facet.Height;

    // A 256-pixel tile at step 2 is 64x64 blocks; coarser tiles touch too many blocks to render inline on a cold cache.
    public bool IsSlow( int step ) => step > 2;

    public void Render( Pixmap target, int x, int y, int step ) => Facet.RenderRadar( target, x, y, step, statics, patched );
}

/// <summary>A decoded MultiMap.rle / facet image.</summary>
public sealed class MultiMapImageSource( MultiMapImage image ) : IMapImageSource
{
    public int Width => image.Width;

    public int Height => image.Height;

    public bool IsSlow( int step ) => false;

    public void Render( Pixmap target, int x, int y, int step ) => image.Render( target, x, y, step );
}

/// <summary>
///     Pan/zoom viewer for huge images (a 7168x4096 facet) that only ever draws the visible
///     window. The source is rendered in 256x256 tiles per zoom level and cached (LRU); slow
///     tiles render on the thread pool while a coarser cached tile stands in. Drag or scroll to
///     pan, wheel to zoom around the cursor, click to select a cell.
/// </summary>
public sealed class MapCanvas : Grid
{
    public const int TileSize = 256;

    public static readonly StyledProperty<IMapImageSource?> SourceProperty =
        AvaloniaProperty.Register<MapCanvas, IMapImageSource?>( nameof( Source ) );

    /// <summary>Source pixel at the centre of the view.</summary>
    public static readonly StyledProperty<double> CenterXProperty =
        AvaloniaProperty.Register<MapCanvas, double>( nameof( CenterX ), defaultBindingMode: BindingMode.TwoWay );

    public static readonly StyledProperty<double> CenterYProperty =
        AvaloniaProperty.Register<MapCanvas, double>( nameof( CenterY ), defaultBindingMode: BindingMode.TwoWay );

    /// <summary>Screen pixels per source pixel; powers of two between <see cref="MinZoom" /> and <see cref="MaxZoom" />.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MapCanvas, double>( nameof( Zoom ), 1, defaultBindingMode: BindingMode.TwoWay );

    public static readonly StyledProperty<double> MinZoomProperty =
        AvaloniaProperty.Register<MapCanvas, double>( nameof( MinZoom ), 0.125 );

    public static readonly StyledProperty<double> MaxZoomProperty =
        AvaloniaProperty.Register<MapCanvas, double>( nameof( MaxZoom ), 8 );

    /// <summary>Source pixel under the pointer, or null when the pointer is outside the image.</summary>
    public static readonly StyledProperty<PixelPoint?> HoverPixelProperty =
        AvaloniaProperty.Register<MapCanvas, PixelPoint?>( nameof( HoverPixel ), defaultBindingMode: BindingMode.TwoWay );

    /// <summary>Last clicked source pixel (outlined).</summary>
    public static readonly StyledProperty<PixelPoint?> SelectedPixelProperty =
        AvaloniaProperty.Register<MapCanvas, PixelPoint?>( nameof( SelectedPixel ), defaultBindingMode: BindingMode.TwoWay );

    public static readonly StyledProperty<bool> ShowCenterCrossProperty =
        AvaloniaProperty.Register<MapCanvas, bool>( nameof( ShowCenterCross ) );

    private const int CacheCapacity = 160;

    private readonly Dictionary<(int Step, int X, int Y), LinkedListNode<((int Step, int X, int Y) Key, Bitmap Bitmap)>> _tiles = new();
    private readonly LinkedList<((int Step, int X, int Y) Key, Bitmap Bitmap)> _lru = new();
    private readonly HashSet<(int Step, int X, int Y)> _pending = [];
    private readonly MapView _view;
    private readonly ScrollBar _hScroll;
    private readonly ScrollBar _vScroll;
    private TaskCompletionSource? _idle;
    private int _version;
    private bool _syncingScroll;

    public MapCanvas()
    {
        ColumnDefinitions = new ColumnDefinitions( "*,Auto" );
        RowDefinitions = new RowDefinitions( "*,Auto" );
        ClipToBounds = true;

        _view = new MapView( this );
        _hScroll = new ScrollBar { Orientation = Orientation.Horizontal, AllowAutoHide = false, SmallChange = 32 };
        _vScroll = new ScrollBar { Orientation = Orientation.Vertical, AllowAutoHide = false, SmallChange = 32 };
        SetRow( _hScroll, 1 );
        SetColumn( _vScroll, 1 );
        _hScroll.Scroll += ( _, _ ) => OnScrolled();
        _vScroll.Scroll += ( _, _ ) => OnScrolled();
        _view.SizeChanged += ( _, _ ) => UpdateScrollBars();
        Children.Add( _view );
        Children.Add( _hScroll );
        Children.Add( _vScroll );
    }

    public IMapImageSource? Source
    {
        get => GetValue( SourceProperty );
        set => SetValue( SourceProperty, value );
    }

    public double CenterX
    {
        get => GetValue( CenterXProperty );
        set => SetValue( CenterXProperty, value );
    }

    public double CenterY
    {
        get => GetValue( CenterYProperty );
        set => SetValue( CenterYProperty, value );
    }

    public double Zoom
    {
        get => GetValue( ZoomProperty );
        set => SetValue( ZoomProperty, value );
    }

    public double MinZoom
    {
        get => GetValue( MinZoomProperty );
        set => SetValue( MinZoomProperty, value );
    }

    public double MaxZoom
    {
        get => GetValue( MaxZoomProperty );
        set => SetValue( MaxZoomProperty, value );
    }

    public PixelPoint? HoverPixel
    {
        get => GetValue( HoverPixelProperty );
        set => SetValue( HoverPixelProperty, value );
    }

    public PixelPoint? SelectedPixel
    {
        get => GetValue( SelectedPixelProperty );
        set => SetValue( SelectedPixelProperty, value );
    }

    public bool ShowCenterCross
    {
        get => GetValue( ShowCenterCrossProperty );
        set => SetValue( ShowCenterCrossProperty, value );
    }

    /// <summary>Size of the drawing area (without scrollbars).</summary>
    public Size ViewSize => _view.Bounds.Size;

    /// <summary>Number of cached tile bitmaps.</summary>
    public int CachedTileCount => _tiles.Count;

    /// <summary>Completes once no background tile render is outstanding (for tests and screenshots).</summary>
    public Task WhenIdleAsync() => _pending.Count == 0 ? Task.CompletedTask : ( _idle ??= new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously ) ).Task;

    /// <summary>Source pixel under a point of the drawing area.</summary>
    public PixelPoint PixelAt( Point point )
    {
        var zoom = EffectiveZoom;
        var size = _view.Bounds.Size;
        return new PixelPoint(
            ( int )Math.Floor( CenterX - size.Width / 2 / zoom + point.X / zoom ),
            ( int )Math.Floor( CenterY - size.Height / 2 / zoom + point.Y / zoom ) );
    }

    /// <summary>Zooms by a power of two, keeping the pixel under <paramref name="anchor" /> (default: centre) in place.</summary>
    public void ZoomBy( int steps, Point? anchor = null )
    {
        var old = EffectiveZoom;
        var zoom = Math.Clamp( old * Math.Pow( 2, steps ), MinZoom, MaxZoom );

        if ( zoom == old )
        {
            return;
        }

        var size = _view.Bounds.Size;
        var a = anchor ?? new Point( size.Width / 2, size.Height / 2 );
        var dx = a.X - size.Width / 2;
        var dy = a.Y - size.Height / 2;

        // The source pixel under the anchor stays under it.
        CenterX += dx / old - dx / zoom;
        CenterY += dy / old - dy / zoom;
        Zoom = zoom;
    }

    private double EffectiveZoom => Zoom > 0 ? Zoom : 1;

    protected override void OnPropertyChanged( AvaloniaPropertyChangedEventArgs change )
    {
        base.OnPropertyChanged( change );

        // Grid sets properties from its constructor, before ours has built the children.
        if ( _view is null )
        {
            return;
        }

        if ( change.Property == SourceProperty )
        {
            ClearTiles();
            UpdateScrollBars();
        }
        else if ( change.Property == CenterXProperty || change.Property == CenterYProperty || change.Property == ZoomProperty )
        {
            UpdateScrollBars();
        }

        _view.InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree( VisualTreeAttachmentEventArgs e )
    {
        base.OnDetachedFromVisualTree( e );
        ClearTiles();
    }

    private void ClearTiles()
    {
        _version++;

        foreach ( var (_, bitmap) in _lru )
        {
            bitmap.Dispose();
        }

        _lru.Clear();
        _tiles.Clear();
        _pending.Clear();
        CompleteIdle();
    }

    private void CompleteIdle()
    {
        if ( _pending.Count == 0 && _idle is { } idle )
        {
            _idle = null;
            idle.TrySetResult();
        }
    }

    private void UpdateScrollBars()
    {
        if ( _syncingScroll )
        {
            return;
        }

        _syncingScroll = true;

        try
        {
            var zoom = EffectiveZoom;
            var size = _view.Bounds.Size;
            var visibleW = size.Width / zoom;
            var visibleH = size.Height / zoom;
            var width = Source?.Width ?? 0;
            var height = Source?.Height ?? 0;

            _hScroll.Maximum = Math.Max( 0, width - visibleW );
            _hScroll.ViewportSize = visibleW;
            _hScroll.LargeChange = Math.Max( 1, visibleW * 0.9 );
            _hScroll.Value = Math.Clamp( CenterX - visibleW / 2, 0, _hScroll.Maximum );
            _vScroll.Maximum = Math.Max( 0, height - visibleH );
            _vScroll.ViewportSize = visibleH;
            _vScroll.LargeChange = Math.Max( 1, visibleH * 0.9 );
            _vScroll.Value = Math.Clamp( CenterY - visibleH / 2, 0, _vScroll.Maximum );
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void OnScrolled()
    {
        if ( _syncingScroll )
        {
            return;
        }

        var zoom = EffectiveZoom;
        var size = _view.Bounds.Size;
        _syncingScroll = true;

        try
        {
            CenterX = _hScroll.Value + size.Width / zoom / 2;
            CenterY = _vScroll.Value + size.Height / zoom / 2;
        }
        finally
        {
            _syncingScroll = false;
        }

        _view.InvalidateVisual();
    }

    /// <summary>Cached tile, rendering it inline when cheap or queueing it otherwise. Null while pending.</summary>
    private Bitmap? GetTile( IMapImageSource source, int step, int tx, int ty, bool request )
    {
        var key = ( step, tx, ty );

        if ( _tiles.TryGetValue( key, out var node ) )
        {
            _lru.Remove( node );
            _lru.AddFirst( node );
            return node.Value.Bitmap;
        }

        if ( !request )
        {
            return null;
        }

        var span = TileSize * step;

        if ( !source.IsSlow( step ) )
        {
            var pixmap = new Pixmap( TileSize, TileSize );
            source.Render( pixmap, tx * span, ty * span, step );
            return AddTile( key, pixmap );
        }

        if ( _pending.Add( key ) )
        {
            var version = _version;

            Task.Run( () =>
            {
                var pixmap = new Pixmap( TileSize, TileSize );
                source.Render( pixmap, tx * span, ty * span, step );
                return pixmap;
            } ).ContinueWith( t => Dispatcher.UIThread.Post( () =>
            {
                if ( version != _version )
                {
                    return;
                }

                _pending.Remove( key );

                if ( t.IsCompletedSuccessfully )
                {
                    AddTile( key, t.Result );
                }

                _view.InvalidateVisual();
                CompleteIdle();
            } ), TaskScheduler.Default );
        }

        return null;
    }

    private Bitmap? AddTile( (int Step, int X, int Y) key, Pixmap pixmap )
    {
        if ( pixmap.ToBitmap() is not { } bitmap )
        {
            return null;
        }

        _tiles[key] = _lru.AddFirst( ( key, bitmap ) );

        while ( _tiles.Count > CacheCapacity && _lru.Last is { } last )
        {
            _lru.RemoveLast();
            _tiles.Remove( last.Value.Key );
            last.Value.Bitmap.Dispose();
        }

        return bitmap;
    }

    private void Draw( DrawingContext context )
    {
        var size = _view.Bounds.Size;
        context.FillRectangle( Brush( "SystemControlBackgroundAltHighBrush", Brushes.Black ), new Rect( size ) );

        if ( Source is not { } source || size.Width <= 0 || size.Height <= 0 )
        {
            return;
        }

        var zoom = EffectiveZoom;
        var step = zoom >= 1 ? 1 : Math.Max( 1, ( int )Math.Round( 1 / zoom ) );
        var span = TileSize * step;
        var left = CenterX - size.Width / 2 / zoom;
        var top = CenterY - size.Height / 2 / zoom;
        var image = new Rect( -left * zoom, -top * zoom, source.Width * zoom, source.Height * zoom );

        var tx0 = Math.Max( 0, ( int )Math.Floor( left / span ) );
        var ty0 = Math.Max( 0, ( int )Math.Floor( top / span ) );
        var tx1 = Math.Min( ( source.Width - 1 ) / span, ( int )Math.Floor( ( left + size.Width / zoom ) / span ) );
        var ty1 = Math.Min( ( source.Height - 1 ) / span, ( int )Math.Floor( ( top + size.Height / zoom ) / span ) );

        using ( context.PushClip( image.Intersect( new Rect( size ) ) ) )
        using ( context.PushRenderOptions( new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None } ) )
        {
            for ( var ty = ty0; ty <= ty1; ty++ )
            {
                for ( var tx = tx0; tx <= tx1; tx++ )
                {
                    // Snap to whole pixels so neighbouring tiles share edges (no anti-aliased seams).
                    var x0 = Math.Round( ( tx * span - left ) * zoom );
                    var y0 = Math.Round( ( ty * span - top ) * zoom );
                    var dest = new Rect( x0, y0, Math.Round( ( ( tx + 1 ) * span - left ) * zoom ) - x0, Math.Round( ( ( ty + 1 ) * span - top ) * zoom ) - y0 );

                    if ( GetTile( source, step, tx, ty, true ) is { } bitmap )
                    {
                        context.DrawImage( bitmap, new Rect( 0, 0, TileSize, TileSize ), dest );
                        continue;
                    }

                    // Pending: stretch a coarser cached tile over the gap.
                    for ( var coarse = step * 2; coarse <= step * 16; coarse *= 2 )
                    {
                        var coarseSpan = TileSize * coarse;
                        var cx = tx * span / coarseSpan;
                        var cy = ty * span / coarseSpan;

                        if ( GetTile( source, coarse, cx, cy, false ) is { } fallback )
                        {
                            var sx = ( tx * span - cx * coarseSpan ) / ( double )coarse;
                            var sy = ( ty * span - cy * coarseSpan ) / ( double )coarse;
                            context.DrawImage( fallback, new Rect( sx, sy, span / ( double )coarse, span / ( double )coarse ), dest );
                            break;
                        }
                    }
                }
            }
        }

        var accent = Brush( "SystemControlHighlightAccentBrush", Brushes.DodgerBlue );

        if ( SelectedPixel is { } selected )
        {
            var cell = Math.Max( zoom, 6 );
            var cx = ( selected.X + 0.5 - left ) * zoom;
            var cy = ( selected.Y + 0.5 - top ) * zoom;
            context.DrawRectangle( null, new Pen( accent, 2 ), new Rect( cx - cell / 2, cy - cell / 2, cell, cell ) );
        }

        if ( ShowCenterCross )
        {
            var pen = new Pen( accent, 1 );
            var mx = Math.Round( size.Width / 2 ) + 0.5;
            var my = Math.Round( size.Height / 2 ) + 0.5;
            context.DrawLine( pen, new Point( mx - 8, my ), new Point( mx + 8, my ) );
            context.DrawLine( pen, new Point( mx, my - 8 ), new Point( mx, my + 8 ) );
        }
    }

    private IBrush Brush( string key, IBrush fallback ) =>
        this.TryFindResource( key, ActualThemeVariant, out var value ) && value is IBrush brush ? brush : fallback;

    private sealed class MapView( MapCanvas owner ) : Control
    {
        private Point? _pressPoint;
        private Point _pressCenter;
        private bool _dragging;

        public override void Render( DrawingContext context ) => owner.Draw( context );

        protected override void OnPointerPressed( PointerPressedEventArgs e )
        {
            base.OnPointerPressed( e );

            if ( !e.GetCurrentPoint( this ).Properties.IsLeftButtonPressed )
            {
                return;
            }

            _pressPoint = e.GetPosition( this );
            _pressCenter = new Point( owner.CenterX, owner.CenterY );
            _dragging = false;
            e.Pointer.Capture( this );
            e.Handled = true;
        }

        protected override void OnPointerMoved( PointerEventArgs e )
        {
            base.OnPointerMoved( e );
            var point = e.GetPosition( this );
            UpdateHover( point );

            if ( _pressPoint is not { } press )
            {
                return;
            }

            var delta = point - press;

            if ( !_dragging && Math.Abs( delta.X ) + Math.Abs( delta.Y ) < 4 )
            {
                return;
            }

            _dragging = true;
            var zoom = owner.EffectiveZoom;
            owner.CenterX = _pressCenter.X - delta.X / zoom;
            owner.CenterY = _pressCenter.Y - delta.Y / zoom;
        }

        protected override void OnPointerReleased( PointerReleasedEventArgs e )
        {
            base.OnPointerReleased( e );

            if ( _pressPoint is null )
            {
                return;
            }

            if ( !_dragging && owner.Source is { } source )
            {
                var pixel = owner.PixelAt( e.GetPosition( this ) );

                if ( ( uint )pixel.X < ( uint )source.Width && ( uint )pixel.Y < ( uint )source.Height )
                {
                    owner.SelectedPixel = pixel;
                }
            }

            _pressPoint = null;
            _dragging = false;
            e.Pointer.Capture( null );
        }

        protected override void OnPointerExited( PointerEventArgs e )
        {
            base.OnPointerExited( e );
            owner.HoverPixel = null;
        }

        protected override void OnPointerWheelChanged( PointerWheelEventArgs e )
        {
            base.OnPointerWheelChanged( e );

            if ( e.Delta.Y != 0 )
            {
                owner.ZoomBy( Math.Sign( e.Delta.Y ), e.GetPosition( this ) );
                UpdateHover( e.GetPosition( this ) );
            }

            e.Handled = true;
        }

        private void UpdateHover( Point point )
        {
            if ( owner.Source is not { } source )
            {
                owner.HoverPixel = null;
                return;
            }

            var pixel = owner.PixelAt( point );
            owner.HoverPixel = ( uint )pixel.X < ( uint )source.Width && ( uint )pixel.Y < ( uint )source.Height ? pixel : null;
        }
    }
}

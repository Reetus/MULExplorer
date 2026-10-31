using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MULExplorer.Controls;

/// <summary>
///     Grid of thumbnails for tens of thousands of ids. Only visible cells are drawn and images
///     are fetched on demand through <see cref="ImageProvider" />, so it stays fast without item
///     containers. Click or arrow keys select; the wheel and scrollbar scroll by rows.
/// </summary>
public sealed class TileGrid : Grid
{
    public static readonly StyledProperty<IReadOnlyList<int>?> IdsProperty =
        AvaloniaProperty.Register<TileGrid, IReadOnlyList<int>?>( nameof( Ids ) );

    public static readonly StyledProperty<Func<int, Bitmap?>?> ImageProviderProperty =
        AvaloniaProperty.Register<TileGrid, Func<int, Bitmap?>?>( nameof( ImageProvider ) );

    public static readonly StyledProperty<int> SelectedIdProperty =
        AvaloniaProperty.Register<TileGrid, int>( nameof( SelectedId ), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay );

    public static readonly StyledProperty<double> CellWidthProperty =
        AvaloniaProperty.Register<TileGrid, double>( nameof( CellWidth ), 64 );

    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<TileGrid, double>( nameof( CellHeight ), 64 );

    /// <summary>Draw images at natural size (cropped to the cell) instead of scaling down to fit.</summary>
    public static readonly StyledProperty<bool> NaturalSizeProperty =
        AvaloniaProperty.Register<TileGrid, bool>( nameof( NaturalSize ) );

    private readonly TileCanvas _canvas;
    private readonly ScrollBar _scrollBar;

    static TileGrid()
    {
        FocusableProperty.OverrideDefaultValue<TileGrid>( true );

        // The last partially visible row is drawn past the bottom edge; never paint over neighbours.
        ClipToBoundsProperty.OverrideDefaultValue<TileGrid>( true );
    }

    public TileGrid()
    {
        ColumnDefinitions = new ColumnDefinitions( "*,Auto" );
        _canvas = new TileCanvas( this );
        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, SmallChange = 1, AllowAutoHide = false };
        _scrollBar.Scroll += ( _, _ ) => _canvas.InvalidateVisual();
        SetColumn( _scrollBar, 1 );
        Children.Add( _canvas );
        Children.Add( _scrollBar );
        // Selection often arrives before the first layout (columns unknown), so re-anchor on resize.
        _canvas.SizeChanged += ( _, _ ) =>
        {
            UpdateScroll();
            ScrollIntoView( SelectedId );
        };
    }

    public IReadOnlyList<int>? Ids
    {
        get => GetValue( IdsProperty );
        set => SetValue( IdsProperty, value );
    }

    public Func<int, Bitmap?>? ImageProvider
    {
        get => GetValue( ImageProviderProperty );
        set => SetValue( ImageProviderProperty, value );
    }

    public int SelectedId
    {
        get => GetValue( SelectedIdProperty );
        set => SetValue( SelectedIdProperty, value );
    }

    public double CellWidth
    {
        get => GetValue( CellWidthProperty );
        set => SetValue( CellWidthProperty, value );
    }

    public double CellHeight
    {
        get => GetValue( CellHeightProperty );
        set => SetValue( CellHeightProperty, value );
    }

    public bool NaturalSize
    {
        get => GetValue( NaturalSizeProperty );
        set => SetValue( NaturalSizeProperty, value );
    }

    internal int Columns => Math.Max( 1, ( int )( _canvas.Bounds.Width / CellWidth ) );

    internal int VisibleRows => Math.Max( 1, ( int )Math.Ceiling( _canvas.Bounds.Height / CellHeight ) );

    internal int FirstRow => ( int )_scrollBar.Value;

    private int RowCount => ( ( Ids?.Count ?? 0 ) + Columns - 1 ) / Columns;

    protected override void OnPropertyChanged( AvaloniaPropertyChangedEventArgs change )
    {
        base.OnPropertyChanged( change );

        if ( change.Property == IdsProperty || change.Property == CellWidthProperty || change.Property == CellHeightProperty )
        {
            UpdateScroll();
            ScrollIntoView( SelectedId );
        }
        else if ( change.Property == SelectedIdProperty )
        {
            ScrollIntoView( SelectedId );
        }

        _canvas.InvalidateVisual();
    }

    private void UpdateScroll()
    {
        var fullyVisible = Math.Max( 1, ( int )( _canvas.Bounds.Height / CellHeight ) );
        _scrollBar.Maximum = Math.Max( 0, RowCount - fullyVisible );
        _scrollBar.ViewportSize = fullyVisible;
        _scrollBar.LargeChange = fullyVisible;
        _scrollBar.Value = Math.Min( _scrollBar.Value, _scrollBar.Maximum );
        _canvas.InvalidateVisual();
    }

    public void ScrollIntoView( int id )
    {
        if ( Ids is not { } ids || IndexOf( ids, id ) is var index && index < 0 )
        {
            return;
        }

        var row = index / Columns;
        var fullyVisible = Math.Max( 1, ( int )( _canvas.Bounds.Height / CellHeight ) );

        if ( row < FirstRow )
        {
            _scrollBar.Value = row;
        }
        else if ( row >= FirstRow + fullyVisible )
        {
            _scrollBar.Value = Math.Min( _scrollBar.Maximum, row - fullyVisible + 1 );
        }
    }

    protected override void OnPointerWheelChanged( PointerWheelEventArgs e )
    {
        base.OnPointerWheelChanged( e );
        _scrollBar.Value = Math.Clamp( _scrollBar.Value - Math.Sign( e.Delta.Y ) * 3, 0, _scrollBar.Maximum );
        _canvas.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown( KeyEventArgs e )
    {
        base.OnKeyDown( e );

        if ( Ids is not { Count: > 0 } ids )
        {
            return;
        }

        var index = Math.Max( 0, IndexOf( ids, SelectedId ) );
        var step = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -Columns,
            Key.Down => Columns,
            Key.PageUp => -Columns * Math.Max( 1, VisibleRows - 1 ),
            Key.PageDown => Columns * Math.Max( 1, VisibleRows - 1 ),
            Key.Home => -index,
            Key.End => ids.Count - 1 - index,
            _ => 0
        };

        if ( step != 0 )
        {
            SelectedId = ids[Math.Clamp( index + step, 0, ids.Count - 1 )];
            e.Handled = true;
        }
    }

    internal void SelectAt( Point point )
    {
        if ( Ids is not { } ids )
        {
            return;
        }

        var col = ( int )( point.X / CellWidth );
        var index = ( FirstRow + ( int )( point.Y / CellHeight ) ) * Columns + col;

        if ( col < Columns && ( uint )index < ( uint )ids.Count )
        {
            SelectedId = ids[index];
        }

        Focus();
    }

    /// <summary>Ids lists are usually sorted, so try a binary search before falling back to a scan.</summary>
    internal static int IndexOf( IReadOnlyList<int> ids, int id )
    {
        int lo = 0, hi = ids.Count - 1;

        while ( lo <= hi )
        {
            var mid = ( lo + hi ) >>> 1;
            var v = ids[mid];

            if ( v == id )
            {
                return mid;
            }

            if ( v < id )
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for ( var i = 0; i < ids.Count; i++ )
        {
            if ( ids[i] == id )
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class TileCanvas( TileGrid owner ) : Control
    {
        protected override void OnPointerPressed( PointerPressedEventArgs e )
        {
            base.OnPointerPressed( e );
            owner.SelectAt( e.GetPosition( this ) );
        }

        public override void Render( DrawingContext context )
        {
            var bounds = new Rect( Bounds.Size );
            context.FillRectangle( Brush( "SystemControlBackgroundAltHighBrush", Brushes.Black ), bounds );

            if ( owner.Ids is not { } ids || owner.ImageProvider is not { } provider )
            {
                return;
            }

            var cw = owner.CellWidth;
            var ch = owner.CellHeight;
            var columns = owner.Columns;
            var first = owner.FirstRow * columns;
            var last = Math.Min( ids.Count, first + ( owner.VisibleRows + 1 ) * columns );
            var gridPen = new Pen( Brush( "SystemControlForegroundBaseLowBrush", Brushes.Gray ), 1 );
            var selectPen = new Pen( Brush( "SystemControlHighlightAccentBrush", Brushes.DodgerBlue ), 2 );

            for ( var i = first; i < last; i++ )
            {
                var cell = new Rect( ( i - first ) % columns * cw, ( i - first ) / columns * ch, cw, ch );
                var image = provider( ids[i] );

                if ( image is not null )
                {
                    var size = image.Size;
                    var scale = owner.NaturalSize ? 1 : Math.Min( 1, Math.Min( ( cw - 4 ) / size.Width, ( ch - 4 ) / size.Height ) );
                    var w = size.Width * scale;
                    var h = size.Height * scale;
                    var dest = new Rect( cell.X + ( cw - w ) / 2, cell.Y + ( ch - h ) / 2, w, h );

                    using ( context.PushClip( cell.Deflate( 1 ) ) )
                    {
                        context.DrawImage( image, new Rect( size ), dest );
                    }
                }

                context.DrawRectangle( null, gridPen, cell );

                if ( ids[i] == owner.SelectedId )
                {
                    context.DrawRectangle( null, selectPen, cell.Deflate( 1 ) );
                }
            }
        }

        private IBrush Brush( string key, IBrush fallback ) =>
            this.TryFindResource( key, ActualThemeVariant, out var value ) && value is IBrush brush ? brush : fallback;
    }
}

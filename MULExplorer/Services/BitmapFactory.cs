using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UODataAccess.Imaging;

namespace MULExplorer.Services;

/// <summary>Turns decoded <see cref="Pixmap" />s into Avalonia bitmaps.</summary>
public static class BitmapFactory
{
    public static unsafe WriteableBitmap? ToBitmap( this Pixmap? pixmap )
    {
        if ( pixmap is null || pixmap.Width <= 0 || pixmap.Height <= 0 )
        {
            return null;
        }

        var bitmap = new WriteableBitmap( new PixelSize( pixmap.Width, pixmap.Height ), new Vector( 96, 96 ), PixelFormat.Bgra8888, AlphaFormat.Premul );

        using var buffer = bitmap.Lock();
        var source = MemoryMarshal.AsBytes( pixmap.Pixels.AsSpan() );
        var rowBytes = pixmap.Width * 4;

        for ( var y = 0; y < pixmap.Height; y++ )
        {
            source.Slice( y * rowBytes, rowBytes ).CopyTo( new Span<byte>( ( byte* )buffer.Address + y * buffer.RowBytes, rowBytes ) );
        }

        return bitmap;
    }
}

/// <summary>
///     Small LRU cache of bitmaps keyed by id, so scrolling a tile grid does not re-decode art.
///     UI-thread only.
/// </summary>
public sealed class BitmapCache( Func<int, Bitmap?> factory, int capacity = 2048 )
{
    private readonly Dictionary<int, LinkedListNode<(int Key, Bitmap? Value)>> _map = new();
    private readonly LinkedList<(int Key, Bitmap? Value)> _order = new();

    public Bitmap? Get( int key )
    {
        if ( _map.TryGetValue( key, out var node ) )
        {
            _order.Remove( node );
            _order.AddFirst( node );
            return node.Value.Value;
        }

        Bitmap? value;

        try
        {
            value = factory( key );
        }
        catch ( Exception e ) when ( e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException )
        {
            value = null;
        }

        _map[key] = _order.AddFirst( ( key, value ) );

        if ( _map.Count > capacity && _order.Last is { } last )
        {
            _order.RemoveLast();
            _map.Remove( last.Value.Key );
            last.Value.Value?.Dispose();
        }

        return value;
    }

    public void Clear()
    {
        foreach ( var (_, value) in _order )
        {
            value?.Dispose();
        }

        _order.Clear();
        _map.Clear();
    }
}

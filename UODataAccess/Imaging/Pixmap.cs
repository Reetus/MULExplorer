using System.Runtime.CompilerServices;

namespace UODataAccess.Imaging;

/// <summary>
///     A decoded image: 32-bit pixels laid out as <c>0xAARRGGBB</c>, i.e. BGRA8888 in memory on
///     little-endian hosts, which is what Avalonia's <c>PixelFormat.Bgra8888</c> expects.
///     UO art only uses alpha 0 or 255, so premultiplied and straight alpha are identical.
/// </summary>
public sealed class Pixmap( int width, int height, uint[] pixels )
{
    public Pixmap( int width, int height ) : this( width, height, new uint[width * height] )
    {
    }

    public int Width { get; } = width;

    public int Height { get; } = height;

    public uint[] Pixels { get; } = pixels;

    public Span<uint> Row( int y ) => Pixels.AsSpan( y * Width, Width );

    public uint this[ int x, int y ]
    {
        get => ( uint )x < ( uint )Width && ( uint )y < ( uint )Height ? Pixels[y * Width + x] : 0u;
        set
        {
            if ( ( uint )x < ( uint )Width && ( uint )y < ( uint )Height )
            {
                Pixels[y * Width + x] = value;
            }
        }
    }

    public Pixmap Clone() => new( Width, Height, ( uint[] )Pixels.Clone() );

    /// <summary>Copies <paramref name="source" /> onto this image at (x, y), skipping transparent pixels.</summary>
    public void Blit( Pixmap source, int x, int y )
    {
        for ( var sy = 0; sy < source.Height; sy++ )
        {
            var dy = y + sy;

            if ( ( uint )dy >= ( uint )Height )
            {
                continue;
            }

            var src = source.Row( sy );
            var dst = Row( dy );

            for ( var sx = 0; sx < src.Length; sx++ )
            {
                var dx = x + sx;

                if ( ( uint )dx < ( uint )dst.Length && src[sx] >> 24 != 0 )
                {
                    dst[dx] = src[sx];
                }
            }
        }
    }

    /// <summary>Smallest rectangle containing every non-transparent pixel, or null if fully transparent.</summary>
    public (int X, int Y, int Width, int Height)? OpaqueBounds()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;

        for ( var y = 0; y < Height; y++ )
        {
            var row = Row( y );

            for ( var x = 0; x < row.Length; x++ )
            {
                if ( row[x] >> 24 == 0 )
                {
                    continue;
                }

                minX = Math.Min( minX, x );
                maxX = Math.Max( maxX, x );
                minY = Math.Min( minY, y );
                maxY = y;
            }
        }

        return maxX < 0 ? null : ( minX, minY, maxX - minX + 1, maxY - minY + 1 );
    }
}

/// <summary>UO's 16-bit ARGB1555 colors.</summary>
public static class Color16
{
    /// <summary>ARGB1555 to opaque 0xAARRGGBB; 5-bit channels are widened by bit replication.</summary>
    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    public static uint ToArgb( ushort c )
    {
        var r = Expand( c >> 10 );
        var g = Expand( c >> 5 );
        var b = Expand( c );
        return 0xFF00_0000u | ( r << 16 ) | ( g << 8 ) | b;
    }

    /// <summary>Like <see cref="ToArgb" /> but color 0 is transparent, the convention for art and gumps.</summary>
    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    public static uint ToArgbTransparent( ushort c ) => c == 0 ? 0u : ToArgb( c );

    /// <summary>Back to 15-bit RGB (alpha bit dropped). Exact inverse of <see cref="ToArgb" />.</summary>
    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    public static ushort FromArgb( uint argb ) =>
        ( ushort )( ( ( argb >> 19 ) & 0x1F ) << 10 | ( ( argb >> 11 ) & 0x1F ) << 5 | ( ( argb >> 3 ) & 0x1F ) );

    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    private static uint Expand( int v )
    {
        v &= 0x1F;
        return ( uint )( ( v << 3 ) | ( v >> 2 ) );
    }
}

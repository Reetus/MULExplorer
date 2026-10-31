using System.Buffers.Binary;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     A decoded overview image kept as ARGB1555 (half the memory of 32-bit; the facet images
///     are up to 7168x4096). <see cref="Render" /> converts any window of it, optionally
///     downsampled, into a <see cref="Pixmap" />.
/// </summary>
public sealed class MultiMapImage( int width, int height, ushort[] colors )
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>Row-major ARGB1555 pixels.</summary>
    public ReadOnlySpan<ushort> Colors => colors;

    public ushort this[ int x, int y ] => ( uint )x < ( uint )Width && ( uint )y < ( uint )Height ? colors[y * Width + x] : ( ushort )0;

    /// <summary>
    ///     Pixel (px, py) of <paramref name="target" /> shows image pixel <c>(x + px * step, y + py * step)</c>;
    ///     with <paramref name="step" /> &gt; 1 it is the average of that step x step box, so thin
    ///     1-bit ink lines in Multimap.rle survive downscaling. Outside the image is transparent.
    /// </summary>
    public void Render( Pixmap target, int x, int y, int step = 1 )
    {
        step = Math.Max( 1, step );

        for ( var py = 0; py < target.Height; py++ )
        {
            var row = target.Row( py );
            var sy = y + py * step;

            if ( sy < 0 || sy >= Height )
            {
                row.Clear();
                continue;
            }

            if ( step == 1 )
            {
                var source = colors.AsSpan( sy * Width, Width );

                for ( var px = 0; px < row.Length; px++ )
                {
                    var sx = x + px;
                    row[px] = ( uint )sx < ( uint )Width ? Color16.ToArgb( source[sx] ) : 0u;
                }

                continue;
            }

            var rows = Math.Min( step, Height - sy );

            for ( var px = 0; px < row.Length; px++ )
            {
                var sx = x + px * step;

                if ( sx < 0 || sx >= Width )
                {
                    row[px] = 0u;
                    continue;
                }

                var cols = Math.Min( step, Width - sx );
                uint r = 0, g = 0, b = 0;

                for ( var dy = 0; dy < rows; dy++ )
                {
                    foreach ( var c in colors.AsSpan( ( sy + dy ) * Width + sx, cols ) )
                    {
                        var argb = Color16.ToArgb( c );
                        r += ( argb >> 16 ) & 0xFF;
                        g += ( argb >> 8 ) & 0xFF;
                        b += argb & 0xFF;
                    }
                }

                var n = ( uint )( rows * cols );
                row[px] = 0xFF00_0000u | ( r / n << 16 ) | ( g / n << 8 ) | ( b / n );
            }
        }
    }

    /// <summary>The whole image, downsampled by <paramref name="step" />.</summary>
    public Pixmap ToPixmap( int step = 1 )
    {
        step = Math.Max( 1, step );
        var pixmap = new Pixmap( Math.Max( 1, ( Width + step - 1 ) / step ), Math.Max( 1, ( Height + step - 1 ) / step ) );
        Render( pixmap, 0, 0, step );
        return pixmap;
    }
}

/// <summary>
///     <c>Multimap.rle</c> (the 1-bit world overview used by the client's map gump) and the
///     <c>facet0N.mul</c> facet images. Read-only.
/// </summary>
public sealed class MultiMap
{
    /// <summary>UOFiddler renders the RLE ink black on white.</summary>
    public const ushort Ink = 0x8000;

    public const ushort Paper = 0xFFFF;

    public MultiMap( UOFileSet files )
    {
        Files = files;
        MultiMapPath = files.Find( "Multimap.rle" );

        var facets = new List<int>();

        for ( var i = 0; i < 10; i++ )
        {
            if ( files.Exists( FacetFileName( i ) ) )
            {
                facets.Add( i );
            }
        }

        FacetIds = facets;

        if ( MultiMapPath is null && facets.Count == 0 )
        {
            throw new FileNotFoundException( "Neither Multimap.rle nor facet0N.mul found." );
        }
    }

    public UOFileSet Files { get; }

    public string? MultiMapPath { get; }

    public bool HasMultiMap => MultiMapPath is not null;

    /// <summary>The N of each <c>facet0N.mul</c> present.</summary>
    public IReadOnlyList<int> FacetIds { get; }

    public static string FacetFileName( int id ) => $"facet0{id}.mul";

    public bool HasFacet( int id ) => FacetIds.Contains( id );

    public MultiMapImage? GetMultiMap() => MultiMapPath is null ? null : DecodeRle( File.ReadAllBytes( MultiMapPath ) );

    public MultiMapImage? GetFacetImage( int id ) =>
        Files.Find( FacetFileName( id ) ) is { } path ? DecodeFacet( File.ReadAllBytes( path ) ) : null;

    /// <summary>Width/height from a file header without decoding.</summary>
    public (int Width, int Height)? GetMultiMapSize()
    {
        if ( MultiMapPath is null )
        {
            return null;
        }

        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead( MultiMapPath );
        return stream.ReadAtLeast( header, 8, false ) == 8
            ? ( BinaryPrimitives.ReadInt32LittleEndian( header ), BinaryPrimitives.ReadInt32LittleEndian( header[4..] ) )
            : null;
    }

    public (int Width, int Height)? GetFacetSize( int id )
    {
        if ( Files.Find( FacetFileName( id ) ) is not { } path )
        {
            return null;
        }

        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead( path );
        return stream.ReadAtLeast( header, 4, false ) == 4
            ? ( BinaryPrimitives.ReadUInt16LittleEndian( header ), BinaryPrimitives.ReadUInt16LittleEndian( header[2..] ) )
            : null;
    }

    /// <summary>
    ///     i32 width, i32 height, then bytes of <c>(run: 7 bits, ink: high bit)</c> filling the
    ///     image row by row.
    /// </summary>
    public static MultiMapImage DecodeRle( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 8 )
        {
            throw new InvalidDataException( "Multimap.rle is truncated." );
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian( data );
        var height = BinaryPrimitives.ReadInt32LittleEndian( data[4..] );

        if ( width <= 0 || height <= 0 || ( long )width * height > 64L * 1024 * 1024 )
        {
            throw new InvalidDataException( $"Multimap.rle has an invalid size {width}x{height}." );
        }

        var pixels = new ushort[width * height];
        var pos = 0;

        foreach ( var b in data[8..] )
        {
            var run = b & 0x7F;

            if ( run > pixels.Length - pos )
            {
                run = pixels.Length - pos;
            }

            pixels.AsSpan( pos, run ).Fill( ( b & 0x80 ) != 0 ? Ink : Paper );
            pos += run;

            if ( pos == pixels.Length )
            {
                break;
            }
        }

        // Whatever the runs do not cover stays paper, as in a fresh bitmap.
        pixels.AsSpan( pos ).Fill( Paper );
        return new MultiMapImage( width, height, pixels );
    }

    /// <summary>
    ///     u16 width, u16 height, then per row an i32 byte count followed by
    ///     <c>(u8 run, u16 ARGB1555 color)</c> entries.
    /// </summary>
    public static MultiMapImage DecodeFacet( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 4 )
        {
            throw new InvalidDataException( "Facet image is truncated." );
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian( data );
        int height = BinaryPrimitives.ReadUInt16LittleEndian( data[2..] );

        if ( width == 0 || height == 0 )
        {
            throw new InvalidDataException( $"Facet image has an invalid size {width}x{height}." );
        }

        var pixels = new ushort[width * height];
        var pos = 4;

        for ( var y = 0; y < height && pos + 4 <= data.Length; y++ )
        {
            var bytes = BinaryPrimitives.ReadInt32LittleEndian( data[pos..] );
            pos += 4;

            if ( bytes < 0 || bytes > data.Length - pos )
            {
                bytes = data.Length - pos;
            }

            var line = data.Slice( pos, bytes / 3 * 3 );
            pos += bytes;

            var row = pixels.AsSpan( y * width, width );
            var x = 0;

            for ( var i = 0; i + 3 <= line.Length && x < width; i += 3 )
            {
                var run = Math.Min( line[i], width - x );
                row.Slice( x, run ).Fill( BinaryPrimitives.ReadUInt16LittleEndian( line[( i + 1 )..] ) );
                x += run;
            }
        }

        return new MultiMapImage( width, height, pixels );
    }
}

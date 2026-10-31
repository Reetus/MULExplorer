using System.Buffers.Binary;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     Land and static art from <c>artLegacyMUL.uop</c> or <c>art.mul</c>/<c>artidx.mul</c>.
///     Ids 0..0x3FFF are 44x44 land diamonds, 0x4000+ are statics (items), addressed here by
///     their item id (without the 0x4000 offset).
/// </summary>
public sealed class Art : IDisposable
{
    public const int LandCount = 0x4000;

    private readonly FileIndex _index;

    public Art( UOFileSet files )
    {
        _index = FileIndex.Open( files, "artidx.mul", "art.mul", "artLegacyMUL.uop", "build/artlegacymul/{0:D8}.tga", 0x14000 )
                 ?? throw new FileNotFoundException( "No art found (artLegacyMUL.uop or art.mul/artidx.mul)." );
    }

    public string Source => _index.Source;

    public FileIndex Index => _index;

    /// <summary>Number of addressable item (static) ids.</summary>
    public int ItemCount => Math.Max( 0, _index.Length - LandCount );

    public void Dispose() => _index.Dispose();

    public bool IsValidLand( int id ) => ( uint )id < LandCount && _index.IsValid( id );

    public bool IsValidItem( int id ) => id >= 0 && _index.IsValid( id + LandCount );

    public Pixmap? GetLand( int id ) => IsValidLand( id ) && _index.TryRead( id, out var data ) ? DecodeLand( data ) : null;

    public Pixmap? GetItem( int id ) => id >= 0 && _index.TryRead( id + LandCount, out var data ) ? DecodeStatic( data ) : null;

    /// <summary>Width/height of an item without decoding its pixels.</summary>
    public (int Width, int Height)? GetItemSize( int id )
    {
        if ( id < 0 || !_index.TryRead( id + LandCount, out var data ) || data.Length < 8 )
        {
            return null;
        }

        return ( BinaryPrimitives.ReadInt16LittleEndian( data[4..] ), BinaryPrimitives.ReadInt16LittleEndian( data[6..] ) );
    }

    /// <summary>22 rows widening then 22 rows narrowing, packed u16 colors.</summary>
    public static Pixmap DecodeLand( ReadOnlySpan<byte> data )
    {
        const int Side = 44;
        var pixmap = new Pixmap( Side, Side );
        var pos = 0;

        for ( var y = 0; y < Side && pos + 2 <= data.Length; y++ )
        {
            var half = y < 22 ? y + 1 : Side - y;
            var start = 22 - half;
            var row = pixmap.Row( y );

            for ( var x = start; x < start + half * 2 && pos + 2 <= data.Length; x++, pos += 2 )
            {
                row[x] = Color16.ToArgb( BinaryPrimitives.ReadUInt16LittleEndian( data[pos..] ) );
            }
        }

        return pixmap;
    }

    /// <summary>
    ///     Header (u32 flags, i16 width, i16 height), a per-row table of u16 word offsets, then
    ///     per row a list of <c>(xOffset, runLength, runLength x u16)</c> ended by (0, 0).
    /// </summary>
    public static Pixmap? DecodeStatic( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 8 )
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt16LittleEndian( data[4..] );
        int height = BinaryPrimitives.ReadInt16LittleEndian( data[6..] );

        if ( width <= 0 || height <= 0 || width > 2048 || height > 2048 || data.Length < 8 + height * 2 )
        {
            return null;
        }

        var pixmap = new Pixmap( width, height );
        var lookup = data.Slice( 8, height * 2 );
        var body = 8 + height * 2;

        for ( var y = 0; y < height; y++ )
        {
            var p = body + BinaryPrimitives.ReadUInt16LittleEndian( lookup[( y * 2 )..] ) * 2;
            var row = pixmap.Row( y );
            var x = 0;

            while ( p + 4 <= data.Length )
            {
                var offset = BinaryPrimitives.ReadUInt16LittleEndian( data[p..] );
                var run = BinaryPrimitives.ReadUInt16LittleEndian( data[( p + 2 )..] );
                p += 4;

                if ( offset + run == 0 )
                {
                    break;
                }

                x += offset;

                for ( var i = 0; i < run && p + 2 <= data.Length; i++, p += 2, x++ )
                {
                    if ( ( uint )x < ( uint )width )
                    {
                        row[x] = Color16.ToArgbTransparent( BinaryPrimitives.ReadUInt16LittleEndian( data[p..] ) );
                    }
                }
            }
        }

        return pixmap;
    }
}

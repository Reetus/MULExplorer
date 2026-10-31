using System.Buffers.Binary;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     Gump (UI) art from <c>gumpartLegacyMUL.uop</c> or <c>gumpart.mul</c>/<c>gumpidx.mul</c>.
///     Each image is a table of per-row offsets (in 4-byte words) followed by
///     <c>(color:u16, run:u16)</c> pairs; color 0 is transparent.
/// </summary>
public sealed class Gumps : IDisposable
{
    public const int MaxGumps = 0x10000;

    private readonly FileIndex _index;

    public Gumps( UOFileSet files )
    {
        _index = FileIndex.Open( files, "gumpidx.mul", "gumpart.mul", "gumpartLegacyMUL.uop", "build/gumpartlegacymul/{0:D8}.tga", MaxGumps, uopHasExtra: true )
                 ?? throw new FileNotFoundException( "No gumps found (gumpartLegacyMUL.uop or gumpart.mul/gumpidx.mul)." );
    }

    public string Source => _index.Source;

    public int Count => _index.Length;

    public void Dispose() => _index.Dispose();

    public bool IsValid( int id ) => _index.IsValid( id );

    /// <summary>Width/height without decoding pixels (BWT-compressed entries must be decompressed).</summary>
    public (int Width, int Height)? GetSize( int id )
    {
        var entry = _index.GetEntry( id );

        if ( !entry.IsValid )
        {
            return null;
        }

        if ( entry.Compression != UOPCompression.ZlibBWT )
        {
            return ( entry.ExtraHigh, entry.ExtraLow );
        }

        return _index.TryRead( id, out var data ) && data.Length >= 8
            ? ( BinaryPrimitives.ReadInt32LittleEndian( data ), BinaryPrimitives.ReadInt32LittleEndian( data[4..] ) )
            : null;
    }

    public Pixmap? Get( int id )
    {
        if ( !_index.TryRead( id, out var data, out var entry ) )
        {
            return null;
        }

        int width, height;

        if ( entry.Compression == UOPCompression.ZlibBWT )
        {
            if ( data.Length < 8 )
            {
                return null;
            }

            width = BinaryPrimitives.ReadInt32LittleEndian( data );
            height = BinaryPrimitives.ReadInt32LittleEndian( data[4..] );
            data = data[8..];
        }
        else
        {
            width = entry.ExtraHigh;
            height = entry.ExtraLow;
        }

        return Decode( data, width, height );
    }

    public static Pixmap? Decode( ReadOnlySpan<byte> data, int width, int height )
    {
        if ( width <= 0 || height <= 0 || width > 4096 || height > 4096 || data.Length < height * 4 )
        {
            return null;
        }

        var pixmap = new Pixmap( width, height );
        var totalWords = data.Length / 4;

        for ( var y = 0; y < height; y++ )
        {
            var start = BinaryPrimitives.ReadInt32LittleEndian( data[( y * 4 )..] );
            var end = y < height - 1 ? BinaryPrimitives.ReadInt32LittleEndian( data[( ( y + 1 ) * 4 )..] ) : totalWords;
            var row = pixmap.Row( y );
            var x = 0;

            for ( var w = start; w < end && w < totalWords && x < width; w++ )
            {
                var color = BinaryPrimitives.ReadUInt16LittleEndian( data[( w * 4 )..] );
                var run = Math.Min( BinaryPrimitives.ReadUInt16LittleEndian( data[( w * 4 + 2 )..] ), width - x );

                row.Slice( x, run ).Fill( Color16.ToArgbTransparent( color ) );
                x += run;
            }
        }

        return pixmap;
    }
}

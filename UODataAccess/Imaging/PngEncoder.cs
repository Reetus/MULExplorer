using System.Buffers.Binary;
using System.IO.Compression;

namespace UODataAccess.Imaging;

/// <summary>
///     Minimal PNG writer (RGBA8, filter 0) so the data layer can hand images to MCP clients and
///     tests without an imaging dependency.
/// </summary>
public static class PngEncoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode( Pixmap pixmap )
    {
        using var output = new MemoryStream();
        Write( pixmap, output );
        return output.ToArray();
    }

    public static void Write( Pixmap pixmap, Stream output )
    {
        output.Write( Signature );

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian( header, pixmap.Width );
        BinaryPrimitives.WriteInt32BigEndian( header[4..], pixmap.Height );
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk( output, "IHDR"u8, header );

        using ( var raw = new MemoryStream() )
        {
            using ( var zlib = new ZLibStream( raw, CompressionLevel.Optimal, true ) )
            {
                var line = new byte[1 + pixmap.Width * 4];

                for ( var y = 0; y < pixmap.Height; y++ )
                {
                    var row = pixmap.Row( y );

                    for ( var x = 0; x < row.Length; x++ )
                    {
                        var p = row[x];
                        var o = 1 + x * 4;
                        line[o] = ( byte )( p >> 16 );
                        line[o + 1] = ( byte )( p >> 8 );
                        line[o + 2] = ( byte )p;
                        line[o + 3] = ( byte )( p >> 24 );
                    }

                    zlib.Write( line );
                }
            }

            WriteChunk( output, "IDAT"u8, raw.GetBuffer().AsSpan( 0, ( int )raw.Length ) );
        }

        WriteChunk( output, "IEND"u8, [] );
    }

    private static void WriteChunk( Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data )
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian( word, data.Length );
        output.Write( word );
        output.Write( type );
        output.Write( data );

        var crc = Crc( 0xFFFF_FFFFu, type );
        crc = Crc( crc, data ) ^ 0xFFFF_FFFFu;
        BinaryPrimitives.WriteUInt32BigEndian( word, crc );
        output.Write( word );
    }

    private static uint Crc( uint crc, ReadOnlySpan<byte> data )
    {
        foreach ( var b in data )
        {
            crc = CrcTable[( crc ^ b ) & 0xFF] ^ ( crc >> 8 );
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for ( var n = 0u; n < 256; n++ )
        {
            var c = n;

            for ( var k = 0; k < 8; k++ )
            {
                c = ( c & 1 ) != 0 ? 0xEDB8_8320u ^ ( c >> 1 ) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

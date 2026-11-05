using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UODataAccess.IO;

public enum UOPCompression : short
{
    None = 0,
    Zlib = 1,
    ZlibBWT = 3
}

/// <summary>
///     One file inside a UOP container. <see cref="Offset" /> already skips the per-entry header of
///     <see cref="HeaderLength" /> bytes; <see cref="Checksum" /> is the table's 32-bit Adler32 field.
/// </summary>
public readonly record struct UOPEntry(
    long Offset,
    int CompressedLength,
    int DecompressedLength,
    ulong Hash,
    UOPCompression Compression,
    int HeaderLength = 0,
    uint Checksum = 0 );

/// <summary>
///     Mythic's UOP container ("MYP\0"). Entries are addressed by <see cref="UOPHash" /> of a
///     pseudo-path such as <c>build/artlegacymul/00001234.tga</c>.
/// </summary>
public sealed class UOPArchive : IDisposable
{
    private const uint Magic = 0x0050_594D;

    private readonly Dictionary<ulong, UOPEntry> _entries = new();

    private UOPArchive( MappedFile file )
    {
        File = file;
        ParseDirectory();
    }

    public MappedFile File { get; }

    /// <summary>Container version (4 or 5 in shipped clients).</summary>
    public int Version { get; private set; }

    /// <summary>Entries per directory block declared in the header (100 or 1000 in shipped clients).</summary>
    public int BlockCapacity { get; private set; }

    public IReadOnlyDictionary<ulong, UOPEntry> Entries => _entries;

    public int Count => _entries.Count;

    public void Dispose() => File.Dispose();

    public static UOPArchive Open( string path ) => new( MappedFile.Open( path ) );

    public bool TryGet( ulong hash, out UOPEntry entry ) => _entries.TryGetValue( hash, out entry );

    public bool TryGet( ReadOnlySpan<char> name, out UOPEntry entry ) => _entries.TryGetValue( UOPHash.Compute( name ), out entry );

    /// <summary>Looks up <paramref name="format" /> with <paramref name="id" /> substituted, without allocating.</summary>
    public bool TryGet( CompositeFormat format, int id, out UOPEntry entry ) => TryGet( Hash( format, id ), out entry );

    public static ulong Hash( CompositeFormat format, int id )
    {
        Span<char> buffer = stackalloc char[256];
        return buffer.TryWrite( null, format, out var written, id )
            ? UOPHash.Compute( buffer[..written] )
            : UOPHash.Compute( string.Format( null, format, id ) );
    }

    /// <summary>The opaque per-entry header in front of the payload (often empty).</summary>
    public ReadOnlySpan<byte> ReadHeader( in UOPEntry entry ) => File.Slice( entry.Offset - entry.HeaderLength, entry.HeaderLength );

    /// <summary>Raw (still compressed) bytes of an entry.</summary>
    public ReadOnlySpan<byte> ReadRaw( in UOPEntry entry ) => File.Slice( entry.Offset, entry.CompressedLength );

    /// <summary>Decompressed bytes of an entry.</summary>
    public ReadOnlySpan<byte> Read( in UOPEntry entry ) => Decompress( ReadRaw( entry ), entry.Compression, entry.DecompressedLength );

    public static ReadOnlySpan<byte> Decompress( ReadOnlySpan<byte> raw, UOPCompression compression, int decompressedLength ) => compression switch
    {
        UOPCompression.None => raw,
        UOPCompression.Zlib => Inflate( raw, decompressedLength ),
        UOPCompression.ZlibBWT => BWTDecompress.Decompress( Inflate( raw, decompressedLength ) ),
        _ => throw new InvalidDataException( $"Unknown UOP compression: {compression}" )
    };

    internal static unsafe byte[] Inflate( ReadOnlySpan<byte> compressed, int decompressedLength )
    {
        var output = new byte[decompressedLength];

        fixed ( byte* ptr = compressed )
        {
            using var source = new UnmanagedMemoryStream( ptr, compressed.Length );
            using var zlib = new ZLibStream( source, CompressionMode.Decompress );
            zlib.ReadAtLeast( output, decompressedLength, false );
        }

        return output;
    }

    private void ParseDirectory()
    {
        var data = File.Span;

        if ( data.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian( data ) != Magic )
        {
            throw new InvalidDataException( $"Not a UOP file: {File.Path}" );
        }

        // magic, version, signature, first block offset, block capacity, file count.
        Version = BinaryPrimitives.ReadInt32LittleEndian( data[4..] );
        var nextBlock = BinaryPrimitives.ReadInt64LittleEndian( data[12..] );
        BlockCapacity = BinaryPrimitives.ReadInt32LittleEndian( data[20..] );

        while ( nextBlock > 0 && nextBlock < data.Length )
        {
            var block = data[( int )nextBlock..];
            var filesInBlock = BinaryPrimitives.ReadInt32LittleEndian( block );
            nextBlock = BinaryPrimitives.ReadInt64LittleEndian( block[4..] );

            // 34 bytes per record: offset, header length, compressed, decompressed, hash, adler32, flag.
            var records = block[12..];

            for ( var i = 0; i < filesInBlock; i++ )
            {
                var r = records.Slice( i * 34, 34 );
                var offset = BinaryPrimitives.ReadInt64LittleEndian( r );

                if ( offset == 0 )
                {
                    continue;
                }

                var headerLength = BinaryPrimitives.ReadInt32LittleEndian( r[8..] );
                var compressed = BinaryPrimitives.ReadInt32LittleEndian( r[12..] );
                var decompressed = BinaryPrimitives.ReadInt32LittleEndian( r[16..] );
                var hash = BinaryPrimitives.ReadUInt64LittleEndian( r[20..] );
                var checksum = BinaryPrimitives.ReadUInt32LittleEndian( r[28..] );
                var flag = ( UOPCompression )BinaryPrimitives.ReadInt16LittleEndian( r[32..] );

                _entries[hash] = new UOPEntry( offset + headerLength, compressed, decompressed, hash, flag, headerLength, checksum );
            }
        }
    }

}

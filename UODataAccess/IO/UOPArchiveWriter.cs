using System.Buffers.Binary;

namespace UODataAccess.IO;

/// <summary>
///     Streams a Mythic UOP container ("MYP\0"). Entries are appended one at a time; each directory
///     block's table is reserved up front and back-patched when the block fills or on
///     <see cref="Complete" />, so payloads are never buffered. The stream must be seekable.
/// </summary>
/// <remarks>
///     Shipped files come in two shapes (see UOFiddler's <c>LegacyMulFileConverter</c>): version 4 with
///     100-entry blocks starting at 0x28 (gumpart from 7.0.114, sound, MultiCollection, tileart) and
///     version 5 with 1000-entry blocks starting at 0x200 (art, maps). Clients only use the hashes,
///     offsets, lengths and compression flags, so either shape works for any content.
/// </remarks>
public sealed class UOPArchiveWriter : IDisposable
{
    private const uint Magic = 0x0050_594D;
    private const uint Signature = 0xFD23EC43;
    private const int HeaderSize = 0x28;
    private const int RecordSize = 34;

    private readonly bool _leaveOpen;
    private readonly List<Record> _block = [];
    private readonly Stream _stream;
    private bool _completed;
    private long _blockStart = -1;

    public UOPArchiveWriter( Stream stream, int version = 5, int blockCapacity = 1000, bool leaveOpen = false )
    {
        if ( !stream.CanSeek || !stream.CanWrite )
        {
            throw new ArgumentException( "UOP output must be a seekable, writable stream.", nameof( stream ) );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan( blockCapacity, 1 );

        _stream = stream;
        _leaveOpen = leaveOpen;
        Version = version;
        BlockCapacity = blockCapacity;

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian( header, Magic );
        BinaryPrimitives.WriteInt32LittleEndian( header[4..], version );
        BinaryPrimitives.WriteUInt32LittleEndian( header[8..], Signature );
        BinaryPrimitives.WriteInt64LittleEndian( header[12..], FirstBlockOffset );
        BinaryPrimitives.WriteInt32LittleEndian( header[20..], blockCapacity );
        _stream.Write( header );

        // Version 5 files leave a gap before the first block; keep that shape.
        for ( var i = HeaderSize; i < FirstBlockOffset; i++ )
        {
            _stream.WriteByte( 0 );
        }
    }

    public int Version { get; }

    public int BlockCapacity { get; }

    public int Count { get; private set; }

    private long FirstBlockOffset => Version >= 5 ? 0x200 : HeaderSize;

    public void Dispose()
    {
        if ( !_completed )
        {
            Complete();
        }

        if ( !_leaveOpen )
        {
            _stream.Dispose();
        }
    }

    /// <summary>Opens <paramref name="path" /> for writing, matching the shape of an existing archive if given.</summary>
    public static UOPArchiveWriter Create( string path, UOPArchive? shapeLike = null ) =>
        new( File.Create( path ), shapeLike?.Version ?? 5, shapeLike?.BlockCapacity is > 0 and var c ? c : 1000 );

    public void Add( string name, ReadOnlySpan<byte> data, UOPCompression compression = UOPCompression.None, int decompressedLength = -1 ) =>
        Add( UOPHash.Compute( name ), [], data, decompressedLength < 0 ? data.Length : decompressedLength, compression );

    /// <summary>
    ///     Appends an entry. <paramref name="data" /> is stored as-is (already compressed when
    ///     <paramref name="compression" /> says so). <paramref name="checksum" /> defaults to the
    ///     Adler32 of the payload, which is what non-Mythic writers store and what clients ignore.
    /// </summary>
    public void Add( ulong hash, ReadOnlySpan<byte> header, ReadOnlySpan<byte> data, int decompressedLength, UOPCompression compression, uint? checksum = null )
    {
        ObjectDisposedException.ThrowIf( _completed, this );

        if ( _blockStart < 0 || _block.Count == BlockCapacity )
        {
            StartBlock();
        }

        var offset = _stream.Position;
        _stream.Write( header );
        _stream.Write( data );

        _block.Add( new Record( offset, header.Length, data.Length, decompressedLength, hash, checksum ?? Adler32( data ), ( short )compression ) );
        Count++;
    }

    /// <summary>Copies an entry verbatim (header, still-compressed payload, flags) from another archive.</summary>
    public void CopyFrom( UOPArchive source, in UOPEntry entry ) =>
        Add( entry.Hash, source.ReadHeader( entry ), source.ReadRaw( entry ), entry.DecompressedLength, entry.Compression, entry.Checksum );

    /// <summary>Writes the last block table and the file count. Called by <see cref="Dispose" /> if needed.</summary>
    public void Complete()
    {
        if ( _completed )
        {
            return;
        }

        if ( _blockStart < 0 )
        {
            StartBlock(); // An empty archive still has one (empty) block.
        }

        FlushBlock();

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian( count, Count );
        _stream.Position = 24;
        _stream.Write( count );
        _stream.Seek( 0, SeekOrigin.End );
        _stream.Flush();
        _completed = true;
    }

    public static uint Adler32( ReadOnlySpan<byte> data )
    {
        const uint Mod = 65521;
        const int Chunk = 5552; // largest n with 255n(n+1)/2 + (n+1)(Mod-1) < 2^32
        uint a = 1, b = 0;

        while ( data.Length > 0 )
        {
            var n = Math.Min( Chunk, data.Length );

            foreach ( var t in data[..n] )
            {
                a += t;
                b += a;
            }

            a %= Mod;
            b %= Mod;
            data = data[n..];
        }

        return ( b << 16 ) | a;
    }

    private void StartBlock()
    {
        if ( _blockStart >= 0 )
        {
            FlushBlock();

            // Link the block we just flushed to the one starting here.
            var start = _stream.Position;
            Span<byte> next = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian( next, start );
            _stream.Position = _blockStart + 4;
            _stream.Write( next );
            _stream.Position = start;
        }

        _blockStart = _stream.Position;
        _block.Clear();

        // Count, next-block pointer, then the reserved table.
        var reserved = 12 + BlockCapacity * RecordSize;
        _stream.Write( new byte[reserved] );
    }

    private void FlushBlock()
    {
        var table = new byte[12 + BlockCapacity * RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian( table, _block.Count );

        for ( var i = 0; i < _block.Count; i++ )
        {
            var r = _block[i];
            var span = table.AsSpan( 12 + i * RecordSize, RecordSize );
            BinaryPrimitives.WriteInt64LittleEndian( span, r.Offset );
            BinaryPrimitives.WriteInt32LittleEndian( span[8..], r.HeaderLength );
            BinaryPrimitives.WriteInt32LittleEndian( span[12..], r.CompressedLength );
            BinaryPrimitives.WriteInt32LittleEndian( span[16..], r.DecompressedLength );
            BinaryPrimitives.WriteUInt64LittleEndian( span[20..], r.Hash );
            BinaryPrimitives.WriteUInt32LittleEndian( span[28..], r.Checksum );
            BinaryPrimitives.WriteInt16LittleEndian( span[32..], r.Compression );
        }

        var end = _stream.Position;

        // Keep the next-block pointer that StartBlock may already have written.
        _stream.Position = _blockStart;
        _stream.Write( table.AsSpan( 0, 4 ) );
        _stream.Position = _blockStart + 12;
        _stream.Write( table.AsSpan( 12 ) );
        _stream.Position = end;
    }

    private readonly record struct Record( long Offset, int HeaderLength, int CompressedLength, int DecompressedLength, ulong Hash, uint Checksum, short Compression );
}

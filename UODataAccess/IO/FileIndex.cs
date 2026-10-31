using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace UODataAccess.IO;

/// <summary>
///     Where an id's payload lives. <see cref="Extra" /> is the MUL idx "extra" field; for UOP
///     archives that carry a width/height preamble (gumps) it is packed the same way as
///     <c>gumpidx.mul</c>: <c>(width &lt;&lt; 16) | height</c>.
/// </summary>
[StructLayout( LayoutKind.Auto )]
public readonly record struct IndexEntry(
    int Source,
    long Offset,
    int Length,
    int DecompressedLength,
    UOPCompression Compression,
    int Extra )
{
    public bool IsValid => Length > 0 && Offset >= 0;

    public int ExtraHigh => ( Extra >> 16 ) & 0xFFFF;

    public int ExtraLow => Extra & 0xFFFF;

    /// <summary>True when the payload came from verdata.mul rather than the main file.</summary>
    public bool IsPatched => Source > 0;
}

/// <summary>
///     Id-addressed view over either a legacy <c>xxx.mul</c> + <c>xxxidx.mul</c> pair or a
///     <c>xxxLegacyMUL.uop</c> container, so loaders never care which one the client ships.
///     UOP is preferred when both exist, matching the official client.
/// </summary>
public sealed class FileIndex : IDisposable
{
    private readonly IndexEntry[] _entries;
    private readonly List<MappedFile> _sources = [];

    private FileIndex( IndexEntry[] entries, MappedFile data, bool isUOP )
    {
        _entries = entries;
        _sources.Add( data );
        IsUOP = isUOP;
    }

    public bool IsUOP { get; }

    public string Source => IsUOP ? "UOP" : "MUL";

    public string DataPath => _sources[0].Path;

    public int Length => _entries.Length;

    public void Dispose()
    {
        foreach ( var source in _sources )
        {
            source.Dispose();
        }

        _sources.Clear();
    }

    /// <summary>
    ///     Opens the best available form of an indexed file.
    /// </summary>
    /// <param name="files">Client folder.</param>
    /// <param name="idxName">Legacy index, e.g. <c>artidx.mul</c>.</param>
    /// <param name="mulName">Legacy data, e.g. <c>art.mul</c>.</param>
    /// <param name="uopName">UOP container, e.g. <c>artLegacyMUL.uop</c>, or null if none exists for this asset.</param>
    /// <param name="uopPattern">Composite format for entry names, e.g. <c>build/artlegacymul/{0:D8}.tga</c>.</param>
    /// <param name="length">Minimum number of ids to expose.</param>
    /// <param name="uopHasExtra">UOP entries start with an 8-byte (width, height) preamble (gumps).</param>
    public static FileIndex? Open(
        UOFileSet files,
        string idxName,
        string mulName,
        string? uopName = null,
        string? uopPattern = null,
        int length = 0,
        bool uopHasExtra = false )
    {
        if ( uopName is not null && uopPattern is not null && files.Find( uopName ) is { } uopPath )
        {
            return OpenUOP( uopPath, uopPattern, length, uopHasExtra );
        }

        if ( files.Find( idxName ) is { } idxPath && files.Find( mulName ) is { } mulPath )
        {
            return OpenMUL( idxPath, mulPath, length );
        }

        return null;
    }

    public static FileIndex OpenMUL( string idxPath, string mulPath, int length = 0 )
    {
        var idx = File.ReadAllBytes( idxPath );
        var count = Math.Max( idx.Length / 12, length );
        var entries = new IndexEntry[count];
        var records = MemoryMarshal.Cast<byte, MULIndexRecord>( idx.AsSpan( 0, idx.Length / 12 * 12 ) );

        for ( var i = 0; i < records.Length; i++ )
        {
            ref readonly var r = ref records[i];
            var offset = r.Lookup == -1 ? -1 : ( long )( uint )r.Lookup;
            entries[i] = new IndexEntry( 0, offset, r.Length, r.Length, UOPCompression.None, r.Extra );
        }

        for ( var i = records.Length; i < count; i++ )
        {
            entries[i] = new IndexEntry( 0, -1, 0, 0, UOPCompression.None, 0 );
        }

        return new FileIndex( entries, MappedFile.Open( mulPath ), false );
    }

    public static FileIndex OpenUOP( string uopPath, string pattern, int length = 0, bool hasExtra = false )
    {
        var archive = UOPArchive.Open( uopPath );
        var format = CompositeFormat.Parse( pattern );

        // Ids are not stored, only hashes of their names, so probe each candidate id.
        var count = Math.Max( length, archive.Count );
        var entries = new IndexEntry[count];
        var max = -1;

        for ( var id = 0; id < count; id++ )
        {
            if ( !archive.TryGet( format, id, out var e ) )
            {
                entries[id] = new IndexEntry( 0, -1, 0, 0, UOPCompression.None, 0 );
                continue;
            }

            var offset = e.Offset;
            var compressed = e.CompressedLength;
            var extra = 0;

            if ( hasExtra && e.Compression != UOPCompression.ZlibBWT )
            {
                var preamble = archive.File.Slice( offset, 8 );
                extra = ( BinaryPrimitives.ReadInt32LittleEndian( preamble ) << 16 ) | ( BinaryPrimitives.ReadInt32LittleEndian( preamble[4..] ) & 0xFFFF );
                offset += 8;
                compressed -= 8;
            }

            entries[id] = new IndexEntry( 0, offset, compressed, e.DecompressedLength - ( hasExtra && e.Compression == UOPCompression.None ? 8 : 0 ), e.Compression, extra );
            max = id;
        }

        if ( length == 0 && max + 1 < count )
        {
            Array.Resize( ref entries, max + 1 );
        }

        // The archive owns the mapped file; FileIndex disposes it via _sources.
        return new FileIndex( entries, archive.File, true );
    }

    public bool IsValid( int id ) => ( uint )id < ( uint )_entries.Length && _entries[id].IsValid;

    public IndexEntry GetEntry( int id ) => ( uint )id < ( uint )_entries.Length ? _entries[id] : default;

    public bool TryRead( int id, out ReadOnlySpan<byte> data ) => TryRead( id, out data, out _ );

    /// <summary>Reads (and if needed decompresses) the payload for <paramref name="id" />.</summary>
    public bool TryRead( int id, out ReadOnlySpan<byte> data, out IndexEntry entry )
    {
        entry = GetEntry( id );

        if ( !entry.IsValid )
        {
            data = [];
            return false;
        }

        var raw = _sources[entry.Source].Slice( entry.Offset, entry.Length );
        data = entry.Compression == UOPCompression.None ? raw : UOPArchive.Decompress( raw, entry.Compression, entry.DecompressedLength );
        return true;
    }

    /// <summary>Registers an extra data file (verdata.mul) that patched entries can point into.</summary>
    public int AddSource( MappedFile file )
    {
        _sources.Add( file );
        return _sources.Count - 1;
    }

    /// <summary>Overrides an entry with a patch from <paramref name="source" />.</summary>
    public void Patch( int id, int source, long offset, int length, int extra )
    {
        if ( ( uint )id < ( uint )_entries.Length )
        {
            _entries[id] = new IndexEntry( source, offset, length, length, UOPCompression.None, extra );
        }
    }

    [StructLayout( LayoutKind.Sequential, Pack = 1 )]
    private readonly struct MULIndexRecord
    {
        public readonly int Lookup;
        public readonly int Length;
        public readonly int Extra;
    }
}

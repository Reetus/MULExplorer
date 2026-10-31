using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>Where a multi's components were read from.</summary>
public enum MultiSource
{
    MUL,
    UOP
}

/// <summary>
///     One component of a multi. <see cref="Flags" /> and <see cref="Unk1" /> use the multi.mul
///     convention for both sources (UOP flags are translated the way UOFiddler does: bit 0x0001
///     inverted into <see cref="Flags" />, bit 0x0100 into <see cref="Unk1" />); <see cref="RawFlags" />
///     keeps the value as stored.
/// </summary>
public readonly record struct MultiTile( ushort ItemId, short X, short Y, short Z, int Flags, int Unk1, int RawFlags, bool IsVisible );

/// <summary>
///     The components of one multi in file order (the client draws them in that order where they
///     tie), plus the bounding box UOFiddler reports.
/// </summary>
public sealed class MultiComponents
{
    public MultiComponents( int id, MultiSource source, MultiTile[] tiles )
    {
        Id = id;
        Source = source;
        Tiles = tiles;

        // Like UOFiddler, the box always contains the anchor (0, 0) and MaxHeight starts at 0.
        int minX = 0, minY = 0, maxX = 0, maxY = 0, maxZ = 0;

        foreach ( var t in tiles )
        {
            minX = Math.Min( minX, t.X );
            minY = Math.Min( minY, t.Y );
            maxX = Math.Max( maxX, t.X );
            maxY = Math.Max( maxY, t.Y );
            maxZ = Math.Max( maxZ, t.Z );
        }

        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        MaxHeight = maxZ;

        var cells = new HashSet<(short, short)>();

        foreach ( var t in tiles )
        {
            cells.Add( ( t.X, t.Y ) );
        }

        Surface = cells.Count;
    }

    public int Id { get; }

    public MultiSource Source { get; }

    public MultiTile[] Tiles { get; }

    public int MinX { get; }

    public int MinY { get; }

    public int MaxX { get; }

    public int MaxY { get; }

    public int Width => MaxX - MinX + 1;

    public int Height => MaxY - MinY + 1;

    /// <summary>Highest component z (at least 0).</summary>
    public int MaxHeight { get; }

    /// <summary>Number of (x, y) cells that hold at least one component.</summary>
    public int Surface { get; }
}

/// <summary>
///     Multis (houses, boats, decorations) from <c>MultiCollection.uop</c> and/or
///     <c>multi.idx</c>/<c>multi.mul</c>. Both are opened when present; <see cref="Get(int)" />
///     prefers UOP like the client.
/// </summary>
/// <remarks>
///     Layouts (ClassicUO <c>MultiLoader</c>):
///     <list type="bullet">
///         <item>multi.mul: records of <c>u16 id, i16 x, i16 y, i16 z, u32 flags</c> (12 bytes), plus a
///         <c>u32</c> extra since 7.0.9 (16 bytes). Visible when flags != 0.</item>
///         <item>UOP <c>build/multicollection/{id:D6}.bin</c>: <c>u32 id, i32 count</c>, then per
///         component <c>u16 id, i16 x, i16 y, i16 z, u16 flags, u32 n, n x u32 cliloc</c>.
///         Visible when flags is 0 or 0x100.</item>
///     </list>
/// </remarks>
public sealed class Multis : IDisposable
{
    /// <summary>Upper bound for multi ids (UOFiddler's; the shipped UOP has ids up to 9000).</summary>
    public const int MaximumMultiIndex = 0x2710;

    private const string UOPPattern = "build/multicollection/{0:D6}.bin";
    private const string HousingBinName = "build/multicollection/housing.bin";

    private readonly FileIndex? _mul;
    private readonly UOPArchive? _uop;
    private readonly UOPEntry?[] _uopEntries = [];
    private readonly MultiComponents?[] _mulCache = new MultiComponents?[MaximumMultiIndex];
    private readonly MultiComponents?[] _uopCache = new MultiComponents?[MaximumMultiIndex];

    public Multis( UOFileSet files )
    {
        if ( files.Find( "MultiCollection.uop" ) is { } uopPath )
        {
            _uop = UOPArchive.Open( uopPath );
            _uopEntries = new UOPEntry?[MaximumMultiIndex];
            var format = CompositeFormat.Parse( UOPPattern );

            for ( var id = 0; id < MaximumMultiIndex; id++ )
            {
                if ( _uop.TryGet( format, id, out var entry ) && entry.DecompressedLength > 8 )
                {
                    _uopEntries[id] = entry;
                }
            }

            HasHousingBin = _uop.TryGet( HousingBinName, out _ );
        }

        _mul = FileIndex.Open( files, "multi.idx", "multi.mul" );

        if ( _mul is not null )
        {
            var lengths = new List<int>();

            for ( var id = 0; id < _mul.Length; id++ )
            {
                if ( _mul.GetEntry( id ) is { IsValid: true } e )
                {
                    lengths.Add( e.Length );
                }
            }

            MULRecordSize = DetectRecordSize( lengths, IsHighSeasArt( files ) );
        }

        if ( _mul is null && _uop is null )
        {
            throw new FileNotFoundException( "No multis found (MultiCollection.uop or multi.idx/multi.mul)." );
        }
    }

    public bool HasUOP => _uop is not null;

    public bool HasMUL => _mul is not null;

    /// <summary>MultiCollection.uop also carries the custom-housing catalogue (housing.bin), which is not a multi.</summary>
    public bool HasHousingBin { get; }

    /// <summary>The source <see cref="Get(int)" /> uses: UOP when present.</summary>
    public MultiSource DefaultSource => HasUOP ? MultiSource.UOP : MultiSource.MUL;

    public IReadOnlyList<MultiSource> Sources => HasUOP && HasMUL ? [MultiSource.UOP, MultiSource.MUL] : [DefaultSource];

    /// <summary>12 (pre High Seas) or 16 bytes per multi.mul component; 0 without multi.mul.</summary>
    public int MULRecordSize { get; }

    public void Dispose()
    {
        _mul?.Dispose();
        _uop?.Dispose();
    }

    public bool Has( MultiSource source ) => source == MultiSource.UOP ? HasUOP : HasMUL;

    public bool IsValid( int id ) => IsValid( id, DefaultSource );

    public bool IsValid( int id, MultiSource source ) =>
        ( uint )id < MaximumMultiIndex && ( source == MultiSource.UOP ? _uop is not null && _uopEntries[id] is not null : _mul is not null && _mul.IsValid( id ) );

    /// <summary>Ids that have components in <paramref name="source" />, ascending.</summary>
    public List<int> GetIds( MultiSource source )
    {
        var list = new List<int>();

        for ( var id = 0; id < MaximumMultiIndex; id++ )
        {
            if ( IsValid( id, source ) )
            {
                list.Add( id );
            }
        }

        return list;
    }

    public MultiComponents? Get( int id ) => Get( id, DefaultSource );

    public MultiComponents? Get( int id, MultiSource source )
    {
        if ( !IsValid( id, source ) )
        {
            return null;
        }

        var cache = source == MultiSource.UOP ? _uopCache : _mulCache;

        if ( Volatile.Read( ref cache[id] ) is { } cached )
        {
            return cached;
        }

        MultiTile[] tiles;

        if ( source == MultiSource.UOP )
        {
            var entry = _uopEntries[id]!.Value;
            tiles = ParseUOP( _uop!.Read( entry ) );
        }
        else
        {
            tiles = _mul!.TryRead( id, out var data ) ? ParseMUL( data, MULRecordSize ) : [];
        }

        if ( tiles.Length == 0 )
        {
            return null;
        }

        var multi = new MultiComponents( id, source, tiles );
        Volatile.Write( ref cache[id], multi );
        return multi;
    }

    /// <summary>Parses multi.mul components (<paramref name="recordSize" /> 12 or 16).</summary>
    public static MultiTile[] ParseMUL( ReadOnlySpan<byte> data, int recordSize )
    {
        var count = data.Length / recordSize;
        var tiles = new MultiTile[count];

        for ( var i = 0; i < count; i++ )
        {
            var r = data.Slice( i * recordSize, recordSize );
            var flags = BinaryPrimitives.ReadInt32LittleEndian( r[8..] );
            var unk1 = recordSize >= 16 ? BinaryPrimitives.ReadInt32LittleEndian( r[12..] ) : 0;

            tiles[i] = new MultiTile(
                BinaryPrimitives.ReadUInt16LittleEndian( r ),
                BinaryPrimitives.ReadInt16LittleEndian( r[2..] ),
                BinaryPrimitives.ReadInt16LittleEndian( r[4..] ),
                BinaryPrimitives.ReadInt16LittleEndian( r[6..] ),
                flags,
                unk1,
                flags,
                flags != 0 );
        }

        return tiles;
    }

    /// <summary>Parses a decompressed MultiCollection.uop entry.</summary>
    public static MultiTile[] ParseUOP( ReadOnlySpan<byte> data )
    {
        if ( data.Length < 8 )
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian( data[4..] );

        if ( count <= 0 )
        {
            return [];
        }

        var tiles = new List<MultiTile>( Math.Min( count, data.Length / 14 ) );
        var pos = 8;

        for ( var i = 0; i < count && pos + 14 <= data.Length; i++ )
        {
            var r = data[pos..];
            var raw = BinaryPrimitives.ReadUInt16LittleEndian( r[8..] );
            var clilocs = BinaryPrimitives.ReadInt32LittleEndian( r[10..] );

            tiles.Add( new MultiTile(
                BinaryPrimitives.ReadUInt16LittleEndian( r ),
                BinaryPrimitives.ReadInt16LittleEndian( r[2..] ),
                BinaryPrimitives.ReadInt16LittleEndian( r[4..] ),
                BinaryPrimitives.ReadInt16LittleEndian( r[6..] ),
                ( raw & 0x0001 ) != 0 ? 0 : 1,
                ( raw & 0x0100 ) != 0 ? 1 : 0,
                raw,
                raw is 0 or 0x100 ) );

            pos += 14 + Math.Max( 0, clilocs ) * 4;
        }

        return tiles.ToArray();
    }

    /// <summary>
    ///     Component record size of a multi.mul: whichever of 12/16 divides every entry; when both
    ///     (or neither) fit, the High Seas hint decides (ClassicUO: client 7.0.9+, UOFiddler: art count).
    /// </summary>
    public static int DetectRecordSize( IEnumerable<int> entryLengths, bool highSeasHint )
    {
        bool fits12 = true, fits16 = true;

        foreach ( var length in entryLengths )
        {
            fits12 &= length % 12 == 0;
            fits16 &= length % 16 == 0;
        }

        return fits12 == fits16 ? highSeasHint ? 16 : 12 : fits16 ? 16 : 12;
    }

    /// <summary>UOFiddler's <c>Art.IsUOAHS</c>: High Seas added art beyond 0x13FDC entries; UOP clients are all newer.</summary>
    private static bool IsHighSeasArt( UOFileSet files )
    {
        if ( files.Exists( "artLegacyMUL.uop" ) )
        {
            return true;
        }

        return files.Find( "artidx.mul" ) is { } idx && new FileInfo( idx ).Length / 12 >= 0x13FDC;
    }
}

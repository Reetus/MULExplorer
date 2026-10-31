using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     One facet: land blocks (8x8 cells of <c>(u16 tile, s8 z)</c> behind a 4-byte header,
///     196 bytes each, addressed column-major <c>bx * BlockHeight + by</c>) and statics
///     (<c>staidx</c> 12-byte records into 7-byte <c>statics</c> records). UOP map files are split
///     into chunks of 4096 blocks (<c>build/map{N}legacymul/{chunk:D8}.dat</c>), read in place.
///     Everything is a span over memory-mapped files; the only allocations are the render cache.
/// </summary>
public sealed class FacetMap : IDisposable
{
    public const int BlockSize = 8;
    public const int BlockBytes = 4 + 64 * 3;
    public const int UOPChunkBlocks = 4096;

    private const int RegionShift = 5; // 32x32 blocks = 256x256 tiles per cache region
    private const int RegionBlocks = 1 << RegionShift;

    private static readonly LandTile[] EmptyLand = new LandTile[64];

    private readonly Maps _owner;
    private readonly MappedFile _map;
    private readonly long[]? _chunkOffsets;
    private readonly int[]? _chunkLengths;
    private readonly UOPEntry[]? _chunkEntries;
    private readonly byte[]?[]? _chunkBuffers;
    private readonly MappedFile? _staidx;
    private readonly MappedFile? _statics;

    private readonly Dictionary<int, int>? _landPatches;
    private readonly MappedFile? _mapDif;
    private readonly Dictionary<int, (long Offset, int Length)>? _staticPatches;
    private readonly MappedFile? _staDif;

    private readonly RenderCache?[] _caches = new RenderCache?[4];

    internal FacetMap(
        Maps owner,
        int index,
        int fileIndex,
        bool alternate,
        (int Width, int Height) size,
        string mapPath,
        string? staidxPath,
        string? staticsPath,
        MapPatchFiles? patches )
    {
        _owner = owner;
        Index = index;
        FileIndex = fileIndex;
        IsAlternate = alternate;
        MapPath = mapPath;

        long dataLength;

        if ( mapPath.EndsWith( ".uop", StringComparison.OrdinalIgnoreCase ) )
        {
            var archive = UOPArchive.Open( mapPath );
            _map = archive.File;
            var format = CompositeFormat.Parse( $"build/map{fileIndex}legacymul/{{0:D8}}.dat" );
            var entries = new List<UOPEntry>();

            while ( archive.TryGet( format, entries.Count, out var entry ) )
            {
                entries.Add( entry );
            }

            _chunkEntries = entries.ToArray();
            _chunkOffsets = entries.Select( e => e.Offset ).ToArray();
            _chunkLengths = entries.Select( e => e.DecompressedLength ).ToArray();
            _chunkBuffers = new byte[]?[entries.Count];
            dataLength = _chunkLengths.Sum( l => ( long )l );
            IsUOP = true;
        }
        else
        {
            _map = MappedFile.Open( mapPath );
            dataLength = _map.Length;
        }

        var (width, height) = size;

        // ClassicUO/UOFiddler: map0/1 with the pre-Mondain's Legacy block count are 6144 wide.
        if ( index <= 1 && dataLength / BlockBytes <= 6144 / 8 * ( 4096 / 8 ) )
        {
            width = 6144;
        }

        Width = width;
        Height = height;
        BlockWidth = width >> 3;
        BlockHeight = height >> 3;

        _staidx = staidxPath is null ? null : MappedFile.Open( staidxPath );
        _statics = staticsPath is null || _staidx is null ? null : MappedFile.Open( staticsPath );

        if ( patches is not null )
        {
            LoadPatches( patches, ref _landPatches, ref _mapDif, ref _staticPatches, ref _staDif );
        }
    }

    public int Index { get; }

    /// <summary>The N of the map files actually used (map1 falls back to map0).</summary>
    public int FileIndex { get; }

    public bool IsAlternate { get; }

    public bool IsUOP { get; }

    public string MapPath { get; }

    public string Name => Maps.GetName( Index );

    public int Width { get; }

    public int Height { get; }

    public int BlockWidth { get; }

    public int BlockHeight { get; }

    public bool HasStatics => _statics is not null;

    /// <summary>Number of land blocks replaced by mapdif.</summary>
    public int LandPatchCount => _landPatches?.Count ?? 0;

    /// <summary>Number of static blocks replaced by stadif.</summary>
    public int StaticPatchCount => _staticPatches?.Count ?? 0;

    public bool HasPatches => LandPatchCount + StaticPatchCount > 0;

    public void Dispose()
    {
        _map.Dispose();
        _staidx?.Dispose();
        _statics?.Dispose();
        _mapDif?.Dispose();
        _staDif?.Dispose();
    }

    public bool Contains( int x, int y ) => ( uint )x < ( uint )Width && ( uint )y < ( uint )Height;

    // ----- land -----

    /// <summary>The 64 cells of a block, row-major (<c>(y &amp; 7) * 8 + (x &amp; 7)</c>).</summary>
    public ReadOnlySpan<LandTile> GetLandBlock( int bx, int by, bool patched = false )
    {
        if ( ( uint )bx >= ( uint )BlockWidth || ( uint )by >= ( uint )BlockHeight )
        {
            return EmptyLand;
        }

        var block = bx * BlockHeight + by;
        ReadOnlySpan<byte> raw;

        if ( patched && _landPatches is not null && _landPatches.TryGetValue( block, out var patch ) )
        {
            raw = _mapDif!.Slice( ( long )patch * BlockBytes, BlockBytes );
        }
        else
        {
            raw = ReadMapBlock( block );
        }

        return raw.Length < BlockBytes ? EmptyLand : MemoryMarshal.Cast<byte, LandTile>( raw.Slice( 4, 64 * 3 ) );
    }

    public LandTile GetLand( int x, int y, bool patched = false ) =>
        Contains( x, y ) ? GetLandBlock( x >> 3, y >> 3, patched )[( ( y & 7 ) << 3 ) + ( x & 7 )] : default;

    public bool IsLandBlockPatched( int bx, int by ) =>
        _landPatches is not null && ( uint )bx < ( uint )BlockWidth && ( uint )by < ( uint )BlockHeight && _landPatches.ContainsKey( bx * BlockHeight + by );

    private ReadOnlySpan<byte> ReadMapBlock( int block )
    {
        if ( _chunkOffsets is null )
        {
            return _map.Slice( ( long )block * BlockBytes, BlockBytes );
        }

        var chunk = block / UOPChunkBlocks;

        if ( ( uint )chunk >= ( uint )_chunkOffsets.Length )
        {
            return [];
        }

        var offset = block % UOPChunkBlocks * BlockBytes;

        if ( offset + BlockBytes > _chunkLengths![chunk] )
        {
            return [];
        }

        ref readonly var entry = ref _chunkEntries![chunk];

        if ( entry.Compression == UOPCompression.None )
        {
            return _map.Slice( _chunkOffsets[chunk] + offset, BlockBytes );
        }

        // Shipped map UOPs are stored uncompressed; decompress once per chunk if one ever is not.
        var buffer = Volatile.Read( ref _chunkBuffers![chunk] );

        if ( buffer is null )
        {
            buffer = UOPArchive.Decompress( _map.Slice( entry.Offset, entry.CompressedLength ), entry.Compression, entry.DecompressedLength ).ToArray();
            Volatile.Write( ref _chunkBuffers[chunk], buffer );
        }

        return buffer.AsSpan( offset, BlockBytes );
    }

    // ----- statics -----

    /// <summary>All statics of a block, in file order. X/Y are 0..7 within the block.</summary>
    public ReadOnlySpan<StaticTile> GetStaticBlock( int bx, int by, bool patched = false )
    {
        if ( _statics is null || ( uint )bx >= ( uint )BlockWidth || ( uint )by >= ( uint )BlockHeight )
        {
            return [];
        }

        var block = bx * BlockHeight + by;
        ReadOnlySpan<byte> raw;

        if ( patched && _staticPatches is not null && _staticPatches.TryGetValue( block, out var patch ) )
        {
            raw = patch.Length > 0 ? _staDif!.Slice( patch.Offset, patch.Length ) : [];
        }
        else
        {
            var record = _staidx!.Slice( ( long )block * 12, 12 );

            if ( record.Length < 12 )
            {
                return [];
            }

            var lookup = BinaryPrimitives.ReadInt32LittleEndian( record );
            var length = BinaryPrimitives.ReadInt32LittleEndian( record[4..] );
            raw = lookup < 0 || length <= 0 ? [] : _statics.Slice( ( uint )lookup, length );
        }

        return MemoryMarshal.Cast<byte, StaticTile>( raw[..( raw.Length / 7 * 7 )] );
    }

    /// <summary>The statics standing on cell (x, y), in file order.</summary>
    public StaticTile[] GetStatics( int x, int y, bool patched = false )
    {
        if ( !Contains( x, y ) )
        {
            return [];
        }

        var block = GetStaticBlock( x >> 3, y >> 3, patched );
        var cx = x & 7;
        var cy = y & 7;
        var count = 0;

        foreach ( ref readonly var s in block )
        {
            if ( ( s.X & 7 ) == cx && ( s.Y & 7 ) == cy )
            {
                count++;
            }
        }

        if ( count == 0 )
        {
            return [];
        }

        var result = new StaticTile[count];
        count = 0;

        foreach ( ref readonly var s in block )
        {
            if ( ( s.X & 7 ) == cx && ( s.Y & 7 ) == cy )
            {
                result[count++] = s;
            }
        }

        return result;
    }

    public bool IsStaticBlockPatched( int bx, int by ) =>
        _staticPatches is not null && ( uint )bx < ( uint )BlockWidth && ( uint )by < ( uint )BlockHeight && _staticPatches.ContainsKey( bx * BlockHeight + by );

    // ----- radar rendering -----

    /// <summary>
    ///     Radar colors (ARGB1555) of one block, row-major, like UOFiddler's radar render: the
    ///     static with the highest top (z + tiledata height) wins over land unless the land is
    ///     higher; hued statics take their hue's color at the radar color's red channel.
    /// </summary>
    public void RenderBlock( int bx, int by, bool statics, bool patched, Span<ushort> colors )
    {
        var tables = _owner.Tables;
        var radar = tables.Radar;
        var land = GetLandBlock( bx, by, patched );

        if ( !statics )
        {
            for ( var i = 0; i < 64; i++ )
            {
                colors[i] = land[i].Id < radar.Length ? radar[land[i].Id] : ( ushort )0;
            }

            return;
        }

        Span<int> highTop = stackalloc int[64];
        Span<int> highZ = stackalloc int[64];
        Span<ushort> highId = stackalloc ushort[64];
        Span<ushort> highHue = stackalloc ushort[64];
        highTop.Fill( -255 );
        highZ.Fill( -255 );

        var heights = tables.Heights;

        foreach ( ref readonly var s in GetStaticBlock( bx, by, patched ) )
        {
            var cell = ( ( s.Y & 7 ) << 3 ) + ( s.X & 7 );
            int z = s.Z;
            var top = z + ( s.Id < heights.Length ? heights[s.Id] : 0 );

            if ( top > highTop[cell] || ( z > highZ[cell] && top >= highTop[cell] ) )
            {
                highTop[cell] = top;
                highZ[cell] = z;
                highId[cell] = s.Id;
                highHue[cell] = s.Hue;
            }
        }

        var hues = tables.HueColors;

        for ( var i = 0; i < 64; i++ )
        {
            if ( land[i].Z > highTop[i] )
            {
                colors[i] = land[i].Id < radar.Length ? radar[land[i].Id] : ( ushort )0;
                continue;
            }

            var itemIndex = RadarColors.LandCount + highId[i];
            var color = itemIndex < radar.Length ? radar[itemIndex] : ( ushort )0;
            var hue = ( highHue[i] & 0x3FFF ) - 1;

            if ( hue >= 0 && hues is not null && hue < hues.Length )
            {
                color = hues[hue][( color >> 10 ) & 0x1F];
            }

            colors[i] = color;
        }
    }

    /// <summary>Cached radar color (ARGB1555) of one cell; 0 outside the map.</summary>
    public ushort GetRadarColor( int x, int y, bool statics = true, bool patched = false ) =>
        Contains( x, y ) ? Cache( statics, patched ).Get( x, y ) : ( ushort )0;

    /// <summary>
    ///     Renders the radar view into <paramref name="target" />: pixel (px, py) shows cell
    ///     <c>(x + px * step, y + py * step)</c>, so <paramref name="step" /> &gt; 1 downsamples.
    ///     Cells outside the facet are opaque black. Blocks are rendered once and cached.
    /// </summary>
    public void RenderRadar( Pixmap target, int x, int y, int step = 1, bool statics = true, bool patched = false )
    {
        step = Math.Max( 1, step );
        var cache = Cache( statics, patched );

        for ( var py = 0; py < target.Height; py++ )
        {
            var row = target.Row( py );
            var ty = y + py * step;

            if ( ( uint )ty >= ( uint )Height )
            {
                row.Fill( 0xFF00_0000u );
                continue;
            }

            for ( var px = 0; px < row.Length; px++ )
            {
                var tx = x + px * step;
                row[px] = ( uint )tx < ( uint )Width ? Color16.ToArgb( cache.Get( tx, ty ) ) : 0xFF00_0000u;
            }
        }
    }

    /// <summary>Convenience: a new pixmap of the radar view of a tile rectangle.</summary>
    public Pixmap RenderRadar( int x, int y, int width, int height, int step = 1, bool statics = true, bool patched = false )
    {
        step = Math.Max( 1, step );
        var pixmap = new Pixmap( Math.Max( 1, ( width + step - 1 ) / step ), Math.Max( 1, ( height + step - 1 ) / step ) );
        RenderRadar( pixmap, x, y, step, statics, patched );
        return pixmap;
    }

    /// <summary>Drops cached radar blocks (all modes).</summary>
    public void ClearRenderCache() => Array.Clear( _caches );

    private RenderCache Cache( bool statics, bool patched )
    {
        var mode = ( statics ? 1 : 0 ) | ( patched && HasPatches ? 2 : 0 );
        var cache = Volatile.Read( ref _caches[mode] );

        if ( cache is null )
        {
            Interlocked.CompareExchange( ref _caches[mode], new RenderCache( this, statics, patched && HasPatches ), null );
            cache = _caches[mode]!;
        }

        return cache;
    }

    /// <summary>
    ///     Radar colors by 32x32-block region, allocated on first touch; each block is rendered
    ///     on demand. Lock-free: racing renders of one block write identical data.
    /// </summary>
    private sealed class RenderCache
    {
        private readonly FacetMap _map;
        private readonly bool _statics;
        private readonly bool _patched;
        private readonly int _regionsPerColumn;
        private readonly Region?[] _regions;

        public RenderCache( FacetMap map, bool statics, bool patched )
        {
            _map = map;
            _statics = statics;
            _patched = patched;
            _regionsPerColumn = ( map.BlockHeight + RegionBlocks - 1 ) >> RegionShift;
            _regions = new Region?[( ( map.BlockWidth + RegionBlocks - 1 ) >> RegionShift ) * _regionsPerColumn];
        }

        public ushort Get( int x, int y )
        {
            var bx = x >> 3;
            var by = y >> 3;
            var index = ( bx >> RegionShift ) * _regionsPerColumn + ( by >> RegionShift );
            var region = Volatile.Read( ref _regions[index] );

            if ( region is null )
            {
                Interlocked.CompareExchange( ref _regions[index], new Region(), null );
                region = _regions[index]!;
            }

            var local = ( ( bx & ( RegionBlocks - 1 ) ) << RegionShift ) + ( by & ( RegionBlocks - 1 ) );
            var colors = region.Colors.AsSpan( local * 64, 64 );

            if ( Volatile.Read( ref region.Done[local] ) == 0 )
            {
                _map.RenderBlock( bx, by, _statics, _patched, colors );
                Volatile.Write( ref region.Done[local], 1 );
            }

            return colors[( ( y & 7 ) << 3 ) + ( x & 7 )];
        }

        private sealed class Region
        {
            public readonly ushort[] Colors = new ushort[RegionBlocks * RegionBlocks * 64];
            public readonly byte[] Done = new byte[RegionBlocks * RegionBlocks];
        }
    }

    // ----- patches -----

    private void LoadPatches(
        MapPatchFiles files,
        ref Dictionary<int, int>? landPatches,
        ref MappedFile? mapDif,
        ref Dictionary<int, (long Offset, int Length)>? staticPatches,
        ref MappedFile? staDif )
    {
        var blocks = BlockWidth * BlockHeight;

        if ( files is { MapList: { } mapList, MapData: { } mapData } )
        {
            var list = File.ReadAllBytes( mapList );
            mapDif = MappedFile.Open( mapData );
            var count = ( int )Math.Min( list.Length / 4, mapDif.Length / BlockBytes );
            landPatches = new Dictionary<int, int>( count );

            for ( var i = 0; i < count; i++ )
            {
                var block = BinaryPrimitives.ReadInt32LittleEndian( list.AsSpan( i * 4 ) );

                if ( ( uint )block < ( uint )blocks )
                {
                    landPatches[block] = i;
                }
            }
        }

        if ( files is { StaticList: { } staList, StaticIndex: { } staIndex, StaticData: { } staData } )
        {
            var list = File.ReadAllBytes( staList );
            var lookup = File.ReadAllBytes( staIndex );
            staDif = MappedFile.Open( staData );
            var count = Math.Min( list.Length / 4, lookup.Length / 12 );
            staticPatches = new Dictionary<int, (long Offset, int Length)>( count );

            for ( var i = 0; i < count; i++ )
            {
                var block = BinaryPrimitives.ReadInt32LittleEndian( list.AsSpan( i * 4 ) );
                var offset = BinaryPrimitives.ReadInt32LittleEndian( lookup.AsSpan( i * 12 ) );
                var length = BinaryPrimitives.ReadInt32LittleEndian( lookup.AsSpan( i * 12 + 4 ) );

                if ( ( uint )block < ( uint )blocks )
                {
                    staticPatches[block] = offset < 0 || length <= 0 ? ( 0, 0 ) : ( ( uint )offset, length );
                }
            }
        }
    }
}

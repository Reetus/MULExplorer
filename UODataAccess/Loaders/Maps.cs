using System.Runtime.InteropServices;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>One map cell: land tile id and altitude.</summary>
[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public readonly struct LandTile( ushort id, sbyte z )
{
    public readonly ushort Id = id;
    public readonly sbyte Z = z;
}

/// <summary>One static as stored in <c>statics*.mul</c>: X/Y are offsets inside its 8x8 block.</summary>
[StructLayout( LayoutKind.Sequential, Pack = 1 )]
public readonly struct StaticTile( ushort id, byte x, byte y, sbyte z, ushort hue )
{
    public readonly ushort Id = id;
    public readonly byte X = x;
    public readonly byte Y = y;
    public readonly sbyte Z = z;
    public readonly ushort Hue = hue;
}

/// <summary>
///     The client's facets (map0..5): <c>map{N}LegacyMUL.uop</c> or <c>map{N}.mul</c>, with
///     <c>staidx{N}.mul</c>/<c>statics{N}.mul</c> and the optional <c>stadif*</c>/<c>mapdif*</c>
///     patches. Clients may also ship an alternate "x" set (<c>map{N}x…</c>, <c>staidx{N}x.mul</c>),
///     exposed via <c>alternate: true</c>. Facets open lazily and are thread-safe.
/// </summary>
public sealed class Maps : IDisposable
{
    public const int FacetCount = 6;

    /// <summary>Default facet sizes in tiles (ClassicUO / UOFiddler). Map 0/1 shrink to 6144 wide for pre-ML files.</summary>
    private static readonly (int Width, int Height)[] DefaultSizes =
    [
        ( 7168, 4096 ), ( 7168, 4096 ), ( 2304, 1600 ), ( 2560, 2048 ), ( 1448, 1448 ), ( 1280, 4096 )
    ];

    private static readonly string[] Names = ["Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur"];

    private readonly FacetMap?[] _facets = new FacetMap?[FacetCount * 2];
    private readonly bool[] _opened = new bool[FacetCount * 2];
    private readonly Lock _lock = new();
    private MapRenderTables? _tables;

    public Maps( UOFileSet files )
    {
        Files = files;

        var any = false;

        for ( var i = 0; i < FacetCount && !any; i++ )
        {
            any = MapPath( i, false ) is not null;
        }

        if ( !any )
        {
            throw new FileNotFoundException( "No maps found (map0LegacyMUL.uop or map0.mul)." );
        }
    }

    public UOFileSet Files { get; }

    public static string GetName( int facet ) => ( uint )facet < FacetCount ? Names[facet] : $"Map{facet}";

    /// <summary>True when the facet's map file exists (map1 falls back to map0, like the client).</summary>
    public bool IsAvailable( int facet, bool alternate = false ) =>
        ( uint )facet < FacetCount && ( MapPath( facet, alternate ) is not null || ( facet == 1 && MapPath( 0, alternate ) is not null ) );

    /// <summary>True when the client ships the alternate "x" map for this facet.</summary>
    public bool HasAlternate( int facet ) => ( uint )facet < FacetCount && MapPath( facet, true ) is not null;

    /// <summary>The facet, opened on first use; null if its files are missing.</summary>
    public FacetMap? GetFacet( int facet, bool alternate = false )
    {
        if ( ( uint )facet >= FacetCount )
        {
            return null;
        }

        var slot = facet * 2 + ( alternate ? 1 : 0 );

        lock ( _lock )
        {
            if ( !_opened[slot] )
            {
                _facets[slot] = Open( facet, alternate );
                _opened[slot] = true;
            }

            return _facets[slot];
        }
    }

    public void Dispose()
    {
        lock ( _lock )
        {
            foreach ( var facet in _facets )
            {
                facet?.Dispose();
            }

            Array.Clear( _facets );
            Array.Clear( _opened );
        }
    }

    /// <summary>radarcol / tiledata heights / hues used by the radar renderer, loaded once.</summary>
    internal MapRenderTables Tables
    {
        get
        {
            if ( Volatile.Read( ref _tables ) is { } tables )
            {
                return tables;
            }

            lock ( _lock )
            {
                return _tables ??= MapRenderTables.Load( Files );
            }
        }
        set => Volatile.Write( ref _tables, value );
    }

    private string? MapPath( int facet, bool alternate )
    {
        var suffix = alternate ? "x" : "";
        return Files.Find( $"map{facet}{suffix}LegacyMUL.uop" ) ?? Files.Find( $"map{facet}{suffix}.mul" );
    }

    private FacetMap? Open( int facet, bool alternate )
    {
        var fileIndex = facet;

        if ( MapPath( facet, alternate ) is null )
        {
            // The client fakes a missing map1 with map0.
            if ( facet != 1 || MapPath( 0, alternate ) is null )
            {
                return null;
            }

            fileIndex = 0;
        }

        var suffix = alternate ? "x" : "";
        var mapPath = MapPath( fileIndex, alternate )!;
        var staidx = Files.Find( $"staidx{fileIndex}{suffix}.mul" ) ?? ( alternate ? Files.Find( $"staidx{fileIndex}.mul" ) : null );
        var statics = Files.Find( $"statics{fileIndex}{suffix}.mul" ) ?? ( alternate ? Files.Find( $"statics{fileIndex}.mul" ) : null );

        MapPatchFiles? patches = null;

        if ( !alternate )
        {
            patches = new MapPatchFiles(
                Files.Find( $"mapdifl{fileIndex}.mul" ),
                Files.Find( $"mapdif{fileIndex}.mul" ),
                Files.Find( $"stadifl{fileIndex}.mul" ),
                Files.Find( $"stadifi{fileIndex}.mul" ),
                Files.Find( $"stadif{fileIndex}.mul" ) );
        }

        return new FacetMap( this, facet, fileIndex, alternate, DefaultSizes[facet], mapPath, staidx, statics, patches );
    }
}

internal sealed record MapPatchFiles( string? MapList, string? MapData, string? StaticList, string? StaticIndex, string? StaticData );

/// <summary>Lookup tables for radar rendering.</summary>
internal sealed class MapRenderTables
{
    private MapRenderTables( ushort[] radar, byte[] heights, ushort[][]? hues )
    {
        Radar = radar;
        Heights = heights;
        HueColors = hues;
    }

    /// <summary>radarcol.mul: land colors then item colors at 0x4000.</summary>
    public ushort[] Radar { get; }

    /// <summary>Tiledata item heights.</summary>
    public byte[] Heights { get; }

    /// <summary>hues.mul colors, entry i is game hue i + 1 (null when the client has no hues.mul).</summary>
    public ushort[][]? HueColors { get; }

    public static MapRenderTables Load( UOFileSet files )
    {
        var radar = new RadarColors( files ).Colors.ToArray();
        var items = new TileData( files ).ItemTable;
        var heights = new byte[items.Length];

        for ( var i = 0; i < items.Length; i++ )
        {
            heights[i] = items[i].Height;
        }

        ushort[][]? hues = null;

        if ( files.Exists( "hues.mul" ) )
        {
            hues = new Hues( files ).List.Select( h => h.Colors ).ToArray();
        }

        return new MapRenderTables( radar, heights, hues );
    }

    /// <summary>For synthetic tests: explicit tables.</summary>
    internal static MapRenderTables Create( ushort[] radar, byte[] heights, ushort[][]? hues = null ) => new( radar, heights, hues );
}

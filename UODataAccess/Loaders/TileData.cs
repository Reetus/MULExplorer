using System.Buffers.Binary;
using System.Text;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

[Flags]
public enum TileFlag : ulong
{
    None = 0,
    Background = 0x00000001,
    Weapon = 0x00000002,
    Transparent = 0x00000004,
    Translucent = 0x00000008,
    Wall = 0x00000010,
    Damaging = 0x00000020,
    Impassable = 0x00000040,
    Wet = 0x00000080,
    Unknown1 = 0x00000100,
    Surface = 0x00000200,
    Bridge = 0x00000400,
    Generic = 0x00000800,
    Window = 0x00001000,
    NoShoot = 0x00002000,
    ArticleA = 0x00004000,
    ArticleAn = 0x00008000,
    ArticleThe = 0x00010000,
    Foliage = 0x00020000,
    PartialHue = 0x00040000,
    NoHouse = 0x00080000,
    Map = 0x00100000,
    Container = 0x00200000,
    Wearable = 0x00400000,
    LightSource = 0x00800000,
    Animation = 0x01000000,
    HoverOver = 0x02000000,
    NoDiagonal = 0x04000000,
    Armor = 0x08000000,
    Roof = 0x10000000,
    Door = 0x20000000,
    StairBack = 0x40000000,
    StairRight = 0x80000000,
    AlphaBlend = 0x0100000000,
    UseNewArt = 0x0200000000,
    ArtUsed = 0x0400000000,
    Unused8 = 0x0800000000,
    NoShadow = 0x1000000000,
    PixelBleed = 0x2000000000,
    PlayAnimOnce = 0x4000000000,
    MultiMovable = 0x10000000000
}

public readonly record struct LandData( int Id, string Name, TileFlag Flags, ushort TextureId );

public readonly record struct ItemData(
    int Id,
    string Name,
    TileFlag Flags,
    byte Weight,
    byte Quality,
    ushort MiscData,
    byte Unknown2,
    byte Quantity,
    ushort Animation,
    byte Unknown3,
    byte Hue,
    byte StackingOffset,
    byte Value,
    byte Height )
{
    /// <summary>Equipment layer for wearables (stored in the quality byte).</summary>
    public byte Layer => Quality;

    /// <summary>Light id for light sources (also the quality byte).</summary>
    public byte LightId => Quality;

    public bool IsBackground => Flags.HasFlag( TileFlag.Background );

    public bool IsBridge => Flags.HasFlag( TileFlag.Bridge );

    public bool IsSurface => Flags.HasFlag( TileFlag.Surface );

    /// <summary>Height used for stacking: bridges count half.</summary>
    public int CalcHeight => IsBridge ? Height / 2 : Height;
}

/// <summary>
///     <c>tiledata.mul</c>: 0x4000 land entries then the item entries, in groups of 32 behind a
///     4-byte header. High Seas (7.0.9+) widened flags from 32 to 64 bits; the layout is detected
///     from the file size.
/// </summary>
public sealed class TileData
{
    private const int LandCount = 0x4000;
    private const int LandGroups = LandCount / 32;

    public TileData( UOFileSet files )
    {
        var path = files.Find( "tiledata.mul" ) ?? throw new FileNotFoundException( "tiledata.mul not found." );
        var data = File.ReadAllBytes( path );

        IsNewFormat = DetectNewFormat( data.Length );

        var landSize = IsNewFormat ? 30 : 26;
        var itemSize = IsNewFormat ? 41 : 37;

        LandTable = new LandData[LandCount];
        var pos = 0;

        for ( var i = 0; i < LandCount; i++ )
        {
            if ( ( i & 31 ) == 0 )
            {
                pos += 4;
            }

            var e = data.AsSpan( pos, landSize );
            var flags = ReadFlags( e, out var o );
            LandTable[i] = new LandData( i, ReadName( e.Slice( o + 2, 20 ) ), flags, BinaryPrimitives.ReadUInt16LittleEndian( e[o..] ) );
            pos += landSize;
        }

        var groups = ( data.Length - pos ) / ( 4 + 32 * itemSize );
        ItemTable = new ItemData[groups * 32];

        for ( var i = 0; i < ItemTable.Length; i++ )
        {
            if ( ( i & 31 ) == 0 )
            {
                pos += 4;
            }

            var e = data.AsSpan( pos, itemSize );
            var flags = ReadFlags( e, out var o );

            ItemTable[i] = new ItemData(
                i,
                ReadName( e.Slice( o + 13, 20 ) ),
                flags,
                e[o],
                e[o + 1],
                BinaryPrimitives.ReadUInt16LittleEndian( e[( o + 2 )..] ),
                e[o + 4],
                e[o + 5],
                BinaryPrimitives.ReadUInt16LittleEndian( e[( o + 6 )..] ),
                e[o + 8],
                e[o + 9],
                e[o + 10],
                e[o + 11],
                e[o + 12] );

            pos += itemSize;
        }

        TileFlag ReadFlags( ReadOnlySpan<byte> e, out int offset )
        {
            offset = IsNewFormat ? 8 : 4;
            return IsNewFormat
                ? ( TileFlag )BinaryPrimitives.ReadUInt64LittleEndian( e )
                : ( TileFlag )BinaryPrimitives.ReadUInt32LittleEndian( e );
        }
    }

    /// <summary>True for the 64-bit-flag (High Seas and later) layout.</summary>
    public bool IsNewFormat { get; }

    public LandData[] LandTable { get; }

    public ItemData[] ItemTable { get; }

    public ItemData GetItem( int id ) => ( uint )id < ( uint )ItemTable.Length ? ItemTable[id] : default;

    public LandData GetLand( int id ) => ( uint )id < ( uint )LandTable.Length ? LandTable[id] : default;

    private static bool DetectNewFormat( int length )
    {
        const int newLand = LandGroups * ( 4 + 32 * 30 );
        const int oldLand = LandGroups * ( 4 + 32 * 26 );

        if ( length > newLand && ( length - newLand ) % ( 4 + 32 * 41 ) == 0 )
        {
            return true;
        }

        return !( length > oldLand && ( length - oldLand ) % ( 4 + 32 * 37 ) == 0 ) && length >= newLand;
    }

    private static string ReadName( ReadOnlySpan<byte> bytes )
    {
        var end = bytes.IndexOf( ( byte )0 );
        return string.Intern( Encoding.Latin1.GetString( end < 0 ? bytes : bytes[..end] ) );
    }
}

using System.Runtime.InteropServices;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     <c>radarcol.mul</c>: one ARGB1555 color per tile used for the radar/overview map, land tiles
///     first (0x4000) then items.
/// </summary>
public sealed class RadarColors
{
    public const int LandCount = 0x4000;

    private readonly ushort[] _colors;

    public RadarColors( UOFileSet files )
    {
        var path = files.Find( "radarcol.mul" ) ?? throw new FileNotFoundException( "radarcol.mul not found." );
        var bytes = File.ReadAllBytes( path );
        _colors = MemoryMarshal.Cast<byte, ushort>( bytes.AsSpan( 0, bytes.Length & ~1 ) ).ToArray();
    }

    /// <summary>Raw table: land colors at [0, 0x4000), item colors after.</summary>
    public ReadOnlySpan<ushort> Colors => _colors;

    public int ItemCount => Math.Max( 0, _colors.Length - LandCount );

    public ushort GetLandColor( int id ) => ( uint )id < LandCount && id < _colors.Length ? _colors[id] : ( ushort )0;

    public ushort GetItemColor( int id ) => id >= 0 && LandCount + id < _colors.Length ? _colors[LandCount + id] : ( ushort )0;

    public uint GetLandArgb( int id ) => Color16.ToArgb( GetLandColor( id ) );

    public uint GetItemArgb( int id ) => Color16.ToArgb( GetItemColor( id ) );
}

using System.Buffers.Binary;
using System.Text;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>One hue: a 32-step ramp of ARGB1555 colors.</summary>
public sealed record Hue( int Index, string Name, ushort[] Colors, ushort TableStart, ushort TableEnd )
{
    /// <summary>The ramp as 0xAARRGGBB.</summary>
    public uint ArgbAt( int i ) => Color16.ToArgb( Colors[i] );

    /// <summary>
    ///     Recolors <paramref name="source" />: each pixel's 5-bit red channel indexes the ramp.
    ///     With <paramref name="onlyGray" /> (partial hue) only pixels where R == G == B change.
    /// </summary>
    public Pixmap ApplyTo( Pixmap source, bool onlyGray )
    {
        var result = source.Clone();
        var pixels = result.Pixels;

        for ( var i = 0; i < pixels.Length; i++ )
        {
            var p = pixels[i];

            if ( p >> 24 == 0 )
            {
                continue;
            }

            var c = Color16.FromArgb( p );
            var r = ( c >> 10 ) & 0x1F;

            if ( onlyGray && ( r != ( ( c >> 5 ) & 0x1F ) || r != ( c & 0x1F ) ) )
            {
                continue;
            }

            pixels[i] = Color16.ToArgb( Colors[r] );
        }

        return result;
    }
}

/// <summary>
///     <c>hues.mul</c>: groups of a 4-byte header plus 8 hues, each hue 32 colors, table
///     start/end and a 20-byte name. Hue numbers are 1-based in game; <see cref="Get" /> takes
///     the game number.
/// </summary>
public sealed class Hues
{
    private const int HueSize = 32 * 2 + 4 + 20;
    private const int GroupSize = 4 + 8 * HueSize;

    public Hues( UOFileSet files )
    {
        var path = files.Find( "hues.mul" ) ?? throw new FileNotFoundException( "hues.mul not found." );
        var data = File.ReadAllBytes( path ).AsSpan();
        var groups = data.Length / GroupSize;
        var list = new Hue[groups * 8];

        for ( var g = 0; g < groups; g++ )
        {
            var group = data.Slice( g * GroupSize + 4, 8 * HueSize );

            for ( var h = 0; h < 8; h++ )
            {
                var block = group.Slice( h * HueSize, HueSize );
                var colors = new ushort[32];

                for ( var c = 0; c < 32; c++ )
                {
                    colors[c] = BinaryPrimitives.ReadUInt16LittleEndian( block[( c * 2 )..] );
                }

                var nameBytes = block.Slice( 68, 20 );
                var end = nameBytes.IndexOf( ( byte )0 );
                var name = Encoding.Latin1.GetString( end < 0 ? nameBytes : nameBytes[..end] ).Trim();
                var index = g * 8 + h;

                list[index] = new Hue(
                    index,
                    name,
                    colors,
                    BinaryPrimitives.ReadUInt16LittleEndian( block[64..] ),
                    BinaryPrimitives.ReadUInt16LittleEndian( block[66..] ) );
            }
        }

        List = list;
    }

    /// <summary>Zero-based list; entry <c>i</c> is game hue <c>i + 1</c>.</summary>
    public IReadOnlyList<Hue> List { get; }

    /// <summary>
    ///     Looks up a game hue number. The 0x8000 (translucent) and 0x4000 (partial) bits are ignored.
    /// </summary>
    public Hue? Get( int hue )
    {
        var index = ( hue & 0x3FFF ) - 1;
        return ( uint )index < ( uint )List.Count ? List[index] : null;
    }

    /// <summary>Applies game hue <paramref name="hue" /> (0 = none), honoring the partial-hue bit.</summary>
    public Pixmap Apply( Pixmap source, int hue, bool partialFromTileData = false )
    {
        if ( Get( hue ) is not { } h )
        {
            return source;
        }

        return h.ApplyTo( source, partialFromTileData || ( hue & 0x4000 ) != 0 );
    }
}

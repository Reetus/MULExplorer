using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>Light-level statistics of one light map.</summary>
public readonly record struct LightStats( int Min, int Max, double Average, int LitPixels );

/// <summary>
///     Light maps from <c>light.mul</c>/<c>lightidx.mul</c>. Each entry is <c>width*height</c>
///     signed bytes of additive light intensity; the idx extra field packs
///     <c>(width &lt;&lt; 16) | height</c> (ClassicUO's reading; every shipped light is square).
///     Intensities are mapped to 0..31 the way ClassicUO does: values above 0x1F (negative bytes)
///     become <c>~v &amp; 0x1F</c>.
/// </summary>
public sealed class Lights : IDisposable
{
    public const int MaxIntensity = 0x1F;

    private readonly FileIndex _index;

    public Lights( UOFileSet files )
    {
        Files = files;
        _index = FileIndex.Open( files, "lightidx.mul", "light.mul" )
                 ?? throw new FileNotFoundException( "light.mul/lightidx.mul not found." );
    }

    public UOFileSet Files { get; }

    /// <summary>Number of index slots (UOFiddler's GetCount).</summary>
    public int Count => _index.Length;

    public void Dispose() => _index.Dispose();

    public (int Width, int Height)? GetSize( int id )
    {
        var entry = _index.GetEntry( id );
        return entry.IsValid && entry.ExtraHigh > 0 && entry.ExtraLow > 0 ? ( entry.ExtraHigh, entry.ExtraLow ) : null;
    }

    public bool IsValid( int id ) => GetSize( id ) is not null;

    /// <summary>Ids of every light with a non-empty map.</summary>
    public IReadOnlyList<int> ValidIds => field ??= Enumerable.Range( 0, Count ).Where( IsValid ).ToList();

    /// <summary>The raw signed light values, row-major.</summary>
    public sbyte[]? GetRaw( int id, out int width, out int height )
    {
        width = height = 0;

        if ( GetSize( id ) is not { } size || !_index.TryRead( id, out var data ) )
        {
            return null;
        }

        ( width, height ) = size;
        var raw = new sbyte[width * height];
        var count = Math.Min( raw.Length, data.Length );
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>( data[..count] ).CopyTo( raw );
        return raw;
    }

    /// <summary>ClassicUO's mapping from a stored byte to a 0..31 intensity.</summary>
    public static int Intensity( sbyte value )
    {
        var v = ( byte )value;
        return v > MaxIntensity ? ~v & MaxIntensity : v;
    }

    public LightStats? GetStats( int id )
    {
        if ( GetRaw( id, out _, out _ ) is not { Length: > 0 } raw )
        {
            return null;
        }

        int min = int.MaxValue, max = int.MinValue, lit = 0;
        long sum = 0;

        foreach ( var v in raw )
        {
            min = Math.Min( min, v );
            max = Math.Max( max, v );
            var i = Intensity( v );
            sum += i;
            lit += i > 0 ? 1 : 0;
        }

        return new LightStats( min, max, ( double )sum / raw.Length, lit );
    }

    /// <summary>The light map as a grayscale image (intensity 0 is transparent), as ClassicUO builds its light textures.</summary>
    public Pixmap? Get( int id )
    {
        if ( GetRaw( id, out var width, out var height ) is not { } raw )
        {
            return null;
        }

        var pixmap = new Pixmap( width, height );

        for ( var i = 0; i < raw.Length; i++ )
        {
            var v = ( uint )Intensity( raw[i] );

            if ( v != 0 )
            {
                var c = ( v << 3 ) | ( v >> 2 );
                pixmap.Pixels[i] = 0xFF00_0000u | ( c << 16 ) | ( c << 8 ) | c;
            }
        }

        return pixmap;
    }

    /// <summary>
    ///     UOFiddler's "IG preview": a floor of <paramref name="land" /> tiles with
    ///     <paramref name="source" /> (the light-emitting item) in the middle, darkened to a third and
    ///     brightened by the light map centred 30 px below the middle.
    /// </summary>
    public Pixmap? RenderPreview( int id, Pixmap? land, Pixmap? source, int width = 0, int height = 0 )
    {
        if ( GetRaw( id, out var lw, out var lh ) is not { } raw )
        {
            return null;
        }

        width = width > 0 ? width : Math.Max( 300, lw + 60 );
        height = height > 0 ? height : Math.Max( 300, lh + 100 );
        var scene = new Pixmap( width, height );
        Array.Fill( scene.Pixels, 0xFF00_0000u );

        if ( land is not null )
        {
            var row = 0;

            for ( var y = -22; y <= height; y += 22, row++ )
            {
                for ( var x = row % 2 == 0 ? 0 : -22; x <= width; x += 44 )
                {
                    scene.Blit( land, x, y );
                }
            }
        }

        if ( source is not null )
        {
            scene.Blit( source, ( width - source.Width ) / 2, ( height - source.Height ) / 2 );
        }

        var startX = width / 2 - lw / 2;
        var startY = 30 + height / 2 - lh / 2;
        var pixels = scene.Pixels;

        for ( var y = 0; y < height; y++ )
        {
            for ( var x = 0; x < width; x++ )
            {
                double light = 0;
                int lx = x - startX, ly = y - startY;

                if ( ( uint )lx < ( uint )lw && ( uint )ly < ( uint )lh )
                {
                    light = Intensity( raw[ly * lw + lx] ) * 3 / 31d;
                }

                ref var p = ref pixels[y * width + x];
                p = 0xFF00_0000u | ( Light( p >> 16, light ) << 16 ) | ( Light( p >> 8, light ) << 8 ) | Light( p, light );
            }
        }

        return scene;

        static uint Light( uint channel, double light )
        {
            var c = ( channel & 0xFF ) / 3;
            return ( uint )Math.Min( 255, c + ( int )( c * light ) );
        }
    }
}

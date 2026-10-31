using System.Buffers.Binary;
using System.Globalization;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>
///     Land textures from <c>texmaps.mul</c>/<c>texidx.mul</c>: square ARGB1555 images, 64x64
///     (0x2000 bytes) or 128x128 (0x8000 bytes). The size follows the entry length (ClassicUO);
///     UOFiddler uses the idx extra field, which agrees on shipped data.
/// </summary>
/// <remarks>
///     <c>TexTerr.def</c>, when present, makes the client draw some texture ids with another id's
///     pixels (ClassicUO applies it unconditionally). <see cref="Get" /> returns the file's own texture;
///     <see cref="GetAlias" /> reports the redirect.
/// </remarks>
public sealed class Textures : IDisposable
{
    public const int MaxCount = 0x4000;

    private readonly FileIndex _index;
    private readonly Dictionary<int, int> _aliases = new();

    public Textures( UOFileSet files )
    {
        _index = FileIndex.Open( files, "texidx.mul", "texmaps.mul", length: MaxCount )
                 ?? throw new FileNotFoundException( "texmaps.mul/texidx.mul not found." );

        if ( files.Find( "TexTerr.def" ) is { } def )
        {
            ParseDef( File.ReadLines( def ), _aliases );
        }
    }

    public int Count => Math.Min( _index.Length, MaxCount );

    public IReadOnlyDictionary<int, int> Aliases => _aliases;

    public void Dispose() => _index.Dispose();

    public bool IsValid( int id ) => ( uint )id < ( uint )Count && SizeOf( _index.GetEntry( id ).Length ) > 0;

    /// <summary>Edge length (64 or 128) without decoding, 0 if the id is empty.</summary>
    public int GetSize( int id ) => ( uint )id < ( uint )Count ? SizeOf( _index.GetEntry( id ).Length ) : 0;

    /// <summary>The id TexTerr.def redirects <paramref name="id" /> to, if any.</summary>
    public int? GetAlias( int id ) => _aliases.TryGetValue( id, out var target ) ? target : null;

    public Pixmap? Get( int id ) => IsValid( id ) && _index.TryRead( id, out var data ) ? Decode( data ) : null;

    /// <summary>Decodes a texture payload; the size follows the length.</summary>
    public static Pixmap? Decode( ReadOnlySpan<byte> data )
    {
        var size = SizeOf( data.Length );

        if ( size == 0 )
        {
            return null;
        }

        var pixmap = new Pixmap( size, size );
        var pixels = pixmap.Pixels;

        for ( var i = 0; i < pixels.Length; i++ )
        {
            pixels[i] = Color16.ToArgb( BinaryPrimitives.ReadUInt16LittleEndian( data[( i * 2 )..] ) );
        }

        return pixmap;
    }

    private static int SizeOf( int length ) => length >= 128 * 128 * 2 ? 128 : length >= 64 * 64 * 2 ? 64 : 0;

    /// <summary>
    ///     Lines are <c>index {a, b, ...} [ignored]</c>; like ClassicUO's <c>DefReader</c> the last group
    ///     member wins. <c>#</c> starts a comment.
    /// </summary>
    internal static void ParseDef( IEnumerable<string> lines, Dictionary<int, int> aliases )
    {
        foreach ( var raw in lines )
        {
            var line = raw.AsSpan();
            var comment = line.IndexOf( '#' );
            line = ( comment >= 0 ? line[..comment] : line ).Trim();
            var open = line.IndexOf( '{' );
            var close = line.IndexOf( '}' );

            if ( open <= 0 || close < open || !int.TryParse( line[..open].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index ) )
            {
                continue;
            }

            int? target = null;

            foreach ( var part in line[( open + 1 )..close].ToString().Split( [',', ' '], StringSplitOptions.RemoveEmptyEntries ) )
            {
                if ( int.TryParse( part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value ) && ( uint )value < MaxCount )
                {
                    target = value;
                }
            }

            if ( target is { } t && ( uint )index < MaxCount )
            {
                aliases[index] = t;
            }
        }
    }
}

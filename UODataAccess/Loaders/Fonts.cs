using System.Buffers.Binary;
using UODataAccess.Imaging;
using UODataAccess.IO;

namespace UODataAccess.Loaders;

/// <summary>One ASCII font character: 16-bit colors, 0 = transparent.</summary>
public sealed class AsciiGlyph( int width, int height, byte unknown, ushort[] pixels )
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>Third header byte of each character; meaning unknown, kept for display.</summary>
    public byte Unknown { get; } = unknown;

    public ushort[] Pixels { get; } = pixels;

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>
    ///     The glyph as an image. With a hue each pixel's red channel indexes the ramp; with
    ///     <paramref name="onlyGray" /> (partial hue) only gray pixels change.
    /// </summary>
    public Pixmap? ToPixmap( Hue? hue = null, bool onlyGray = false )
    {
        if ( IsEmpty )
        {
            return null;
        }

        var pixmap = new Pixmap( Width, Height );
        DrawTo( pixmap, 0, 0, hue, onlyGray );
        return pixmap;
    }

    internal void DrawTo( Pixmap target, int left, int top, Hue? hue, bool onlyGray )
    {
        for ( var y = 0; y < Height; y++ )
        {
            for ( var x = 0; x < Width; x++ )
            {
                var c = Pixels[y * Width + x];

                if ( c != 0 )
                {
                    target[left + x, top + y] = Fonts.HueColor( c, hue, onlyGray );
                }
            }
        }
    }
}

/// <summary>
///     One of the ASCII fonts in <c>fonts.mul</c>: a header byte then 224 characters for codes
///     0x20..0xFF, each <c>(width, height, unknown)</c> plus <c>width*height</c> u16 colors.
/// </summary>
public sealed class AsciiFont( int index, byte header, AsciiGlyph[] glyphs )
{
    public const int FirstChar = 0x20;
    public const int GlyphCount = 224;

    public int Index { get; } = index;

    public byte Header { get; } = header;

    public IReadOnlyList<AsciiGlyph> Glyphs { get; } = glyphs;

    /// <summary>Line height: the tallest of the first 96 characters (as UOFiddler computes it).</summary>
    public int Height { get; } = glyphs.Take( 96 ).Select( g => g.Height ).DefaultIfEmpty( 0 ).Max();

    /// <summary>Glyph index for a character; control characters map to the space glyph (ClassicUO).</summary>
    public static int GlyphIndex( char c )
    {
        var b = ( byte )c;
        return b < FirstChar ? 0 : b - FirstChar;
    }

    public AsciiGlyph GlyphFor( char c ) => Glyphs[GlyphIndex( c )];

    /// <summary>ClassicUO's per-character vertical offsets for lower-case letters and symbols.</summary>
    public int OffsetY( char c )
    {
        ReadOnlySpan<int> charTable = [2, 0, 2, 2, 0, 0, 2, 2, 0, 0];
        ReadOnlySpan<int> symbolTable = [1, 0, 1, 1, -1, 0, 1, 1, 0, 0];
        var b = ( byte )c;

        if ( b == 0xB8 )
        {
            return 1;
        }

        if ( b is >= 0x41 and <= 0x5A or >= 0xC0 and <= 0xDF or 0xA8 )
        {
            return 0;
        }

        if ( Index >= charTable.Length )
        {
            return 2;
        }

        return b is >= 0x61 and <= 0x7A ? charTable[Index] : symbolTable[Index];
    }

    public int MeasureWidth( ReadOnlySpan<char> line )
    {
        var width = 0;

        foreach ( var c in line )
        {
            width += GlyphFor( c ).Width;
        }

        return width;
    }
}

/// <summary>One Unicode font character: a 1-bit-per-pixel bitmap plus signed offsets.</summary>
public sealed class UnicodeGlyph( int code, sbyte xOffset, sbyte yOffset, int width, int height, byte[] bits )
{
    public int Code { get; } = code;

    public sbyte XOffset { get; } = xOffset;

    public sbyte YOffset { get; } = yOffset;

    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>Rows of <c>(width + 7) / 8</c> bytes, most significant bit first.</summary>
    public byte[] Bits { get; } = bits;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IsSet( int x, int y )
    {
        var offset = x / 8 + y * ( ( Width + 7 ) / 8 );
        return ( uint )offset < ( uint )Bits.Length && ( Bits[offset] & ( 0x80 >> ( x % 8 ) ) ) != 0;
    }

    public Pixmap? ToPixmap( uint color = 0xFFFFFFFF )
    {
        if ( IsEmpty )
        {
            return null;
        }

        var pixmap = new Pixmap( Width, Height );
        DrawTo( pixmap, 0, 0, color );
        return pixmap;
    }

    internal void DrawTo( Pixmap target, int left, int top, uint color )
    {
        for ( var y = 0; y < Height; y++ )
        {
            for ( var x = 0; x < Width; x++ )
            {
                if ( IsSet( x, y ) )
                {
                    target[left + x, top + y] = color;
                }
            }
        }
    }
}

/// <summary>
///     <c>unifont{N}.mul</c>: a table of 0x10000 i32 offsets (0 = no glyph), each pointing at
///     <c>(xOffset:i8, yOffset:i8, width:u8, height:u8)</c> and the 1-bpp rows. Glyphs are read on
///     demand from the mapped file.
/// </summary>
public sealed class UnicodeFont : IDisposable
{
    public const int CharCount = 0x10000;

    /// <summary>Width the client uses for a space, which usually has no glyph (ClassicUO).</summary>
    public const int SpaceWidth = 8;

    private readonly MappedFile _file;
    private readonly UnicodeGlyph?[] _cache = new UnicodeGlyph?[CharCount];
    private readonly bool[] _loaded = new bool[CharCount];
    private readonly Lock _lock = new();

    public UnicodeFont( int index, string path )
    {
        Index = index;
        FileName = Path.GetFileName( path );
        _file = MappedFile.Open( path );
    }

    public int Index { get; }

    public string FileName { get; }

    public void Dispose() => _file.Dispose();

    /// <summary>True when the lookup table has an entry for <paramref name="code" /> (cheap, no decoding).</summary>
    public bool HasGlyph( int code )
    {
        if ( ( uint )code >= CharCount )
        {
            return false;
        }

        var table = _file.Slice( code * 4L, 4 );

        if ( table.Length < 4 )
        {
            return false;
        }

        var lookup = BinaryPrimitives.ReadInt32LittleEndian( table );

        if ( lookup <= 0 || lookup + 4L > _file.Length )
        {
            return false;
        }

        var header = _file.Slice( lookup, 4 );
        return header[2] > 0 && header[3] > 0;
    }

    /// <summary>Every character code that has a visible glyph.</summary>
    public IReadOnlyList<int> Characters => field ??= Enumerable.Range( 0, CharCount ).Where( HasGlyph ).ToList();

    public UnicodeGlyph? GetGlyph( int code )
    {
        if ( ( uint )code >= CharCount )
        {
            return null;
        }

        lock ( _lock )
        {
            if ( _loaded[code] )
            {
                return _cache[code];
            }

            _loaded[code] = true;
            return _cache[code] = Read( code );
        }
    }

    private UnicodeGlyph? Read( int code )
    {
        var table = _file.Slice( code * 4L, 4 );

        if ( table.Length < 4 )
        {
            return null;
        }

        var lookup = BinaryPrimitives.ReadInt32LittleEndian( table );

        if ( lookup <= 0 || lookup + 4L > _file.Length )
        {
            return null;
        }

        var header = _file.Slice( lookup, 4 );
        var width = header[2];
        var height = header[3];
        var bits = width > 0 && height > 0 ? _file.Slice( lookup + 4, ( width + 7 ) / 8 * height ).ToArray() : [];
        return new UnicodeGlyph( code, ( sbyte )header[0], ( sbyte )header[1], width, height, bits );
    }

    /// <summary>Horizontal advance of a character, as ClassicUO lays text out.</summary>
    public int Advance( char c ) =>
        c == ' ' ? SpaceWidth : GetGlyph( c ) is { IsEmpty: false } g ? g.XOffset + g.Width + 1 : 0;
}

/// <summary>
///     ASCII fonts from <c>fonts.mul</c> and Unicode fonts from <c>unifont.mul</c>,
///     <c>unifont1.mul</c> ... <c>unifont19.mul</c>. Layout follows ClassicUO's FontsLoader.
/// </summary>
public sealed class Fonts : IDisposable
{
    public const int MaxUnicodeFonts = 20;

    private readonly string?[] _unicodePaths = new string?[MaxUnicodeFonts];
    private readonly UnicodeFont?[] _unicode = new UnicodeFont?[MaxUnicodeFonts];
    private readonly Lock _lock = new();

    public Fonts( UOFileSet files )
    {
        Files = files;

        for ( var i = 0; i < MaxUnicodeFonts; i++ )
        {
            _unicodePaths[i] = files.Find( UnicodeFileName( i ) );
        }

        UnicodeFontIds = Enumerable.Range( 0, MaxUnicodeFonts ).Where( i => _unicodePaths[i] is not null ).ToList();
        Ascii = files.Find( "fonts.mul" ) is { } path ? ParseAscii( File.ReadAllBytes( path ) ) : [];

        if ( Ascii.Count == 0 && UnicodeFontIds.Count == 0 )
        {
            throw new FileNotFoundException( "No fonts found (fonts.mul or unifont*.mul)." );
        }
    }

    public UOFileSet Files { get; }

    public IReadOnlyList<AsciiFont> Ascii { get; }

    /// <summary>Numbers N of the <c>unifontN.mul</c> files present (0 = <c>unifont.mul</c>).</summary>
    public IReadOnlyList<int> UnicodeFontIds { get; }

    public static string UnicodeFileName( int font ) => font == 0 ? "unifont.mul" : $"unifont{font}.mul";

    public void Dispose()
    {
        lock ( _lock )
        {
            for ( var i = 0; i < _unicode.Length; i++ )
            {
                _unicode[i]?.Dispose();
                _unicode[i] = null;
            }
        }
    }

    public AsciiFont? GetAscii( int font ) => ( uint )font < ( uint )Ascii.Count ? Ascii[font] : null;

    /// <summary>Opens a Unicode font on first use; null if its file is missing.</summary>
    public UnicodeFont? GetUnicode( int font )
    {
        if ( ( uint )font >= MaxUnicodeFonts || _unicodePaths[font] is not { } path )
        {
            return null;
        }

        lock ( _lock )
        {
            return _unicode[font] ??= new UnicodeFont( font, path );
        }
    }

    /// <summary>
    ///     Parses <c>fonts.mul</c>. Like ClassicUO, the number of fonts is however many complete
    ///     224-character blocks the file holds (classic clients have 10).
    /// </summary>
    public static IReadOnlyList<AsciiFont> ParseAscii( ReadOnlySpan<byte> data )
    {
        var fonts = new List<AsciiFont>();
        var pos = 0;

        while ( pos < data.Length )
        {
            var start = pos;
            var header = data[pos++];
            var glyphs = new AsciiGlyph[AsciiFont.GlyphCount];
            var complete = true;

            for ( var i = 0; i < glyphs.Length; i++ )
            {
                if ( pos + 3 > data.Length )
                {
                    complete = false;
                    break;
                }

                int w = data[pos], h = data[pos + 1];
                var unknown = data[pos + 2];
                pos += 3;
                var bytes = w * h * 2;

                if ( pos + bytes > data.Length )
                {
                    complete = false;
                    break;
                }

                var pixels = new ushort[w * h];

                for ( var p = 0; p < pixels.Length; p++ )
                {
                    pixels[p] = BinaryPrimitives.ReadUInt16LittleEndian( data[( pos + p * 2 )..] );
                }

                glyphs[i] = new AsciiGlyph( w, h, unknown, pixels );
                pos += bytes;
            }

            if ( !complete || pos == start )
            {
                break;
            }

            fonts.Add( new AsciiFont( fonts.Count, header, glyphs ) );
        }

        return fonts;
    }

    /// <summary>
    ///     Renders text with an ASCII font (multi-line on <c>\n</c>). Glyphs are top-aligned with
    ///     ClassicUO's per-character offsets. Fonts other than 5 and 8 are partially hued like the
    ///     client: only gray pixels take the hue.
    /// </summary>
    public Pixmap? RenderAscii( int font, string text, Hue? hue = null )
    {
        if ( GetAscii( font ) is not { } f || string.IsNullOrEmpty( text ) )
        {
            return null;
        }

        var lines = SplitLines( text );
        var onlyGray = font != 5 && font != 8;
        var heights = lines.Select( l => LineHeightAscii( f, l ) ).ToArray();
        var width = lines.Max( l => f.MeasureWidth( l ) ) + 4;
        var height = heights.Sum();

        if ( width <= 4 || height <= 0 )
        {
            return null;
        }

        var pixmap = new Pixmap( width, height );
        var top = 0;

        for ( var i = 0; i < lines.Length; i++ )
        {
            var x = 0;

            foreach ( var c in lines[i] )
            {
                var glyph = f.GlyphFor( c );
                glyph.DrawTo( pixmap, x, top + f.OffsetY( c ), hue, onlyGray );
                x += glyph.Width;
            }

            top += heights[i];
        }

        return pixmap;
    }

    /// <summary>Renders text with a Unicode font in a single color (multi-line on <c>\n</c>), laid out like ClassicUO.</summary>
    public Pixmap? RenderUnicode( int font, string text, uint color = 0xFFFFFFFF )
    {
        if ( GetUnicode( font ) is not { } f || string.IsNullOrEmpty( text ) )
        {
            return null;
        }

        var lines = SplitLines( text );
        var heights = lines.Select( l => LineHeightUnicode( f, l ) ).ToArray();
        var width = lines.Max( l => l.Sum( f.Advance ) ) + 4;
        var height = heights.Sum() + 4;

        if ( width <= 4 )
        {
            return null;
        }

        var pixmap = new Pixmap( width, height );
        var top = 0;

        for ( var i = 0; i < lines.Length; i++ )
        {
            var x = 0;

            foreach ( var c in lines[i] )
            {
                if ( c != ' ' && f.GetGlyph( c ) is { IsEmpty: false } glyph )
                {
                    glyph.DrawTo( pixmap, x + glyph.XOffset + 1, top + glyph.YOffset, color );
                }

                x += f.Advance( c );
            }

            top += heights[i];
        }

        return pixmap;
    }

    /// <summary>
    ///     Renders with either family: ASCII text takes the hue like the client (see <see cref="RenderAscii" />);
    ///     Unicode text is drawn in the hue's ramp color 30 (ClassicUO's default cell), or white without a hue.
    /// </summary>
    public Pixmap? Render( bool unicode, int font, string text, Hue? hue = null ) =>
        unicode ? RenderUnicode( font, text, hue?.ArgbAt( 30 ) ?? 0xFFFFFFFF ) : RenderAscii( font, text, hue );

    /// <summary>Maps a 16-bit glyph color through a hue (red channel indexes the ramp).</summary>
    public static uint HueColor( ushort c, Hue? hue, bool onlyGray )
    {
        if ( hue is null )
        {
            return Color16.ToArgb( c );
        }

        var r = ( c >> 10 ) & 0x1F;

        if ( onlyGray && ( r != ( ( c >> 5 ) & 0x1F ) || r != ( c & 0x1F ) ) )
        {
            return Color16.ToArgb( c );
        }

        return Color16.ToArgb( hue.Colors[r] );
    }

    private static string[] SplitLines( string text ) => text.Replace( "\r", "" ).Split( '\n' );

    private static int LineHeightAscii( AsciiFont font, string line )
    {
        var height = 0;

        foreach ( var c in line )
        {
            height = Math.Max( height, font.GlyphFor( c ).Height + font.OffsetY( c ) );
        }

        return height == 0 ? Math.Max( font.Height, 14 ) : height;
    }

    private static int LineHeightUnicode( UnicodeFont font, string line )
    {
        var height = 0;

        foreach ( var c in line )
        {
            if ( font.GetGlyph( c ) is { IsEmpty: false } g )
            {
                height = Math.Max( height, g.YOffset + g.Height );
            }
        }

        return height == 0 ? 14 : height;
    }
}

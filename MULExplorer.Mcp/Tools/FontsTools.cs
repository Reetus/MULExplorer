using System.Globalization;
using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Fonts tab: ASCII fonts (fonts.mul) and Unicode fonts (unifont*.mul).</summary>
public sealed class FontsTools : IToolProvider
{
    private static readonly JsonObject KindSchema = OneOf( "Font family: 'ascii' (fonts.mul) or 'unicode' (unifont*.mul).", "ascii", "unicode" );

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_fonts",
            "The ASCII fonts in fonts.mul (number, header, line height) and the Unicode font files present.",
            null,
            null,
            _ =>
            {
                var fonts = context.RequireData().Fonts;
                return Json( new JsonObject
                {
                    ["ascii"] = new JsonArray( fonts.Ascii.Select( f => ( JsonNode? )new JsonObject
                    {
                        ["font"] = f.Index,
                        ["header"] = f.Header,
                        ["height"] = f.Height,
                        ["characters"] = f.Glyphs.Count( g => !g.IsEmpty )
                    } ).ToArray() ),
                    ["unicode"] = new JsonArray( fonts.UnicodeFontIds.Select( i => ( JsonNode? )new JsonObject
                    {
                        ["font"] = i,
                        ["file"] = Fonts.UnicodeFileName( i )
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "get_font_info",
            "Details of a font, or of one character in it (width, height, offsets). Without 'char' returns the font summary and its character list (ASCII) or count (Unicode).",
            Props(
                ( "kind", KindSchema ),
                ( "font", Int( "Font number." ) ),
                ( "char", Str( "Optional character: a single character, or its code as decimal / 0x hex / U+XXXX." ) ) ),
            ["kind", "font"],
            args =>
            {
                var fonts = context.RequireData().Fonts;
                var font = args.RequireInt( "font" );
                var code = args.Has( "char" ) ? ParseChar( args.RequireString( "char" ) ) : ( int? )null;

                if ( IsUnicode( args ) )
                {
                    if ( fonts.GetUnicode( font ) is not { } uf )
                    {
                        return Error( $"Unicode font {font} not found. Available: {string.Join( ", ", fonts.UnicodeFontIds )}." );
                    }

                    if ( code is { } c )
                    {
                        return uf.GetGlyph( c ) is { } g
                            ? Json( new JsonObject
                            {
                                ["char"] = CharText( c ), ["code"] = $"U+{c:X4}", ["width"] = g.Width, ["height"] = g.Height,
                                ["xOffset"] = g.XOffset, ["yOffset"] = g.YOffset, ["advance"] = uf.Advance( ( char )c )
                            } )
                            : Error( $"Unicode font {font} has no glyph for U+{c:X4}." );
                    }

                    return Json( new JsonObject { ["kind"] = "unicode", ["font"] = font, ["file"] = uf.FileName, ["characters"] = uf.Characters.Count } );
                }

                if ( fonts.GetAscii( font ) is not { } af )
                {
                    return Error( $"ASCII font {font} not found (0..{fonts.Ascii.Count - 1})." );
                }

                if ( code is { } ac )
                {
                    if ( ac is < AsciiFont.FirstChar or > 0xFF )
                    {
                        return Error( "ASCII fonts cover codes 0x20..0xFF." );
                    }

                    var g = af.Glyphs[ac - AsciiFont.FirstChar];
                    return Json( new JsonObject
                    {
                        ["char"] = CharText( ac ), ["code"] = $"0x{ac:X2}", ["width"] = g.Width, ["height"] = g.Height,
                        ["yOffset"] = af.OffsetY( ( char )ac ), ["unknown"] = g.Unknown
                    } );
                }

                return Json( new JsonObject
                {
                    ["kind"] = "ascii",
                    ["font"] = font,
                    ["header"] = af.Header,
                    ["height"] = af.Height,
                    ["characters"] = new JsonArray( af.Glyphs.Select( ( g, i ) => ( JsonNode? )new JsonObject
                    {
                        ["code"] = $"0x{i + AsciiFont.FirstChar:X2}", ["char"] = CharText( i + AsciiFont.FirstChar ), ["width"] = g.Width, ["height"] = g.Height
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "render_text",
            "Renders text with a client font to PNG, laid out like the client (\\n for new lines). ASCII fonts are hued like the client (partial hue except fonts 5 and 8); Unicode text is drawn in the hue's color (white without one).",
            Props(
                ( "kind", KindSchema ),
                ( "font", Int( "Font number." ) ),
                ( "text", Str( "Text to render." ) ),
                ( "hue", Id( "Game hue number (0 = font's own colors / white)." ) ),
                ( "background", Str( "Optional opaque background as #RRGGBB (default transparent)." ) ) ),
            ["kind", "font", "text"],
            args =>
            {
                var data = context.RequireData();
                var font = args.RequireInt( "font" );
                var text = args.RequireString( "text" );
                var hue = args.GetInt( "hue", 0 );
                var pixmap = Render( data, IsUnicode( args ), font, text, hue );

                if ( pixmap is null )
                {
                    return Error( "Nothing rendered: unknown font or no drawable characters." );
                }

                if ( args.GetString( "background" ) is { Length: > 0 } bg )
                {
                    pixmap = OnBackground( pixmap, ParseColor( bg ) );
                }

                return Image( pixmap, new JsonObject { ["width"] = pixmap.Width, ["height"] = pixmap.Height } );
            } );
    }

    /// <summary>Shared renderer for the tool and the Fonts tab preview.</summary>
    public static Pixmap? Render( UOData data, bool unicode, int font, string text, int hue ) =>
        data.Fonts.Render( unicode, font, text, hue > 0 ? data.Hues.Get( hue ) : null );

    public static Pixmap OnBackground( Pixmap pixmap, uint argb )
    {
        var result = new Pixmap( pixmap.Width, pixmap.Height );
        Array.Fill( result.Pixels, argb | 0xFF00_0000u );
        result.Blit( pixmap, 0, 0 );
        return result;
    }

    private static bool IsUnicode( ToolArgs args ) =>
        args.RequireString( "kind" ).ToLowerInvariant() switch
        {
            "unicode" => true,
            "ascii" => false,
            var k => throw new ArgumentException( $"Unknown font kind '{k}'; use 'ascii' or 'unicode'." )
        };

    private static string CharText( int code ) => code is >= 0x20 and < 0xD800 or > 0xDFFF and <= 0xFFFF ? ( ( char )code ).ToString() : "";

    public static int ParseChar( string text )
    {
        if ( text.StartsWith( "U+", StringComparison.OrdinalIgnoreCase ) &&
             int.TryParse( text.AsSpan( 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u ) )
        {
            return u;
        }

        if ( text.Length == 1 )
        {
            return text[0];
        }

        return ToolArgs.ParseInt( text ) ?? throw new ArgumentException( $"'{text}' is not a character or character code." );
    }

    private static uint ParseColor( string text )
    {
        var s = text.TrimStart( '#' );
        return uint.TryParse( s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v ) && s.Length == 6
            ? v
            : throw new ArgumentException( $"Background must be #RRGGBB, got '{text}'." );
    }
}

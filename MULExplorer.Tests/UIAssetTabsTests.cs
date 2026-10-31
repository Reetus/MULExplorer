using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using MULExplorer.Mcp;
using UODataAccess.Imaging;
using UODataAccess.IO;
using UODataAccess.Loaders;

namespace MULExplorer.Tests;

/// <summary>Loader and MCP tests for the Gumps, Hues, Fonts, CliLoc and Light tabs.</summary>
public sealed class UIAssetTabsTests
{
    // ----- Fonts -----

    [Fact]
    public void Fonts_ParseSyntheticAsciiFile()
    {
        // One font: header, then 224 glyphs; glyph 'A' (index 0x21) is 2x1 with one transparent pixel.
        var bytes = new List<byte> { 0x01 };

        for ( var i = 0; i < AsciiFont.GlyphCount; i++ )
        {
            if ( i == 'A' - AsciiFont.FirstChar )
            {
                bytes.AddRange( [2, 1, 7, 0x00, 0x00, 0xFF, 0x7F] );
            }
            else
            {
                bytes.AddRange( [1, 1, 0, 0x00, 0x00] );
            }
        }

        bytes.Add( 0xEE ); // trailing partial font is ignored

        var fonts = Fonts.ParseAscii( bytes.ToArray() );

        var font = Assert.Single( fonts );
        Assert.Equal( 1, font.Header );
        var a = font.GlyphFor( 'A' );
        Assert.Equal( ( 2, 1, ( byte )7 ), ( a.Width, a.Height, a.Unknown ) );
        var image = a.ToPixmap()!;
        Assert.Equal( 0u, image[0, 0] );
        Assert.Equal( 0xFFFFFFFFu, image[1, 0] );
        Assert.Same( font.Glyphs[0], font.GlyphFor( '\t' ) );
    }

    [Fact]
    public void Fonts_UnicodeGlyphBitsAreMsbFirst()
    {
        var glyph = new UnicodeGlyph( 'x', 0, 0, 10, 2, [0b1000_0000, 0b0100_0000, 0, 0b0100_0000] );

        Assert.True( glyph.IsSet( 0, 0 ) );
        Assert.True( glyph.IsSet( 9, 0 ) );
        Assert.False( glyph.IsSet( 1, 0 ) );
        Assert.True( glyph.IsSet( 9, 1 ) );
        Assert.False( glyph.IsSet( 7, 1 ) );
        Assert.Equal( 3, glyph.ToPixmap()!.Pixels.Count( p => p != 0 ) );
    }

    [Fact]
    public void Fonts_LoadRealAsciiAndUnicode()
    {
        var fonts = UOTestData.Require().Fonts;

        Assert.True( fonts.Ascii.Count >= 10 );
        Assert.Contains( 0, fonts.UnicodeFontIds );
        Assert.All( fonts.Ascii, f => Assert.True( f.Height > 0 ) );

        var unicode = fonts.GetUnicode( 0 )!;
        Assert.True( unicode.HasGlyph( 'A' ) );
        Assert.Contains( ( int )'A', unicode.Characters );
        Assert.False( unicode.GetGlyph( 'A' )!.IsEmpty );

        var ascii = fonts.RenderAscii( 3, "Hello\nWorld" )!;
        Assert.True( ascii.Width > 20 && ascii.Height > fonts.Ascii[3].Height );
        Assert.NotNull( ascii.OpaqueBounds() );

        var uni = fonts.RenderUnicode( 1, "Hello", 0xFFFF0000 )!;
        Assert.Contains( 0xFFFF0000u, uni.Pixels );
    }

    [Fact]
    public void Fonts_AsciiHueRecolorsGrayPixels()
    {
        var data = UOTestData.Require();
        var plain = data.Fonts.RenderAscii( 0, "Hue" )!;
        var hued = data.Fonts.RenderAscii( 0, "Hue", data.Hues.Get( 0x21 ) )!;

        Assert.Equal( ( plain.Width, plain.Height ), ( hued.Width, hued.Height ) );
        Assert.NotEqual( plain.Pixels, hued.Pixels );
    }

    // ----- Lights -----

    [Fact]
    public void Lights_IntensityMappingMatchesClassicUO()
    {
        Assert.Equal( 0, Lights.Intensity( 0 ) );
        Assert.Equal( 31, Lights.Intensity( 31 ) );
        Assert.Equal( 0, Lights.Intensity( -1 ) );
        Assert.Equal( 16, Lights.Intensity( -17 ) );
    }

    [Fact]
    public void Lights_ReadSyntheticFiles()
    {
        using var dir = new TempDir();
        var idx = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian( idx, 0 );
        BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( 4 ), 6 );
        BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( 8 ), ( 3 << 16 ) | 2 );   // width 3, height 2
        BinaryPrimitives.WriteInt32LittleEndian( idx.AsSpan( 12 ), -1 );
        File.WriteAllBytes( dir.File( "lightidx.mul" ), idx );
        File.WriteAllBytes( dir.File( "light.mul" ), [0, 10, 31, 0xFF, 0xEF, 5] );

        var lights = new Lights( new UOFileSet( dir.Path ) );

        Assert.Equal( 2, lights.Count );
        Assert.Equal( [0], lights.ValidIds );
        Assert.Equal( ( 3, 2 ), lights.GetSize( 0 ) );
        var image = lights.Get( 0 )!;
        Assert.Equal( 0u, image[0, 0] );
        Assert.Equal( 0xFFFFFFFFu, image[2, 0] );
        var stats = lights.GetStats( 0 )!.Value;
        Assert.Equal( ( -17, 31, 4 ), ( stats.Min, stats.Max, stats.LitPixels ) );
        lights.Dispose();
    }

    [Fact]
    public void Lights_LoadRealDataAndPreview()
    {
        var data = UOTestData.Require();
        var lights = data.Lights;

        Assert.NotEmpty( lights.ValidIds );
        var id = lights.ValidIds[0];
        var (w, h) = lights.GetSize( id )!.Value;
        var map = lights.Get( id )!;
        Assert.Equal( ( w, h ), ( map.Width, map.Height ) );

        var preview = lights.RenderPreview( id, data.Art.GetLand( 3 ), data.Art.GetItem( 0x0B20 ) )!;
        Assert.True( preview.Width >= w && preview.Height >= h );
        var centre = preview[preview.Width / 2, preview.Height / 2 + 30];
        var corner = preview[0, 0];
        Assert.True( Brightness( centre ) >= Brightness( corner ) );
    }

    private static int Brightness( uint argb ) => ( int )( ( ( argb >> 16 ) & 0xFF ) + ( ( argb >> 8 ) & 0xFF ) + ( argb & 0xFF ) );

    // ----- Gumps / Hues / Cliloc -----

    [Fact]
    public void GumpNames_LoadFromEmbeddedJson()
    {
        Assert.True( GumpNames.All.Count > 900 );
        Assert.Equal( "backpack", GumpNames.NameOf( 60 ) );
        Assert.True( GumpNames.TryGet( 60, out var g ) && g!.HasTag( "container" ) );
        Assert.Contains( "equipment", GumpNames.Tags );
        Assert.Null( GumpNames.NameOf( -1 ) );
    }

    [Fact]
    public void Cliloc_IndexOfSearchAndLanguageCache()
    {
        var data = UOTestData.Require();
        var cliloc = Cliloc.Get( data.Files, "enu" );

        Assert.Same( cliloc, Cliloc.Get( data.Files, "ENU" ) );
        var first = cliloc.Entries[10];
        Assert.Equal( 10, cliloc.IndexOf( first.Number ) );
        Assert.True( cliloc.IndexOf( first.Number + 1 ) is >= 10 or < 0 );
        Assert.Contains( cliloc.Search( first.Number.ToString( System.Globalization.CultureInfo.InvariantCulture ) ), e => e.Number == first.Number );
        Assert.NotEmpty( cliloc.Search( "gold", regex: false ) );
        Assert.NotEmpty( cliloc.Search( "^gold", regex: true ) );

        foreach ( var language in Cliloc.AvailableLanguages( data.Files ) )
        {
            Assert.True( Cliloc.Get( data.Files, language ).Count > 0, language );
        }
    }

    // ----- MCP -----

    private static ToolRegistry Registry() => MULExplorerMcpServer.CreateRegistry( new McpContext { Data = UOTestData.Require() } );

    private static IReadOnlyDictionary<string, JsonElement> Args( JsonObject values ) =>
        JsonSerializer.Deserialize( values.ToJsonString(), McpTestJson.Default.DictionaryStringJsonElement )!;

    private static async Task<JsonNode> CallJson( string tool, JsonObject? args = null )
    {
        var result = await McpTestExtensions.Call( Registry(), tool, args is null ? null : Args( args ) );
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    private static async Task<byte[]> CallImage( string tool, JsonObject args )
    {
        var result = await McpTestExtensions.Call( Registry(), tool, Args( args ) );
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        var image = Assert.IsType<ImageContentBlock>( result.Content[0] );
        Assert.Equal( "image/png", image.MimeType );
        return image.DecodedData.ToArray();
    }

    [Fact]
    public async Task Mcp_GumpTools()
    {
        var search = await CallJson( "search_gumps", new() { ["tag"] = "container", ["limit"] = 5 } );
        Assert.True( search["total"]!.GetValue<int>() > 0 );
        var id = search["gumps"]![0]!["id"]!.GetValue<string>();

        var info = await CallJson( "get_gump_info", new() { ["id"] = id } );
        Assert.NotNull( info["name"] );
        Assert.True( info["width"]!.GetValue<int>() > 0 );

        var png = await CallImage( "get_gump_image", new() { ["id"] = id, ["hue"] = 0x21 } );
        Assert.Equal( 0x89, png[0] );

        Assert.Contains( "equipment", ( await CallJson( "list_gump_tags" ) ).AsArray().Select( n => n!.GetValue<string>() ) );
    }

    [Fact]
    public async Task Mcp_HueTools()
    {
        var hue = await CallJson( "get_hue", new() { ["hue"] = "0x21" } );
        Assert.Equal( 0x21, hue["hue"]!.GetValue<int>() );
        Assert.Equal( 32, hue["colors"]!.AsArray().Count );

        var search = await CallJson( "search_hues", new() { ["query"] = "33" } );
        Assert.Contains( search["hues"]!.AsArray(), h => h!["hue"]!.GetValue<int>() == 33 );

        var png = await CallImage( "get_hue_ramp_image", new() { ["hue"] = 0x21 } );
        Assert.Equal( 0x89, png[0] );
    }

    [Fact]
    public async Task Mcp_FontTools()
    {
        var list = await CallJson( "list_fonts" );
        Assert.True( list["ascii"]!.AsArray().Count >= 10 );

        var ascii = await CallJson( "get_font_info", new() { ["kind"] = "ascii", ["font"] = 3, ["char"] = "A" } );
        Assert.True( ascii["width"]!.GetValue<int>() > 0 );

        var uni = await CallJson( "get_font_info", new() { ["kind"] = "unicode", ["font"] = 0, ["char"] = "U+0041" } );
        Assert.True( uni["height"]!.GetValue<int>() > 0 );

        var png = await CallImage( "render_text", new() { ["kind"] = "unicode", ["font"] = 1, ["text"] = "Britannia", ["hue"] = 0x35, ["background"] = "#000000" } );
        Assert.Equal( 0x89, png[0] );
    }

    [Fact]
    public async Task Mcp_ClilocTools()
    {
        var languages = ( await CallJson( "list_cliloc_languages" ) ).AsArray().Select( n => n!.GetValue<string>() ).ToList();
        Assert.Contains( "enu", languages );

        var search = await CallJson( "search_cliloc", new() { ["query"] = "gold coin", ["limit"] = 3 } );
        var number = search["entries"]![0]!["number"]!.GetValue<int>();

        var entry = await CallJson( "get_cliloc", new() { ["number"] = number, ["language"] = languages.Last() } );
        Assert.Equal( number, entry["number"]!.GetValue<int>() );
    }

    [Fact]
    public async Task Mcp_LightTools()
    {
        var list = ( await CallJson( "list_lights" ) ).AsArray();
        Assert.NotEmpty( list );
        var id = list[0]!["id"]!.GetValue<int>();

        Assert.Equal( 0x89, ( await CallImage( "get_light_image", new() { ["id"] = id } ) )[0] );
        Assert.Equal( 0x89, ( await CallImage( "get_light_image", new() { ["id"] = id, ["in_game"] = true } ) )[0] );
    }
}

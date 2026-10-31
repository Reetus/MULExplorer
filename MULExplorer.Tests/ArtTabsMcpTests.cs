using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using MULExplorer.Mcp;

namespace MULExplorer.Tests;

/// <summary>MCP tools of the Multis, Land Tiles, Textures, TileData and RadarColor tabs.</summary>
public sealed class ArtTabsMcpTests
{
    private static readonly string[] Tools =
    [
        "list_multis", "get_multi", "get_multi_image", "get_land", "get_land_image", "search_land",
        "get_texture", "get_texture_image", "list_textures", "get_tiledata_info", "get_radar_color", "list_radar_colors"
    ];

    private static ToolRegistry Registry() => MULExplorerMcpServer.CreateRegistry( new McpContext { Data = UOTestData.Require() } );

    private static IReadOnlyDictionary<string, JsonElement> Args( Dictionary<string, object> values ) =>
        JsonSerializer.Deserialize( JsonSerializer.Serialize( values, McpTestJson.Default.DictionaryStringObject ), McpTestJson.Default.DictionaryStringJsonElement )!;

    private static async Task<JsonNode> CallJson( ToolRegistry registry, string tool, Dictionary<string, object>? args = null )
    {
        var result = await McpTestExtensions.Call( registry, tool, args is null ? null : Args( args ) );
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    private static async Task AssertPng( ToolRegistry registry, string tool, Dictionary<string, object> args )
    {
        var result = await McpTestExtensions.Call( registry, tool, Args( args ) );
        var image = Assert.IsType<ImageContentBlock>( result.Content[0] );
        Assert.Equal( "image/png", image.MimeType );
        Assert.Equal( 0x89, image.DecodedData.Span[0] );
    }

    [Fact]
    public void Registry_HasTabTools()
    {
        var registry = MULExplorerMcpServer.CreateRegistry( new McpContext() );

        Assert.All( Tools, t => Assert.True( registry.Contains( t ), t ) );
    }

    [Fact]
    public async Task Multis_ListGetAndImage()
    {
        var registry = Registry();

        var list = await CallJson( registry, "list_multis", new() { ["name"] = "tower", ["limit"] = 5 } );
        Assert.True( list["total"]!.GetValue<int>() > 0 );
        Assert.All( list["multis"]!.AsArray(), m => Assert.Contains( "tower", m!["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase ) );

        var multi = await CallJson( registry, "get_multi", new() { ["id"] = "0x96" } );
        Assert.Equal( "Large Marble Patio House", multi["name"]!.GetValue<string>() );
        Assert.Equal( multi["component_count"]!.GetValue<int>(), multi["components"]!.AsArray().Count );

        await AssertPng( registry, "get_multi_image", new() { ["id"] = 0x96, ["max_height"] = 10 } );

        var missing = await McpTestExtensions.Call( registry, "get_multi", Args( new() { ["id"] = 0x2700 } ) );
        Assert.True( missing.IsError is true );
    }

    [Fact]
    public async Task Land_GetSearchAndImages()
    {
        var registry = Registry();

        var grass = await CallJson( registry, "get_land", new() { ["id"] = 3 } );
        Assert.Equal( "grass", grass["name"]!.GetValue<string>() );
        Assert.True( grass["has_texture"]!.GetValue<bool>() );

        var search = await CallJson( registry, "search_land", new() { ["name"] = "water", ["flags"] = "Wet", ["limit"] = 5 } );
        Assert.True( search["total"]!.GetValue<int>() > 0 );

        await AssertPng( registry, "get_land_image", new() { ["id"] = 3 } );
        await AssertPng( registry, "get_land_image", new() { ["id"] = 3, ["as_texture"] = "true" } );
    }

    [Fact]
    public async Task Textures_GetListAndImage()
    {
        var registry = Registry();

        var list = await CallJson( registry, "list_textures", new() { ["limit"] = 3 } );
        Assert.True( list["total"]!.GetValue<int>() > 1000 );

        var texture = await CallJson( registry, "get_texture", new() { ["id"] = 3 } );
        Assert.True( texture["valid"]!.GetValue<bool>() );
        Assert.Contains( texture["used_by_land"]!.AsArray(), l => l!.GetValue<string>() == "0x0003" );

        await AssertPng( registry, "get_texture_image", new() { ["id"] = 3 } );
    }

    [Fact]
    public async Task TileDataInfo_AndRadarColors()
    {
        var registry = Registry();
        var data = UOTestData.Require();

        var info = await CallJson( registry, "get_tiledata_info" );
        Assert.Equal( 0x4000, info["land_count"]!.GetValue<int>() );
        Assert.True( info["flag_counts"]!["Wet"]!["land"]!.GetValue<int>() > 0 );

        var color = await CallJson( registry, "get_radar_color", new() { ["id"] = 3, ["kind"] = "land" } );
        Assert.Equal( $"0x{data.RadarColors.GetLandColor( 3 ):X4}", color["color"]!.GetValue<string>() );
        Assert.Equal( "grass", color["name"]!.GetValue<string>() );

        var run = await CallJson( registry, "list_radar_colors", new() { ["start"] = "0x0EED", ["limit"] = 3 } );
        Assert.Equal( 3, run["colors"]!.AsArray().Count );
        Assert.Equal( "gold coin", run["colors"]![0]!["name"]!.GetValue<string>() );
    }
}

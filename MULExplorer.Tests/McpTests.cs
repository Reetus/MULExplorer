using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MULExplorer.Mcp;

namespace MULExplorer.Tests;

public sealed class McpTests
{
    private static ToolRegistry Registry( out McpContext context )
    {
        context = new McpContext { Data = UOTestData.Require() };
        return MULExplorerMcpServer.CreateRegistry( context );
    }

    private static IReadOnlyDictionary<string, JsonElement> Args( object values ) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>( JsonSerializer.Serialize( values, McpTestJson.Default.DictionaryStringObject ), McpTestJson.Default.DictionaryStringJsonElement )!;

    private static JsonNode TextJson( CallToolResult result )
    {
        Assert.True( result.IsError is not true, ( result.Content.FirstOrDefault() as TextContentBlock )?.Text );
        return JsonNode.Parse( result.Content.OfType<TextContentBlock>().First().Text )!;
    }

    [Fact]
    public void Registry_HasUniqueSnakeCaseNames()
    {
        var registry = MULExplorerMcpServer.CreateRegistry( new McpContext() );
        var names = registry.Tools.Select( t => t.Name ).ToList();

        Assert.Equal( names.Count, names.Distinct().Count() );
        Assert.All( names, n => Assert.Matches( "^[a-z][a-z0-9_]*$", n ) );
        Assert.All( registry.Tools, t => Assert.False( string.IsNullOrWhiteSpace( t.Description ) ) );
    }

    [Fact]
    public async Task DataTools_FailCleanlyWithoutFolder()
    {
        var registry = MULExplorerMcpServer.CreateRegistry( new McpContext() );

        var result = await McpTestExtensions.Call( registry, "get_item", Args( new Dictionary<string, object> { ["id"] = 1 } ) );

        Assert.True( result.IsError is true );
    }

    [Fact]
    public async Task UITools_FailCleanlyHeadless()
    {
        var registry = Registry( out _ );

        var result = await McpTestExtensions.Call( registry, "ui_list_tabs", null );

        Assert.True( result.IsError is true );
        Assert.Contains( "headless", ( ( TextContentBlock )result.Content[0] ).Text );
    }

    [Fact]
    public async Task GetItem_AcceptsHexString()
    {
        var registry = Registry( out _ );

        var json = TextJson( await McpTestExtensions.Call( registry, "get_item", Args( new Dictionary<string, object> { ["id"] = "0x0EED" } ) ) );

        Assert.Equal( "gold coin", json["name"]!.GetValue<string>() );
        Assert.Equal( "0x0EED", json["id"]!.GetValue<string>() );
    }

    [Fact]
    public async Task GetItemImage_ReturnsPng()
    {
        var registry = Registry( out _ );

        var result = await McpTestExtensions.Call( registry, "get_item_image", Args( new Dictionary<string, object> { ["id"] = 0x0EED, ["hue"] = 0x21 } ) );

        var image = Assert.IsType<ImageContentBlock>( result.Content[0] );
        Assert.Equal( "image/png", image.MimeType );
        Assert.Equal( 0x89, image.DecodedData.Span[0] );
    }

    [Fact]
    public async Task SearchItems_FiltersByNameAndFlags()
    {
        var registry = Registry( out _ );

        var json = TextJson( await McpTestExtensions.Call( registry, "search_items", Args( new Dictionary<string, object> { ["name"] = "coin", ["flags"] = "Generic", ["limit"] = 5 } ) ) );

        Assert.True( json["total"]!.GetValue<int>() > 0 );
        Assert.All( json["items"]!.AsArray(), i => Assert.Contains( "coin", i!["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase ) );
        Assert.True( json["items"]!.AsArray().Count <= 5 );
    }

    [Fact]
    public async Task ClientInfo_ReportsAssets()
    {
        var registry = Registry( out var context );

        var json = TextJson( await McpTestExtensions.Call( registry, "get_client_info", null ) );

        Assert.Equal( context.Data!.Folder, json["folder"]!.GetValue<string>() );
        Assert.DoesNotContain( "unavailable", json["assets"]!["art"]!.GetValue<string>() );
    }

    /// <summary>End to end over Streamable HTTP using the SDK's own client.</summary>
    [Fact]
    public async Task Http_ServesToolsToSdkClient()
    {
        var context = new McpContext { Data = UOTestData.Require() };
        await using var server = new McpHttpServer( context );
        await server.StartAsync( 0, TestContext.Current.CancellationToken );

        var transport = new HttpClientTransport( new HttpClientTransportOptions { Endpoint = new Uri( server.Url ) } );
        await using var client = await McpClient.CreateAsync( transport, cancellationToken: TestContext.Current.CancellationToken );

        var tools = await client.ListToolsAsync( cancellationToken: TestContext.Current.CancellationToken );
        Assert.Contains( tools, t => t.Name == "get_item" );

        var result = await client.CallToolAsync( "get_item", new Dictionary<string, object?> { ["id"] = "0x0EED" }, cancellationToken: TestContext.Current.CancellationToken );
        Assert.Contains( "gold coin", ( ( TextContentBlock )result.Content[0] ).Text );
    }

    /// <summary>The stdio code path, run over in-memory pipes instead of the console.</summary>
    [Fact]
    public async Task Stream_ServesToolsToSdkClient()
    {
        var context = new McpContext { Data = UOTestData.Require() };
        var options = MULExplorerMcpServer.CreateOptions( MULExplorerMcpServer.CreateRegistry( context ) );
        var toServer = new System.IO.Pipelines.Pipe();
        var toClient = new System.IO.Pipelines.Pipe();
        var ct = TestContext.Current.CancellationToken;

        var serverTransport = new ModelContextProtocol.Server.StreamServerTransport( toServer.Reader.AsStream(), toClient.Writer.AsStream() );
        await using var server = ModelContextProtocol.Server.McpServer.Create( serverTransport, options );
        var run = server.RunAsync( ct );

        var clientTransport = new StreamClientTransport( toServer.Writer.AsStream(), toClient.Reader.AsStream() );
        await using ( var client = await McpClient.CreateAsync( clientTransport, cancellationToken: ct ) )
        {
            Assert.Equal( MULExplorerMcpServer.Name, client.ServerInfo.Name );
            var result = await client.CallToolAsync( "get_item", new Dictionary<string, object?> { ["id"] = 0x0EED }, cancellationToken: ct );
            Assert.Contains( "gold coin", ( ( TextContentBlock )result.Content[0] ).Text );
        }

        toServer.Writer.Complete();
        await run.WaitAsync( TimeSpan.FromSeconds( 10 ), ct ).ContinueWith( _ => { }, ct );
    }
}

[System.Text.Json.Serialization.JsonSerializable( typeof( Dictionary<string, object> ) )]
[System.Text.Json.Serialization.JsonSerializable( typeof( Dictionary<string, JsonElement> ) )]
[System.Text.Json.Serialization.JsonSerializable( typeof( int ) )]
[System.Text.Json.Serialization.JsonSerializable( typeof( string ) )]
internal sealed partial class McpTestJson : System.Text.Json.Serialization.JsonSerializerContext;

internal static class McpTestExtensions
{
    /// <summary>Calls a tool with the test's cancellation token.</summary>
    public static ValueTask<CallToolResult> Call( ToolRegistry registry, string name, IReadOnlyDictionary<string, JsonElement>? args ) =>
        registry.CallAsync( name, args, TestContext.Current.CancellationToken );
}

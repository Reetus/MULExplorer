using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MULExplorer.Mcp.Tools;
using System.Net;

namespace MULExplorer.Mcp;

/// <summary>
///     Builds the MCP server on the official C# SDK. Tools come from <see cref="ToolRegistry" />
///     through the low-level list/call handlers, so nothing relies on attribute reflection.
/// </summary>
public static class MULExplorerMcpServer
{
    public const string Name = "mul-explorer";
    public const int DefaultPort = 5006;

    private const string Instructions =
        "Read-only explorer for Ultima Online client data files (MUL/UOP). Ids accept decimal or 0x hex. " +
        "Start with get_client_info. Data tools (items, land, gumps, hues, cliloc, multis, animations, maps, sounds...) work headless; " +
        "ui_* tools drive the MULExplorer window (select tabs/entries, read tab state, take screenshots) and need the GUI.";

    /// <summary>Every tool provider. Each tab owns one so features stay in separate files.</summary>
    public static IReadOnlyList<IToolProvider> Providers { get; } =
    [
        new ClientTools(),
        new MultisTools(),
        new AnimationsTools(),
        new ItemsTools(),
        new LandTilesTools(),
        new TexturesTools(),
        new GumpsTools(),
        new SoundsTools(),
        new HuesTools(),
        new FontsTools(),
        new ClilocTools(),
        new MapTools(),
        new LightTools(),
        new SpeechTools(),
        new SkillsTools(),
        new AnimDataTools(),
        new MultiMapTools(),
        new DressTools(),
        new TileDataTools(),
        new RadarColorTools(),
        new SkillGroupTools(),
        new VerdataTools(),
        new UITools()
    ];

    public static ToolRegistry CreateRegistry( McpContext context )
    {
        var registry = new ToolRegistry( context );

        foreach ( var provider in Providers )
        {
            provider.Register( registry );
        }

        return registry;
    }

    public static McpServerOptions CreateOptions( ToolRegistry registry ) => new()
    {
        ServerInfo = new Implementation { Name = Name, Version = typeof( MULExplorerMcpServer ).Assembly.GetName().Version?.ToString( 3 ) ?? "1.0.0" },
        ServerInstructions = Instructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = ( _, _ ) => ValueTask.FromResult( new ListToolsResult { Tools = registry.Tools.ToList() } ),
            CallToolHandler = ( request, token ) => registry.CallAsync( request.Params?.Name ?? "", request.Params?.Arguments as IReadOnlyDictionary<string, System.Text.Json.JsonElement>, token )
        }
    };

    /// <summary>Serves MCP over stdin/stdout until the client disconnects. Nothing else may write to stdout.</summary>
    public static async Task RunStdioAsync( McpContext context, CancellationToken token = default )
    {
        var options = CreateOptions( CreateRegistry( context ) );
        var transport = new StdioServerTransport( options, NullLoggerFactory.Instance );
        await using var server = McpServer.Create( transport, options, NullLoggerFactory.Instance );
        await server.RunAsync( token );
    }
}

/// <summary>
///     Streamable HTTP endpoint on <c>http://127.0.0.1:{port}/mcp</c> (loopback only), hosted by a
///     slim Kestrel so it can run inside the GUI process or headless.
/// </summary>
public sealed class McpHttpServer : IAsyncDisposable
{
    private WebApplication? _app;

    public McpHttpServer( McpContext context )
    {
        Context = context;
    }

    public McpContext Context { get; }

    public int Port { get; private set; }

    public bool IsRunning => _app is not null;

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Starts listening. Pass port 0 to pick a free port (see <see cref="Port" />).</summary>
    public async Task StartAsync( int port, CancellationToken token = default )
    {
        if ( _app is not null )
        {
            return;
        }

        var registry = MULExplorerMcpServer.CreateRegistry( Context );
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel( k => k.Listen( IPAddress.Loopback, port ) );

        builder.Services
            .AddMcpServer( o =>
            {
                var template = MULExplorerMcpServer.CreateOptions( registry );
                o.ServerInfo = template.ServerInfo;
                o.ServerInstructions = template.ServerInstructions;
                o.Capabilities = template.Capabilities;
                o.Handlers = template.Handlers;
            } )
            .WithHttpTransport( o => o.Stateless = true );

        var app = builder.Build();
        app.MapMcp( "/mcp" );
        app.MapMcp( "/" );

        await app.StartAsync( token );
        _app = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Port = address is not null && Uri.TryCreate( address, UriKind.Absolute, out var uri ) ? uri.Port : port;
    }

    public async Task StopAsync()
    {
        if ( _app is { } app )
        {
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

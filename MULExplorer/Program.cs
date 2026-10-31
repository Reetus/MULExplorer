using Avalonia;
using MULExplorer.Mcp;
using MULExplorer.Services;
using UODataAccess;
using Velopack;

namespace MULExplorer;

internal static class Program
{
    /// <summary>
    ///     <code>
    /// MULExplorer [--uo-folder PATH]                       GUI (MCP HTTP auto-starts per settings)
    /// MULExplorer --mcp-stdio [--uo-folder PATH]           headless MCP over stdio
    /// MULExplorer --mcp-http [--mcp-port N] [--uo-folder PATH]   headless MCP over HTTP
    /// </code>
    /// </summary>
    [STAThread]
    public static int Main( string[] args )
    {
        // Must run first: handles Velopack install/update hooks and exits early when invoked for one.
        VelopackApp.Build().Run();

        var folder = Option( args, "--uo-folder" );

        if ( args.Contains( "--mcp-stdio" ) || args.Contains( "--mcp-http" ) )
        {
            return RunHeadless( args, folder ).GetAwaiter().GetResult();
        }

        App.CommandLineFolder = folder;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunHeadless( string[] args, string? folder )
    {
        var settings = Settings.Load();
        var resolved = settings.ResolveUOFolder( folder );
        using var data = resolved is null ? null : new UOData( resolved );
        var context = new McpContext { Data = data };

        if ( args.Contains( "--mcp-stdio" ) )
        {
            await MULExplorerMcpServer.RunStdioAsync( context );
            return 0;
        }

        var port = int.TryParse( Option( args, "--mcp-port" ), out var p ) ? p : settings.McpPort;
        await using var server = new McpHttpServer( context );
        await server.StartAsync( port );
        await Console.Error.WriteLineAsync( $"MULExplorer MCP listening on {server.Url} (folder: {resolved ?? "none"}). Ctrl+C to exit." );

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += ( _, e ) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            await Task.Delay( Timeout.Infinite, stop.Token );
        }
        catch ( OperationCanceledException )
        {
        }

        return 0;
    }

    private static string? Option( string[] args, string name )
    {
        var i = Array.IndexOf( args, name );
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

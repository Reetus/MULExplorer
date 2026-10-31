using System.Text.Json;
using System.Text.Json.Serialization;

namespace MULExplorer.Services;

public sealed class Settings
{
    public string? UOFolder { get; set; }

    /// <summary>"Dark", "Light" or "Default" (follow the OS).</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>"Mica", "Acrylic" or "Solid". Unsupported platforms fall back to solid automatically.</summary>
    public string Backdrop { get; set; } = "Mica";

    public int McpPort { get; set; } = Mcp.MULExplorerMcpServer.DefaultPort;

    public bool McpAutoStart { get; set; } = true;

    public string? LastTab { get; set; }

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    /// <summary>
    ///     Settings file location. <c>MULEXPLORER_SETTINGS</c> overrides it so tests and parallel
    ///     instances never touch the user's real settings.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable( "MULEXPLORER_SETTINGS" ) is { Length: > 0 } custom
            ? custom
            : Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "MULExplorer", "settings.json" );

    public static Settings Load( string? path = null )
    {
        path ??= DefaultPath;

        try
        {
            if ( File.Exists( path ) )
            {
                return JsonSerializer.Deserialize( File.ReadAllText( path ), SettingsJsonContext.Default.Settings ) ?? new Settings();
            }
        }
        catch ( Exception e ) when ( e is IOException or JsonException or UnauthorizedAccessException )
        {
            // A corrupt or unreadable file falls back to defaults.
        }

        return new Settings();
    }

    public void Save( string? path = null )
    {
        path ??= DefaultPath;

        try
        {
            Directory.CreateDirectory( Path.GetDirectoryName( path )! );
            File.WriteAllText( path, JsonSerializer.Serialize( this, SettingsJsonContext.Default.Settings ) );
        }
        catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
        {
            // Settings are a convenience; never crash over them.
        }
    }

    /// <summary>
    ///     Resolves the client folder: <c>--uo-folder</c>, then <c>UO_DATA_FOLDER</c>, then saved settings.
    /// </summary>
    public string? ResolveUOFolder( string? commandLine ) =>
        new[] { commandLine, Environment.GetEnvironmentVariable( "UO_DATA_FOLDER" ), UOFolder }
            .FirstOrDefault( IsClientFolder );

    public static bool IsClientFolder( string? path ) =>
        !string.IsNullOrWhiteSpace( path ) && Directory.Exists( path ) &&
        Directory.EnumerateFiles( path ).Any( f => Path.GetFileName( f ).Equals( "tiledata.mul", StringComparison.OrdinalIgnoreCase ) );
}

[JsonSourceGenerationOptions( WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof( Settings ) )]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

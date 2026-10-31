using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using UODataAccess.Imaging;

namespace MULExplorer.Mcp;

public delegate ValueTask<CallToolResult> ToolHandler( ToolArgs args, CancellationToken token );

/// <summary>A feature area (usually one tab) that contributes tools.</summary>
public interface IToolProvider
{
    void Register( ToolRegistry registry );
}

/// <summary>
///     The tool table behind <c>tools/list</c> and <c>tools/call</c>. Tools are plain
///     delegates with hand-built JSON schemas, which keeps the server free of reflection and so
///     NativeAOT-safe.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, (Tool Tool, ToolHandler Handler)> _tools = new( StringComparer.Ordinal );

    public ToolRegistry( McpContext context )
    {
        Context = context;
    }

    public McpContext Context { get; }

    public IEnumerable<Tool> Tools => _tools.Values.Select( t => t.Tool );

    public int Count => _tools.Count;

    public bool Contains( string name ) => _tools.ContainsKey( name );

    public void Add( string name, string description, JsonObject? properties, string[]? required, ToolHandler handler, bool readOnly = true )
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject() };

        if ( required is { Length: > 0 } )
        {
            schema["required"] = new JsonArray( required.Select( r => ( JsonNode? )r ).ToArray() );
        }

        var tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonDocument.Parse( schema.ToJsonString() ).RootElement.Clone(),
            Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, OpenWorldHint = false }
        };

        if ( !_tools.TryAdd( name, ( tool, handler ) ) )
        {
            throw new InvalidOperationException( $"Duplicate MCP tool '{name}'." );
        }
    }

    /// <summary>Synchronous convenience overload for tools that only read data.</summary>
    public void Add( string name, string description, JsonObject? properties, string[]? required, Func<ToolArgs, CallToolResult> handler ) =>
        Add( name, description, properties, required, ( args, _ ) => ValueTask.FromResult( handler( args ) ) );

    public async ValueTask<CallToolResult> CallAsync( string name, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken token = default )
    {
        if ( !_tools.TryGetValue( name, out var entry ) )
        {
            return Error( $"Unknown tool: {name}" );
        }

        try
        {
            return await entry.Handler( new ToolArgs( arguments ), token );
        }
        catch ( Exception e ) when ( e is not OperationCanceledException )
        {
            return Error( e.Message );
        }
    }

    // ----- schema helpers -----

    public static JsonObject Props( params (string Name, JsonObject Schema)[] props )
    {
        var obj = new JsonObject();

        foreach ( var (name, schema) in props )
        {
            obj[name] = schema.Parent is null ? schema : schema.DeepClone();
        }

        return obj;
    }

    public static JsonObject Str( string description ) => new() { ["type"] = "string", ["description"] = description };

    /// <summary>Integer that may also be given as a "0x" hex string.</summary>
    public static JsonObject Id( string description ) => new()
    {
        ["type"] = new JsonArray( "integer", "string" ),
        ["description"] = description + " Decimal or 0x-prefixed hex."
    };

    public static JsonObject Int( string description ) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Bool( string description ) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject OneOf( string description, params string[] values ) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray( values.Select( v => ( JsonNode? )v ).ToArray() )
    };

    // ----- result helpers -----

    public static CallToolResult Text( string text ) => new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Json( JsonNode node ) => Text( node.ToJsonString() );

    public static CallToolResult Error( string message ) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    public static CallToolResult Image( Pixmap pixmap, JsonNode? info = null ) => Image( PngEncoder.Encode( pixmap ), info );

    public static CallToolResult Image( byte[] png, JsonNode? info = null )
    {
        var result = new CallToolResult { Content = [ImageContentBlock.FromBytes( png, "image/png" )] };

        if ( info is not null )
        {
            result.Content.Add( new TextContentBlock { Text = info.ToJsonString() } );
        }

        return result;
    }

    public static string Hex( int value ) => $"0x{value:X4}";

}

/// <summary>Typed access to tool arguments. Ids accept numbers, decimal strings and 0x hex strings.</summary>
public readonly struct ToolArgs( IReadOnlyDictionary<string, JsonElement>? args )
{
    public bool Has( string name ) => args is not null && args.TryGetValue( name, out var e ) && e.ValueKind != JsonValueKind.Null;

    public string? GetString( string name ) =>
        args is not null && args.TryGetValue( name, out var e )
            ? e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => e.GetRawText()
            }
            : null;

    public string RequireString( string name ) =>
        GetString( name ) is { Length: > 0 } s ? s : throw new ArgumentException( $"Missing required argument '{name}'." );

    public int? GetInt( string name )
    {
        if ( args is null || !args.TryGetValue( name, out var e ) )
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt32( out var i ) => i,
            JsonValueKind.Number => ( int )e.GetDouble(),
            JsonValueKind.String => ParseInt( e.GetString() ),
            _ => null
        };
    }

    public int GetInt( string name, int fallback ) => GetInt( name ) ?? fallback;

    public int RequireInt( string name ) => GetInt( name ) ?? throw new ArgumentException( $"Missing or invalid required argument '{name}'." );

    public bool GetBool( string name, bool fallback = false )
    {
        if ( args is null || !args.TryGetValue( name, out var e ) )
        {
            return fallback;
        }

        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString() is "true" or "1" or "yes",
            JsonValueKind.Number => e.GetDouble() != 0,
            _ => fallback
        };
    }

    /// <summary>Page window for list/search tools: offset (default 0) and limit (default 50, max 1000).</summary>
    public (int Offset, int Limit) GetPage( int defaultLimit = 50 ) =>
        ( Math.Max( 0, GetInt( "offset", 0 ) ), Math.Clamp( GetInt( "limit", defaultLimit ), 1, 1000 ) );

    public static int? ParseInt( string? text )
    {
        var s = text.AsSpan().Trim();

        if ( s.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) )
        {
            return int.TryParse( s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex ) ? hex : null;
        }

        return int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec ) ? dec : null;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace UODataAccess.Loaders;

/// <summary>A named gump: display name plus filter tags (e.g. "equipment", "male", "helmet").</summary>
public sealed record GumpName( int Id, string Name, IReadOnlyList<string> Tags )
{
    public bool HasTag( string tag ) => Tags.Contains( tag, StringComparer.OrdinalIgnoreCase );
}

/// <summary>
///     Names for well-known gump ids (the client files carry none), from the embedded
///     <c>UODataAccess/Data/gumps.json</c>: an array of <c>{"id": 60, "name": "backpack", "tags": ["container"]}</c>
///     objects sorted by id, converted once from UOFiddler's <c>Gumplist.xml</c>.
/// </summary>
public static class GumpNames
{
    private static readonly Lazy<(IReadOnlyList<GumpName> All, Dictionary<int, GumpName> Map)> Table = new( Load );

    /// <summary>All entries ordered by id.</summary>
    public static IReadOnlyList<GumpName> All => Table.Value.All;

    /// <summary>Every distinct tag, sorted.</summary>
    public static IReadOnlyList<string> Tags => field ??= All.SelectMany( g => g.Tags ).Distinct().Order( StringComparer.Ordinal ).ToList();

    public static bool TryGet( int id, out GumpName? name ) => Table.Value.Map.TryGetValue( id, out name );

    public static string? NameOf( int id ) => Table.Value.Map.TryGetValue( id, out var g ) ? g.Name : null;

    private static (IReadOnlyList<GumpName>, Dictionary<int, GumpName>) Load()
    {
        using var stream = typeof( GumpNames ).Assembly.GetManifestResourceStream( "UODataAccess.Data.gumps.json" )
                           ?? throw new InvalidOperationException( "Embedded gumps.json is missing." );
        var entries = JsonSerializer.Deserialize( stream, GumpNamesJsonContext.Default.ListGumpNameJson ) ?? [];
        var list = entries.Select( e => new GumpName( e.Id, e.Name, e.Tags ?? [] ) ).OrderBy( g => g.Id ).ToList();
        var map = new Dictionary<int, GumpName>();

        foreach ( var g in list )
        {
            map.TryAdd( g.Id, g );
        }

        return ( list, map );
    }
}

internal sealed record GumpNameJson( int Id, string Name, string[]? Tags );

[JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof( List<GumpNameJson> ) )]
internal sealed partial class GumpNamesJsonContext : JsonSerializerContext;

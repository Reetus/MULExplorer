using System.Text.Json;
using System.Text.Json.Serialization;

namespace UODataAccess.Loaders;

/// <summary>Kind of multi, from UOFiddler's <c>Multilist.xml</c> <c>type</c> attribute.</summary>
public enum MultiKind
{
    Boat = 0,
    House = 1,
    Decoration = 2,
    Other = 3
}

/// <summary>One row of <c>Data/multis.json</c>.</summary>
public sealed record MultiNameEntry( int Id, string Name, MultiKind Type );

/// <summary>
///     Display names for multi ids (the multi files carry none). The table is the embedded
///     <c>UODataAccess/Data/multis.json</c>, converted once from UOFiddler's <c>UoFiddler/Multilist.xml</c>:
///     an array of <c>{"id": 0, "name": "Small Boat [north]", "type": 0}</c> where type is
///     0 boat, 1 house, 2 decoration, 3 other. Ids without a row have no name.
/// </summary>
public static class MultiNames
{
    private static readonly Lazy<Dictionary<int, MultiNameEntry>> Table = new( Load );

    public static IReadOnlyDictionary<int, MultiNameEntry> All => Table.Value;

    public static string? GetName( int id ) => Table.Value.TryGetValue( id, out var e ) ? e.Name : null;

    public static MultiKind? GetKind( int id ) => Table.Value.TryGetValue( id, out var e ) ? e.Type : null;

    private static Dictionary<int, MultiNameEntry> Load()
    {
        using var stream = typeof( MultiNames ).Assembly.GetManifestResourceStream( "UODataAccess.Data.multis.json" )
                           ?? throw new InvalidOperationException( "Embedded multis.json missing." );
        var rows = JsonSerializer.Deserialize( stream, MultiNamesJson.Default.MultiNameEntryArray ) ?? [];
        var table = new Dictionary<int, MultiNameEntry>( rows.Length );

        foreach ( var row in rows )
        {
            table[row.Id] = row;
        }

        return table;
    }
}

[JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
[JsonSerializable( typeof( MultiNameEntry[] ) )]
internal sealed partial class MultiNamesJson : JsonSerializerContext;

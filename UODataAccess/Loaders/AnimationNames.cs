using System.Text.Json.Serialization;

namespace UODataAccess.Loaders;

/// <summary>One entry of <c>Data/animations.json</c>: a body id, its display name and list group.</summary>
public sealed record AnimationName(
    [property: JsonPropertyName( "body" )] int Body,
    [property: JsonPropertyName( "name" )] string Name,
    [property: JsonPropertyName( "type" )] string Type )
{
    /// <summary>The list group as a <see cref="MobType" /> ("sea" = <see cref="MobType.SeaMonster" />).</summary>
    public MobType Group => Type switch
    {
        "sea" => MobType.SeaMonster,
        "animal" => MobType.Animal,
        "human" => MobType.Human,
        "equipment" => MobType.Equipment,
        _ => MobType.Monster
    };
}

public sealed record AnimationNameList(
    [property: JsonPropertyName( "description" )] string? Description,
    [property: JsonPropertyName( "bodies" )] List<AnimationName> Bodies );

[JsonSerializable( typeof( AnimationNameList ) )]
internal sealed partial class AnimationNamesJson : JsonSerializerContext;

/// <summary>
///     Body display names from UOFiddler's animation list, shipped as the embedded resource
///     <c>Data/animations.json</c>.
/// </summary>
public static class AnimationNames
{
    private static readonly Lazy<IReadOnlyDictionary<int, AnimationName>> ByBody = new( Load );

    public static IEnumerable<AnimationName> Entries => ByBody.Value.Values;

    public static AnimationName? Get( int body ) => ByBody.Value.GetValueOrDefault( body );

    private static IReadOnlyDictionary<int, AnimationName> Load()
    {
        using var stream = typeof( AnimationNames ).Assembly.GetManifestResourceStream( "UODataAccess.Data.animations.json" )
                           ?? throw new InvalidOperationException( "Embedded resource animations.json is missing." );
        var list = System.Text.Json.JsonSerializer.Deserialize( stream, AnimationNamesJson.Default.AnimationNameList );
        var map = new Dictionary<int, AnimationName>();

        foreach ( var entry in list?.Bodies ?? [] )
        {
            map.TryAdd( entry.Body, entry );
        }

        return map;
    }
}

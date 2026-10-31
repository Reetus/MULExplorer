using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Imaging;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Light tab: light maps from light.mul.</summary>
public sealed class LightTools : IToolProvider
{
    /// <summary>UOFiddler's defaults for the in-game preview: grass and a lit lamp post.</summary>
    public const int DefaultLandTile = 0x0003;

    public const int DefaultLightSource = 0x0B20;

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_lights",
            "Every light id with its size and light-level statistics (raw min/max, average 0..31 intensity).",
            null,
            null,
            _ =>
            {
                var lights = context.RequireData().Lights;
                return Json( new JsonArray( lights.ValidIds.Select( id => ( JsonNode? )Describe( context.RequireData(), id ) ).ToArray() ) );
            } );

        registry.Add(
            "get_light_image",
            "PNG of a light map. By default the raw intensity map (grayscale); with in_game the light shines on a floor of land tiles around a light-source item, like UOFiddler's IG preview.",
            Props(
                ( "id", Id( "Light id." ) ),
                ( "in_game", Bool( "Render over a tiled floor instead of the raw map (default false)." ) ),
                ( "land", Id( $"Land tile for the floor (default {Hex( DefaultLandTile )})." ) ),
                ( "source", Id( $"Item drawn as the light source (default {Hex( DefaultLightSource )}; -1 = none)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( !data.Lights.IsValid( id ) )
                {
                    return Error( $"Light {id} does not exist." );
                }

                var pixmap = args.GetBool( "in_game" )
                    ? Preview( data, id, args.GetInt( "land", DefaultLandTile ), args.GetInt( "source", DefaultLightSource ) )
                    : data.Lights.Get( id );

                return pixmap is null ? Error( $"Light {id} could not be decoded." ) : Image( pixmap, Describe( data, id ) );
            } );
    }

    /// <summary>Shared by the tool and the Light tab.</summary>
    public static Pixmap? Preview( UOData data, int id, int land, int source, int width = 0, int height = 0 ) =>
        data.Lights.RenderPreview( id, land >= 0 ? data.Art.GetLand( land ) : null, source >= 0 ? data.Art.GetItem( source ) : null, width, height );

    public static JsonObject Describe( UOData data, int id )
    {
        var size = data.Lights.GetSize( id );
        var stats = data.Lights.GetStats( id );

        return new JsonObject
        {
            ["id"] = id,
            ["width"] = size?.Width,
            ["height"] = size?.Height,
            ["rawMin"] = stats?.Min,
            ["rawMax"] = stats?.Max,
            ["averageIntensity"] = stats is { } s ? Math.Round( s.Average, 2 ) : null,
            ["litPixels"] = stats?.LitPixels
        };
    }
}

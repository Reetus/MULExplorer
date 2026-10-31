using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Dress tab: paperdoll and in-world outfit previews.</summary>
public sealed class DressTools : IToolProvider
{
    private static readonly string[] BodyNames = Enum.GetNames<DressBody>();

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_wearables",
            "Wearable items (tiledata Wearable flag, a dress layer and an animation id), with layer and animation. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive name substring." ) ),
                ( "layer", Int( "Only this layer (tiledata quality byte), e.g. 5 = chest, 11 = hair." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var dress = new DressRenderer( data );
                var name = args.GetString( "name" );
                var layer = args.GetInt( "layer" );
                var (offset, limit) = args.GetPage();
                var matches = data.TileData.ItemTable
                    .Where( i => dress.IsWearable( i.Id ) && ( layer is null || i.Layer == layer ) )
                    .Where( i => name is null || i.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["items"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( i => ( JsonNode? )new JsonObject
                    {
                        ["id"] = Hex( i.Id ),
                        ["name"] = i.Name,
                        ["layer"] = i.Layer,
                        ["animation"] = i.Animation
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "render_paperdoll",
            "PNG of a paperdoll: the body gump plus each item's paperdoll gump (animation + 50000 male / + 60000 female), in layer order.",
            Props( BodyProps() ),
            null,
            args => Render( context, args, paperdoll: true ) );

        registry.Add(
            "render_dressed_figure",
            "PNG of the dressed in-world figure (body animation plus item animations composited by layer) for one action, direction and frame.",
            Props( [
                ..BodyProps(),
                ( "action", Int( "Action (default 4, standing)." ) ),
                ( "direction", Int( "Direction 0-7 (default 1); 5-7 are mirrored." ) ),
                ( "frame", Int( "Frame index (default 0)." ) )
            ] ),
            null,
            args => Render( context, args, paperdoll: false ) );
    }

    private static (string Name, JsonObject Schema)[] BodyProps() =>
    [
        ( "body", OneOf( "Body (default HumanMale).", BodyNames ) ),
        ( "body_hue", Id( "Skin hue (0 = none)." ) ),
        ( "items", Str( "Comma-separated item ids, each optionally with ':hue', e.g. '0x1517,0x1539:0x21,0x203B'. One item per layer; later items replace earlier ones." ) )
    ];

    private static ModelContextProtocol.Protocol.CallToolResult Render( McpContext context, ToolArgs args, bool paperdoll )
    {
        var data = context.RequireData();
        var dress = new DressRenderer( data );
        var bodyName = args.GetString( "body" ) ?? nameof( DressBody.HumanMale );

        if ( !Enum.TryParse<DressBody>( bodyName, true, out var body ) )
        {
            return Error( $"Unknown body '{bodyName}'. One of: {string.Join( ", ", BodyNames )}." );
        }

        var items = new Dictionary<int, DressItem>();
        var skipped = new JsonArray();

        foreach ( var part in ( args.GetString( "items" ) ?? "" ).Split( [',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries ) )
        {
            var pieces = part.Split( ':' );

            if ( ToolArgs.ParseInt( pieces[0] ) is not { } id || !dress.IsWearable( id ) )
            {
                skipped.Add( ( JsonNode? )part );
                continue;
            }

            var hue = pieces.Length > 1 ? ToolArgs.ParseInt( pieces[1] ) ?? 0 : 0;
            items[data.TileData.GetItem( id ).Layer] = new DressItem( id, hue );
        }

        var list = items.Values.ToList();
        var bodyHue = args.GetInt( "body_hue", 0 );
        var info = new JsonObject
        {
            ["body"] = body.ToString(),
            ["items"] = new JsonArray( list.Select( i => ( JsonNode? )Hex( i.ItemId ) ).ToArray() ),
            ["skipped"] = skipped
        };

        if ( paperdoll )
        {
            return dress.RenderPaperdoll( body, bodyHue, list ) is { } pixmap ? Image( pixmap, info ) : Error( "Nothing to draw (missing body gump?)." );
        }

        var action = args.GetInt( "action", 4 );
        var direction = args.GetInt( "direction", 1 );

        if ( ( uint )direction >= Animations.Directions )
        {
            return Error( "Direction must be 0-7." );
        }

        var frames = dress.RenderFigure( body, bodyHue, list, action, direction );

        if ( frames.Length == 0 )
        {
            return Error( $"Body {body} has no action {action}." );
        }

        var frame = Math.Clamp( args.GetInt( "frame", 0 ), 0, frames.Length - 1 );
        info["action"] = action;
        info["direction"] = direction;
        info["frame"] = frame;
        info["frames"] = frames.Length;
        return Image( frames[frame], info );
    }
}

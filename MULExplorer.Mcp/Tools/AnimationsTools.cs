using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Animations tab: mobile/equipment bodies from anim*.mul and AnimationFrame*.uop.</summary>
public sealed class AnimationsTools : IToolProvider
{
    private const string DirectionHelp = "Direction 0-7: 0 faces down the screen, 2 left, 4 up, 6 right; 5-7 are mirrored from 3-1.";

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_bodies",
            "Animated bodies (UOFiddler's animation list plus mobtypes.txt), with name, group (monster, sea, animal, human, equipment) and source file. Paginated.",
            Props(
                ( "group", OneOf( "Only this list group.", "monster", "sea", "animal", "human", "equipment" ) ),
                ( "name", Str( "Case-insensitive name substring." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var animations = context.RequireData().Animations;
                var group = args.GetString( "group" );
                var name = args.GetString( "name" );
                var (offset, limit) = args.GetPage();

                var matches = animations.CandidateBodies()
                    .Where( animations.HasAnimation )
                    .Select( b => ( Body: b, Name: BodyName( b ), Group: GroupName( animations, b ) ) )
                    .Where( e => group is null || e.Group == group )
                    .Where( e => name is null || e.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["bodies"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( e => ( JsonNode? )new JsonObject
                    {
                        ["body"] = Hex( e.Body ),
                        ["name"] = e.Name,
                        ["group"] = e.Group,
                        ["source"] = animations.GetBody( e.Body ).FileName
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "get_body_info",
            "How a body resolves (Body.def/Bodyconv.def/mobtypes.txt/AnimationSequence.uop): source file, file body, type, flags, defined actions with names and frame counts.",
            Props( ( "body", Id( "Body id, e.g. 0x190 (human male)." ) ) ),
            ["body"],
            args =>
            {
                var animations = context.RequireData().Animations;
                var body = args.RequireInt( "body" );
                return Json( DescribeBody( animations, body ) );
            } );

        registry.Add(
            "get_animation_frame",
            $"PNG of one animation frame, optionally hued. {DirectionHelp}",
            Props(
                ( "body", Id( "Body id." ) ),
                ( "action", Int( "Action (group) number; default the body's first action." ) ),
                ( "direction", Int( "Direction 0-7 (default 1)." ) ),
                ( "frame", Int( "Frame index (default 0)." ) ),
                ( "hue", Id( "Game hue number (0 = none)." ) ) ),
            ["body"],
            args =>
            {
                var data = context.RequireData();
                var body = args.RequireInt( "body" );

                if ( !TryFrames( data, args, out var frames, out var action, out var direction, out var error ) )
                {
                    return Error( error );
                }

                var index = args.GetInt( "frame", 0 );

                if ( ( uint )index >= ( uint )frames.Length )
                {
                    return Error( $"Frame {index} is out of range (0..{frames.Length - 1})." );
                }

                if ( frames[index].Image is not { } image )
                {
                    return Error( $"Frame {index} of body {Hex( body )} action {action} direction {direction} is empty." );
                }

                return Image( image, new JsonObject
                {
                    ["body"] = Hex( body ),
                    ["action"] = action,
                    ["direction"] = direction,
                    ["frame"] = index,
                    ["frames"] = frames.Length,
                    ["width"] = image.Width,
                    ["height"] = image.Height,
                    ["centerX"] = frames[index].CenterX,
                    ["centerY"] = frames[index].CenterY
                } );
            } );

        registry.Add(
            "get_animation_strip",
            $"PNG with every frame of one action and direction side by side, aligned on the feet. {DirectionHelp}",
            Props(
                ( "body", Id( "Body id." ) ),
                ( "action", Int( "Action (group) number; default the body's first action." ) ),
                ( "direction", Int( "Direction 0-7 (default 1)." ) ),
                ( "hue", Id( "Game hue number (0 = none)." ) ) ),
            ["body"],
            args =>
            {
                var data = context.RequireData();

                if ( !TryFrames( data, args, out var frames, out var action, out var direction, out var error ) )
                {
                    return Error( error );
                }

                if ( Animations.RenderStrip( frames ) is not { } strip )
                {
                    return Error( "All frames are empty." );
                }

                return Image( strip, new JsonObject
                {
                    ["body"] = Hex( args.RequireInt( "body" ) ),
                    ["action"] = action,
                    ["direction"] = direction,
                    ["frames"] = frames.Length,
                    ["width"] = strip.Width,
                    ["height"] = strip.Height
                } );
            } );
    }

    public static string BodyName( int body ) => AnimationNames.Get( body )?.Name ?? $"Body 0x{body:X}";

    /// <summary>List group: the animation list's group if named there, else the mobtypes.txt type.</summary>
    public static string GroupName( Animations animations, int body ) =>
        ( AnimationNames.Get( body )?.Group ?? animations.GetBody( body ).Type ) switch
        {
            MobType.SeaMonster => "sea",
            MobType.Animal => "animal",
            MobType.Human => "human",
            MobType.Equipment => "equipment",
            _ => "monster"
        };

    public static JsonObject DescribeBody( Animations animations, int body )
    {
        var info = animations.GetBody( body );

        return new JsonObject
        {
            ["body"] = Hex( body ),
            ["name"] = BodyName( body ),
            ["group"] = GroupName( animations, body ),
            ["type"] = info.Type.ToString(),
            ["inMobTypes"] = info.HasMobType,
            ["flags"] = info.Flags.ToString(),
            ["source"] = info.Source.ToString(),
            ["file"] = info.FileName,
            ["fileBody"] = info.FileBody,
            ["bodyDefTarget"] = info.BodyDefTarget,
            ["bodyDefHue"] = info.Hue,
            ["actions"] = new JsonArray( info.Actions.Select( a => ( JsonNode? )new JsonObject
            {
                ["action"] = a,
                ["name"] = animations.GetActionName( body, a ),
                ["frames"] = animations.GetFrames( body, a, 1 ).Length
            } ).ToArray() )
        };
    }

    private static bool TryFrames( UOData data, ToolArgs args, out AnimationFrame[] frames, out int action, out int direction, out string error )
    {
        var animations = data.Animations;
        var body = args.RequireInt( "body" );
        var info = animations.GetBody( body );
        frames = [];
        action = args.GetInt( "action" ) ?? ( info.Actions.Count > 0 ? info.Actions[0] : 0 );
        direction = args.GetInt( "direction", 1 );
        error = "";

        if ( !info.HasAnimation )
        {
            error = $"Body {Hex( body )} has no animation.";
            return false;
        }

        if ( !info.IsDefined( action ) )
        {
            error = $"Body {Hex( body )} has no action {action}. Defined: {string.Join( ", ", info.Actions )}.";
            return false;
        }

        if ( ( uint )direction >= Animations.Directions )
        {
            error = "Direction must be 0-7.";
            return false;
        }

        frames = animations.GetFrames( body, action, direction );
        var hue = args.GetInt( "hue", 0 );

        if ( hue <= 0 && info.Hue > 0 )
        {
            hue = info.Hue;
        }

        if ( hue > 0 )
        {
            frames = Animations.ApplyHue( frames, data.Hues, hue );
        }

        if ( frames.Length == 0 )
        {
            error = $"Body {Hex( body )} action {action} direction {direction} has no frames.";
            return false;
        }

        return true;
    }
}

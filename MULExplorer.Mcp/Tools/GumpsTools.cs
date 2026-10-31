using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Gumps tab: UI art from gumpart, with names from UOFiddler's gump list.</summary>
public sealed class GumpsTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_gump_image",
            "PNG of a gump (UI art), optionally hued. By default the hue recolors every pixel; set only_gray for partial hues.",
            Props(
                ( "id", Id( "Gump id." ) ),
                ( "hue", Id( "Game hue number (0 = none)." ) ),
                ( "only_gray", Bool( "Partial hue: only recolor gray pixels (default false)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( data.Gumps.Get( id ) is not { } pixmap )
                {
                    return Error( $"Gump {Hex( id )} does not exist." );
                }

                var hue = args.GetInt( "hue", 0 );

                if ( hue > 0 )
                {
                    pixmap = data.Hues.Apply( pixmap, hue, args.GetBool( "only_gray" ) );
                }

                return Image( pixmap, Describe( id, ( pixmap.Width, pixmap.Height ) ) );
            } );

        registry.Add(
            "get_gump_info",
            "Size, known name and tags of a gump id.",
            Props( ( "id", Id( "Gump id." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );
                return data.Gumps.GetSize( id ) is { } size ? Json( Describe( id, size ) ) : Error( $"Gump {Hex( id )} does not exist." );
            } );

        registry.Add(
            "search_gumps",
            "Lists existing gump ids, optionally filtered by name substring, tag, or id range. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive substring of the gump's known name (from UOFiddler's gump list)." ) ),
                ( "tag", Str( "Required tag, e.g. 'equipment', 'female', 'helmet', 'container'." ) ),
                ( "from", Id( "Lowest id (inclusive)." ) ),
                ( "to", Id( "Highest id (inclusive)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var name = args.GetString( "name" );
                var tag = args.GetString( "tag" );
                var from = Math.Max( 0, args.GetInt( "from", 0 ) );
                var to = Math.Min( data.Gumps.Count - 1, args.GetInt( "to", data.Gumps.Count - 1 ) );
                var (offset, limit) = args.GetPage();

                var matches = Enumerable.Range( from, Math.Max( 0, to - from + 1 ) )
                    .Where( data.Gumps.IsValid )
                    .Where( id => name is null && tag is null || Matches( id, name, tag ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["gumps"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( id => ( JsonNode? )Describe( id, data.Gumps.GetSize( id ) ) ).ToArray() )
                } );
            } );

        registry.Add(
            "list_gump_tags",
            "Tags available for filtering gumps (from UOFiddler's gump list).",
            null,
            null,
            _ => Json( new JsonArray( GumpNames.Tags.Select( t => ( JsonNode? )t ).ToArray() ) ) );
    }

    private static bool Matches( int id, string? name, string? tag ) =>
        GumpNames.TryGet( id, out var g ) && g is not null &&
        ( name is null || g.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) ) &&
        ( tag is null || g.HasTag( tag ) );

    public static JsonObject Describe( int id, (int Width, int Height)? size )
    {
        GumpNames.TryGet( id, out var g );
        return new JsonObject
        {
            ["id"] = Hex( id ),
            ["decimal"] = id,
            ["width"] = size?.Width,
            ["height"] = size?.Height,
            ["name"] = g?.Name,
            ["tags"] = g is null ? null : new JsonArray( g.Tags.Select( t => ( JsonNode? )t ).ToArray() )
        };
    }
}

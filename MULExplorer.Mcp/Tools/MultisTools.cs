using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Multis tab: houses, boats and decorations from MultiCollection.uop or multi.mul.</summary>
public sealed class MultisTools : IToolProvider
{
    private static JsonObject SourceSchema => OneOf( "Data source; defaults to MultiCollection.uop when present, else multi.mul.", "UOP", "MUL" );

    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_multis",
            "Lists multis that exist in the client, with UOFiddler names and kinds (boat/house/decoration/other). Filter by name or id substring. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive name substring." ) ),
                ( "source", SourceSchema ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var multis = context.RequireData().Multis;
                var source = GetSource( multis, args );
                var name = args.GetString( "name" );
                var (offset, limit) = args.GetPage();

                var matches = multis.GetIds( source )
                    .Where( id => name is null || ( MultiNames.GetName( id )?.Contains( name, StringComparison.OrdinalIgnoreCase ) ?? false ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["source"] = source.ToString(),
                    ["sources"] = new JsonArray( multis.Sources.Select( s => ( JsonNode? )s.ToString() ).ToArray() ),
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["multis"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( id => ( JsonNode? )new JsonObject
                    {
                        ["id"] = Hex( id ),
                        ["name"] = MultiNames.GetName( id ),
                        ["kind"] = MultiNames.GetKind( id )?.ToString()
                    } ).ToArray() )
                } );
            } );

        registry.Add(
            "get_multi",
            "A multi's bounding box, max height, surface and (optionally) its components: item id, x/y/z offset, flags, visibility.",
            Props(
                ( "id", Id( "Multi id (item graphic minus 0x4000)." ) ),
                ( "source", SourceSchema ),
                ( "include_components", Bool( "Also list every component (default true)." ) ) ),
            ["id"],
            args =>
            {
                var multis = context.RequireData().Multis;
                var source = GetSource( multis, args );
                var id = args.RequireInt( "id" );

                if ( multis.Get( id, source ) is not { } multi )
                {
                    return Error( $"Multi {Hex( id )} does not exist in {source}." );
                }

                var json = new JsonObject
                {
                    ["id"] = Hex( id ),
                    ["name"] = MultiNames.GetName( id ),
                    ["kind"] = MultiNames.GetKind( id )?.ToString(),
                    ["source"] = source.ToString(),
                    ["width"] = multi.Width,
                    ["height"] = multi.Height,
                    ["min"] = new JsonObject { ["x"] = multi.MinX, ["y"] = multi.MinY },
                    ["max"] = new JsonObject { ["x"] = multi.MaxX, ["y"] = multi.MaxY },
                    ["max_z"] = multi.MaxHeight,
                    ["surface"] = multi.Surface,
                    ["component_count"] = multi.Tiles.Length
                };

                if ( args.GetBool( "include_components", true ) )
                {
                    json["components"] = new JsonArray( multi.Tiles.Select( t => ( JsonNode? )new JsonObject
                    {
                        ["item"] = Hex( t.ItemId ),
                        ["x"] = t.X,
                        ["y"] = t.Y,
                        ["z"] = t.Z,
                        ["flags"] = t.Flags,
                        ["unk1"] = t.Unk1,
                        ["visible"] = t.IsVisible
                    } ).ToArray() );
                }

                return Json( json );
            } );

        registry.Add(
            "get_multi_image",
            "PNG of a multi composited isometrically from static art, optionally cut at a maximum z (like UOFiddler's height slider).",
            Props(
                ( "id", Id( "Multi id." ) ),
                ( "source", SourceSchema ),
                ( "max_height", Int( "Skip components above this z (default: all)." ) ),
                ( "include_invisible", Bool( "Also draw components flagged invisible (default false)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var source = GetSource( data.Multis, args );
                var id = args.RequireInt( "id" );

                if ( data.Multis.Get( id, source ) is not { } multi )
                {
                    return Error( $"Multi {Hex( id )} does not exist in {source}." );
                }

                var image = MultiRenderer.Render( multi, data.Art, data.TryGet( d => d.TileData ), args.GetInt( "max_height", int.MaxValue ), args.GetBool( "include_invisible" ) );

                return image is null
                    ? Error( $"Multi {Hex( id )} has no visible art." )
                    : Image( image, new JsonObject { ["id"] = Hex( id ), ["name"] = MultiNames.GetName( id ), ["width"] = image.Width, ["height"] = image.Height } );
            } );
    }

    private static MultiSource GetSource( Multis multis, ToolArgs args )
    {
        if ( args.GetString( "source" ) is not { Length: > 0 } text )
        {
            return multis.DefaultSource;
        }

        if ( !Enum.TryParse<MultiSource>( text, true, out var source ) || !multis.Has( source ) )
        {
            throw new ArgumentException( $"Source '{text}' is not available. Available: {string.Join( ", ", multis.Sources )}." );
        }

        return source;
    }
}

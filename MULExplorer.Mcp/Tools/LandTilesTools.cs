using System.Text.Json.Nodes;
using UODataAccess;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Land Tiles tab: land art (ids 0..0x3FFF) and its tiledata.</summary>
public sealed class LandTilesTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_land",
            "Tiledata for a land tile id (name, flags, texture id), whether it has art and a texture, and its radar color.",
            Props( ( "id", Id( "Land id, 0..0x3FFF." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( ( uint )id >= Art.LandCount )
                {
                    return Error( $"Land id {Hex( id )} is out of range (0..0x3FFF)." );
                }

                return Json( Describe( data, data.TileData.GetLand( id ) ) );
            } );

        registry.Add(
            "get_land_image",
            "PNG of a land tile's 44x44 art, or with as_texture=true its texmaps texture.",
            Props( ( "id", Id( "Land id, 0..0x3FFF." ) ), ( "as_texture", Bool( "Return the land tile's texture instead of its art (default false)." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );

                if ( args.GetBool( "as_texture" ) )
                {
                    var textureId = data.TileData.GetLand( id ).TextureId;

                    return data.Textures.Get( textureId ) is { } texture
                        ? Image( texture, new JsonObject { ["id"] = Hex( id ), ["texture_id"] = Hex( textureId ), ["size"] = texture.Width } )
                        : Error( $"Land {Hex( id )} has no texture (texture id {Hex( textureId )})." );
                }

                return data.Art.GetLand( id ) is { } pixmap
                    ? Image( pixmap, new JsonObject { ["id"] = Hex( id ), ["name"] = data.TileData.GetLand( id ).Name } )
                    : Error( $"Land {Hex( id )} has no art." );
            } );

        registry.Add(
            "search_land",
            "Finds land tiles by name substring and/or required flags. Paginated.",
            Props(
                ( "name", Str( "Case-insensitive name substring." ) ),
                ( "flags", Str( "Comma-separated flags that must all be set, e.g. 'Wet,Impassable'. See list_tile_flags." ) ),
                ( "with_art_only", Bool( "Only land tiles that have art (default true)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var name = args.GetString( "name" );
                var flags = ItemsTools.ParseFlags( args.GetString( "flags" ) );
                var artOnly = args.GetBool( "with_art_only", true );
                var (offset, limit) = args.GetPage();

                var matches = data.TileData.LandTable
                    .Where( l => ( name is null || l.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) ) && ( l.Flags & flags ) == flags )
                    .Where( l => !artOnly || data.Art.IsValidLand( l.Id ) )
                    .ToList();

                return Json( new JsonObject
                {
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["land"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( l => ( JsonNode? )new JsonObject
                    {
                        ["id"] = Hex( l.Id ),
                        ["name"] = l.Name,
                        ["flags"] = l.Flags.ToString(),
                        ["texture_id"] = Hex( l.TextureId )
                    } ).ToArray() )
                } );
            } );
    }

    private static JsonObject Describe( UOData data, LandData land )
    {
        var textures = data.TryGet( d => d.Textures );
        var radar = data.TryGet( d => d.RadarColors );

        return new JsonObject
        {
            ["id"] = Hex( land.Id ),
            ["name"] = land.Name,
            ["flags"] = land.Flags.ToString(),
            ["texture_id"] = Hex( land.TextureId ),
            ["has_art"] = data.Art.IsValidLand( land.Id ),
            ["has_texture"] = textures?.IsValid( land.TextureId ) ?? false,
            ["radar_color"] = radar is null ? null : $"0x{radar.GetLandColor( land.Id ):X4}"
        };
    }
}

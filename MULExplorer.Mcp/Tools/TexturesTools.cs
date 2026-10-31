using System.Text.Json.Nodes;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Textures tab: texmaps.mul land textures.</summary>
public sealed class TexturesTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_texture",
            "A texmaps texture's size (64 or 128), its TexTerr.def redirect if any, and the land tiles whose tiledata references it.",
            Props( ( "id", Id( "Texture id, 0..0x3FFF." ) ) ),
            ["id"],
            args =>
            {
                var data = context.RequireData();
                var id = args.RequireInt( "id" );
                var textures = data.Textures;
                var users = data.TryGet( d => d.TileData )?.LandTable.Where( l => l.TextureId == id && id != 0 ).Select( l => l.Id ).ToList() ?? [];

                return Json( new JsonObject
                {
                    ["id"] = Hex( id ),
                    ["valid"] = textures.IsValid( id ),
                    ["size"] = textures.GetSize( id ),
                    ["texterr_alias"] = textures.GetAlias( id ) is { } alias ? Hex( alias ) : null,
                    ["used_by_land_count"] = users.Count,
                    ["used_by_land"] = new JsonArray( users.Take( 100 ).Select( l => ( JsonNode? )Hex( l ) ).ToArray() )
                } );
            } );

        registry.Add(
            "get_texture_image",
            "PNG of a texmaps texture (64x64 or 128x128).",
            Props( ( "id", Id( "Texture id, 0..0x3FFF." ) ) ),
            ["id"],
            args =>
            {
                var id = args.RequireInt( "id" );

                return context.RequireData().Textures.Get( id ) is { } texture
                    ? Image( texture, new JsonObject { ["id"] = Hex( id ), ["size"] = texture.Width } )
                    : Error( $"Texture {Hex( id )} does not exist." );
            } );

        registry.Add(
            "list_textures",
            "Ids of existing textures with their sizes. Paginated.",
            Props( ( "offset", Int( "Skip this many textures." ) ), ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var textures = context.RequireData().Textures;
                var (offset, limit) = args.GetPage();
                var ids = Enumerable.Range( 0, textures.Count ).Where( textures.IsValid ).ToList();

                return Json( new JsonObject
                {
                    ["total"] = ids.Count,
                    ["offset"] = offset,
                    ["textures"] = new JsonArray( ids.Skip( offset ).Take( limit )
                        .Select( i => ( JsonNode? )new JsonObject { ["id"] = Hex( i ), ["size"] = textures.GetSize( i ) } ).ToArray() )
                } );
            } );
    }
}

using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>MultiMap/Facets tab: Multimap.rle and facet0N.mul overview images.</summary>
public sealed class MultiMapTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_multimap_images",
            "Which overview images exist (Multimap.rle, facet0N.mul) and their sizes.",
            null,
            null,
            _ =>
            {
                var multiMap = context.RequireData().MultiMap;
                var facets = new JsonArray();

                foreach ( var id in multiMap.FacetIds )
                {
                    var size = multiMap.GetFacetSize( id );
                    facets.Add( ( JsonNode )new JsonObject { ["id"] = id, ["file"] = MultiMap.FacetFileName( id ), ["width"] = size?.Width, ["height"] = size?.Height } );
                }

                var rle = multiMap.GetMultiMapSize();
                return Json( new JsonObject
                {
                    ["multimap"] = rle is { } r ? new JsonObject { ["file"] = "Multimap.rle", ["width"] = r.Width, ["height"] = r.Height } : null,
                    ["facets"] = facets
                } );
            } );

        registry.Add(
            "get_multimap_image",
            "PNG of Multimap.rle (black on white, as UOFiddler shows it), downsampled to fit 'max_size'.",
            Props( MaxSizeProp ),
            null,
            args =>
            {
                var image = context.RequireData().MultiMap.GetMultiMap() ?? throw new ArgumentException( "This client has no Multimap.rle." );
                return Render( image, args, new JsonObject { ["file"] = "Multimap.rle" } );
            } );

        registry.Add(
            "get_facet_image",
            "PNG of a facet0N.mul facet image, downsampled to fit 'max_size'.",
            Props( ( "id", Int( "Facet image number N of facet0N.mul (0..5)." ) ), MaxSizeProp ),
            ["id"],
            args =>
            {
                var id = args.RequireInt( "id" );
                var image = context.RequireData().MultiMap.GetFacetImage( id ) ?? throw new ArgumentException( $"{MultiMap.FacetFileName( id )} not found." );
                return Render( image, args, new JsonObject { ["file"] = MultiMap.FacetFileName( id ) } );
            } );
    }

    private static readonly (string, JsonObject) MaxSizeProp = ( "max_size", Int( $"Longest side of the PNG in pixels (default 1024, max {MapTools.MaxImageSize})." ) );

    private static ModelContextProtocol.Protocol.CallToolResult Render( MultiMapImage image, ToolArgs args, JsonObject info )
    {
        var maxSize = Math.Clamp( args.GetInt( "max_size", 1024 ), 16, MapTools.MaxImageSize );
        var step = Math.Max( 1, ( Math.Max( image.Width, image.Height ) + maxSize - 1 ) / maxSize );
        var pixmap = image.ToPixmap( step );
        info["width"] = image.Width;
        info["height"] = image.Height;
        info["step"] = step;
        info["image_width"] = pixmap.Width;
        info["image_height"] = pixmap.Height;
        return Image( pixmap, info );
    }
}

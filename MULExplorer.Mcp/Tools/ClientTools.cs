using System.Text.Json.Nodes;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Client folder overview: which files exist and which loaders can open.</summary>
public sealed class ClientTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_client_info",
            "Client folder path, and for each asset type whether it loaded and from which source (MUL or UOP).",
            null,
            null,
            _ =>
            {
                var data = context.RequireData();
                var assets = new JsonObject
                {
                    ["tiledata"] = Probe( () => $"{data.TileData.ItemTable.Length} items, {( data.TileData.IsNewFormat ? "64-bit" : "32-bit" )} flags" ),
                    ["art"] = Probe( () => $"{data.Art.Source}, {data.Art.ItemCount} item slots" ),
                    ["gumps"] = Probe( () => $"{data.Gumps.Source}, {data.Gumps.Count} slots" ),
                    ["hues"] = Probe( () => $"{data.Hues.List.Count} hues" ),
                    ["cliloc"] = Probe( () => $"{data.Cliloc.Language}, {data.Cliloc.Count} entries" )
                };

                return Json( new JsonObject
                {
                    ["folder"] = data.Folder,
                    ["fileCount"] = data.Files.FileNames.Count(),
                    ["assets"] = assets
                } );
            } );

        registry.Add(
            "list_client_files",
            "Lists files in the client folder, optionally filtered by a case-insensitive substring.",
            Props( ( "filter", Str( "Substring to match, e.g. 'uop' or 'map'." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var filter = args.GetString( "filter" );
                var names = data.Files.FileNames
                    .Where( n => filter is null || n.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
                    .Order( StringComparer.OrdinalIgnoreCase )
                    .Select( n => ( JsonNode? )new JsonObject { ["name"] = n, ["size"] = new FileInfo( data.Files.Find( n )! ).Length } )
                    .ToArray();
                return Json( new JsonArray( names ) );
            } );
    }

    private static JsonNode Probe( Func<string> describe )
    {
        try
        {
            return describe();
        }
        catch ( Exception e )
        {
            return $"unavailable: {e.Message}";
        }
    }
}

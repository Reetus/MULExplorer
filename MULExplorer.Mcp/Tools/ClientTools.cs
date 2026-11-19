using System.Text.Json.Nodes;
using UODataAccess;
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
            _ => Json( Describe( context.RequireData() ) ) );

        registry.Add(
            "set_client_folder",
            "Switches to another UO client folder (e.g. a different client version) and returns its get_client_info. " +
            "Every other tool then reads the new folder; in the GUI the window switches too. The app's saved folder setting is not changed.",
            Props( ( "folder", Str( "Absolute path of a client folder (must contain tiledata.mul)." ) ) ),
            ["folder"],
            async ( args, _ ) => Json( Describe( await context.OpenFolderAsync( args.RequireString( "folder" ) ) ) ),
            readOnly: false );

        registry.Add(
            "reload_client_folder",
            "Reopens the current client folder so files changed on disk are read again. Returns get_client_info.",
            null,
            null,
            async ( _, _ ) => Json( Describe( await context.OpenFolderAsync( context.RequireData().Folder ) ) ),
            readOnly: false );

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

    private static JsonObject Describe( UOData data ) => new()
    {
        ["folder"] = data.Folder,
        ["fileCount"] = data.Files.FileNames.Count(),
        ["assets"] = new JsonObject
        {
            ["tiledata"] = Probe( () => $"{data.TileData.ItemTable.Length} items, {( data.TileData.IsNewFormat ? "64-bit" : "32-bit" )} flags" ),
            ["art"] = Probe( () => $"{data.Art.Source}, {data.Art.ItemCount} item slots" ),
            ["gumps"] = Probe( () => $"{data.Gumps.Source}, {data.Gumps.Count} slots" ),
            ["hues"] = Probe( () => $"{data.Hues.List.Count} hues" ),
            ["cliloc"] = Probe( () => $"{data.Cliloc.Language}, {data.Cliloc.Count} entries" )
        }
    };

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

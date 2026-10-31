using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Verdata tab: verdata.mul patch records.</summary>
public sealed class VerdataTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_verdata_patches",
            "Patch records in verdata.mul (file id and name, entry index, lookup, length, extra), optionally for one file id. " +
            "Reports present=false when the client has no verdata.mul (all modern clients). Paginated.",
            Props(
                ( "file", Int( "Only patches for this verdata file id (e.g. 4 = art.mul, 12 = gumpart.mul, 30 = tiledata.mul)." ) ),
                ( "offset", Int( "Skip this many matches." ) ),
                ( "limit", Int( "Max results (default 50)." ) ) ),
            null,
            args =>
            {
                var verdata = context.RequireData().Verdata;
                var file = args.GetInt( "file" );
                var (offset, limit) = args.GetPage();
                var matches = verdata.Patches.Where( p => file is null || p.File == file ).ToList();

                return Json( new JsonObject
                {
                    ["present"] = verdata.IsPresent,
                    ["truncated"] = verdata.IsTruncated,
                    ["total"] = matches.Count,
                    ["offset"] = offset,
                    ["files"] = new JsonArray( verdata.Patches.GroupBy( p => p.File ).OrderBy( g => g.Key )
                        .Select( g => ( JsonNode? )new JsonObject { ["file"] = g.Key, ["name"] = Verdata.GetFileName( g.Key ), ["count"] = g.Count() } ).ToArray() ),
                    ["patches"] = new JsonArray( matches.Skip( offset ).Take( limit ).Select( p => ( JsonNode? )new JsonObject
                    {
                        ["file"] = p.File,
                        ["file_name"] = p.FileName,
                        ["index"] = Hex( p.Index ),
                        ["lookup"] = $"0x{p.Lookup:X}",
                        ["length"] = p.Length,
                        ["extra"] = $"0x{p.Extra:X}"
                    } ).ToArray() )
                } );
            } );
    }
}

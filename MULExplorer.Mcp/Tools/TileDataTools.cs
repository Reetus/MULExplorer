using System.Text.Json.Nodes;
using UODataAccess.Loaders;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>TileData tab: whole-file facts about tiledata.mul (per-entry lookups are get_item / get_land).</summary>
public sealed class TileDataTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "get_tiledata_info",
            "tiledata.mul layout (32- or 64-bit flags), land/item entry counts, and how many land and item entries set each flag.",
            null,
            null,
            _ =>
            {
                var tiles = context.RequireData().TileData;
                var flags = new JsonObject();

                foreach ( var flag in Enum.GetValues<TileFlag>() )
                {
                    if ( flag == TileFlag.None )
                    {
                        continue;
                    }

                    flags[flag.ToString()] = new JsonObject
                    {
                        ["land"] = tiles.LandTable.Count( l => l.Flags.HasFlag( flag ) ),
                        ["items"] = tiles.ItemTable.Count( i => i.Flags.HasFlag( flag ) )
                    };
                }

                return Json( new JsonObject
                {
                    ["format"] = tiles.IsNewFormat ? "64-bit flags (High Seas and later)" : "32-bit flags (pre High Seas)",
                    ["land_count"] = tiles.LandTable.Length,
                    ["item_count"] = tiles.ItemTable.Length,
                    ["named_land"] = tiles.LandTable.Count( l => l.Name.Length > 0 ),
                    ["named_items"] = tiles.ItemTable.Count( i => i.Name.Length > 0 ),
                    ["flag_counts"] = flags
                } );
            } );
    }
}

using System.Text.Json.Nodes;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Drives the MULExplorer window: the same actions a person takes, plus screenshots.</summary>
public sealed class UITools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;
        var tab = ( "tab", Str( "Tab header as shown in the window, e.g. 'Items', 'Multis', 'MultiMap/Facets'." ) );

        registry.Add( "ui_list_tabs", "Tab headers in display order, and which is selected.", null, null, async ( _, _ ) =>
        {
            var ui = context.RequireUI();
            var tabs = await ui.ListTabsAsync();
            return Json( new JsonObject
            {
                ["tabs"] = new JsonArray( tabs.Select( t => ( JsonNode? )t ).ToArray() ),
                ["selected"] = await ui.GetSelectedTabAsync()
            } );
        } );

        registry.Add( "ui_select_tab", "Switches to a tab (loading it if needed).", Props( tab ), ["tab"], async ( args, _ ) =>
        {
            await context.RequireUI().SelectTabAsync( args.RequireString( "tab" ) );
            return Text( "ok" );
        } );

        registry.Add( "ui_get_tab_state", "The tab's view-model state: selection, counts, filter, available actions.", Props( tab ), null, async ( args, _ ) =>
            Json( await context.RequireUI().GetTabStateAsync( args.GetString( "tab" ) ) ) );

        registry.Add( "ui_select_entry", "Selects an entry by id in a tab (item id, gump id, body id, hue number...).",
            Props( tab, ( "id", Id( "Entry id." ) ) ), ["tab", "id"], async ( args, _ ) =>
            {
                var found = await context.RequireUI().SelectEntryAsync( args.RequireString( "tab" ), args.RequireInt( "id" ) );
                return found ? Text( "ok" ) : Error( "No such entry in that tab." );
            } );

        registry.Add( "ui_search", "Types into a tab's search/filter box. Returns the number of visible entries.",
            Props( tab, ( "text", Str( "Filter text; empty clears it." ) ) ), ["tab"], async ( args, _ ) =>
            {
                var count = await context.RequireUI().SearchAsync( args.RequireString( "tab" ), args.GetString( "text" ) ?? "" );
                return Json( new JsonObject { ["visible"] = count } );
            } );

        registry.Add( "ui_invoke", "Runs a tab-specific action listed in ui_get_tab_state 'actions'.",
            Props( tab, ( "action", Str( "Action name." ) ), ( "args", new JsonObject { ["type"] = "object", ["description"] = "Action arguments." } ) ),
            ["tab", "action"], async ( args, _ ) =>
            {
                var actionArgs = args.GetString( "args" ) is { } raw ? JsonNode.Parse( raw ) as JsonObject ?? [] : [];
                return Json( await context.RequireUI().InvokeAsync( args.RequireString( "tab" ), args.RequireString( "action" ), actionArgs ) );
            } );

        registry.Add( "ui_screenshot", "PNG screenshot of the MULExplorer window.", null, null, async ( _, _ ) =>
            Image( await context.RequireUI().ScreenshotAsync() ) );

        registry.Add( "ui_set_theme", "Switches the theme.", Props( ( "variant", OneOf( "Theme variant.", "Dark", "Light", "Default" ) ) ), ["variant"], async ( args, _ ) =>
        {
            await context.RequireUI().SetThemeAsync( args.RequireString( "variant" ) );
            return Text( "ok" );
        } );

        registry.Add( "ui_get_window_info", "Window size, theme, loaded folder and MCP status.", null, null, async ( _, _ ) =>
            Json( await context.RequireUI().GetWindowInfoAsync() ) );
    }
}

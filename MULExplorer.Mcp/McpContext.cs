using System.Text.Json.Nodes;
using UODataAccess;

namespace MULExplorer.Mcp;

/// <summary>
///     What the tools operate on. Headless, only <see cref="Data" /> is set; inside the GUI the
///     app also supplies <see cref="UI" /> so agents can drive and screenshot the window.
/// </summary>
public sealed class McpContext
{
    public UOData? Data { get; set; }

    public IUIAutomation? UI { get; set; }

    public UOData RequireData() =>
        Data ?? throw new InvalidOperationException( "No UO client folder is loaded. Set one in the app (File > Set UO Folder) or pass --uo-folder." );

    public IUIAutomation RequireUI() =>
        UI ?? throw new InvalidOperationException( "UI tools need the MULExplorer window; this server is running headless." );
}

/// <summary>
///     Lets MCP clients drive the GUI the same way a person would, for end-to-end testing.
///     Implemented by the Avalonia app; every method marshals onto the UI thread.
/// </summary>
public interface IUIAutomation
{
    Task<IReadOnlyList<string>> ListTabsAsync();

    Task<string?> GetSelectedTabAsync();

    Task SelectTabAsync( string tab );

    /// <summary>Snapshot of a tab's view model (selection, counts, filter...). Null tab = current.</summary>
    Task<JsonObject> GetTabStateAsync( string? tab );

    /// <summary>Selects the entry with <paramref name="id" /> in a tab; false if it does not exist.</summary>
    Task<bool> SelectEntryAsync( string tab, int id );

    /// <summary>Applies the tab's search/filter box; returns the number of visible entries.</summary>
    Task<int> SearchAsync( string tab, string text );

    /// <summary>Runs a tab-specific action (e.g. "next_frame", "set_facet"); see the tab's state for the list.</summary>
    Task<JsonObject> InvokeAsync( string tab, string action, JsonObject args );

    /// <summary>PNG of the whole window.</summary>
    Task<byte[]> ScreenshotAsync();

    Task SetThemeAsync( string variant );

    Task<JsonObject> GetWindowInfoAsync();
}

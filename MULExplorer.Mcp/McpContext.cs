using System.Text.Json.Nodes;
using UODataAccess;

namespace MULExplorer.Mcp;

/// <summary>
///     What the tools operate on. Headless, only <see cref="Data" /> is set; inside the GUI the
///     app also supplies <see cref="UI" /> so agents can drive and screenshot the window.
/// </summary>
public sealed class McpContext : IDisposable
{
    private readonly SemaphoreSlim _switch = new( 1, 1 );

    /// <summary>Data opened by <see cref="OpenFolderAsync" /> headless; this context disposes it.</summary>
    private UOData? _owned;

    public UOData? Data { get; set; }

    public IUIAutomation? UI { get; set; }

    public void Dispose()
    {
        _owned?.Dispose();
        _owned = null;
    }

    /// <summary>
    ///     Opens another client folder, or reopens the current one so changed files are re-read.
    ///     In the GUI the window switches too (every tab resets, the saved folder setting is left alone);
    ///     headless only <see cref="Data" /> is replaced. Data this context opened earlier is disposed.
    /// </summary>
    public async Task<UOData> OpenFolderAsync( string folder )
    {
        if ( !UOData.IsClientFolder( folder ) )
        {
            throw new ArgumentException( $"'{folder}' is not a UO client folder (no tiledata.mul)." );
        }

        var full = Path.GetFullPath( folder );
        await _switch.WaitAsync();

        try
        {
            if ( UI is { } ui )
            {
                await ui.OpenFolderAsync( full );
                return RequireData();
            }

            var previous = _owned;
            Data = _owned = new UOData( full );
            previous?.Dispose();
            return Data;
        }
        finally
        {
            _switch.Release();
        }
    }

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

    /// <summary>Opens a client folder in the window (like File > Set UO Folder, without saving it) and waits for the current tab.</summary>
    Task OpenFolderAsync( string folder );
}

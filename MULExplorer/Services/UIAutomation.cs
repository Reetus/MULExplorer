using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MULExplorer.Mcp;
using MULExplorer.ViewModels;

namespace MULExplorer.Services;

/// <summary>
///     <see cref="IUIAutomation" /> over the real window. Every call hops onto the UI thread and
///     waits for the affected tab to finish loading, so callers see settled state.
/// </summary>
public sealed class UIAutomation( Window window, MainWindowViewModel vm ) : IUIAutomation
{
    public Task<IReadOnlyList<string>> ListTabsAsync() =>
        OnUI<IReadOnlyList<string>>( () => vm.Tabs.Select( t => t.Header ).ToList() );

    public Task<string?> GetSelectedTabAsync() => OnUI( () => vm.SelectedTab?.Header );

    public Task SelectTabAsync( string tab ) => OnUIAsync( async () =>
    {
        var target = Find( tab );
        vm.SelectedTab = target;
        await target.EnsureLoadedAsync();
        await Settle();
        return true;
    } );

    public Task<JsonObject> GetTabStateAsync( string? tab ) => OnUIAsync( async () =>
    {
        var target = tab is null ? vm.SelectedTab ?? vm.Tabs[0] : Find( tab );
        await target.EnsureLoadedAsync();
        var state = target.GetState();
        state["selected"] = ReferenceEquals( target, vm.SelectedTab );
        return state;
    } );

    public Task<bool> SelectEntryAsync( string tab, int id ) => OnUIAsync( async () =>
    {
        var target = Find( tab );
        await target.EnsureLoadedAsync();
        var found = target.SelectEntry( id );
        await Settle();
        return found;
    } );

    public Task<int> SearchAsync( string tab, string text ) => OnUIAsync( async () =>
    {
        var target = Find( tab );
        await target.EnsureLoadedAsync();
        var count = target.Search( text );
        await Settle();
        return count;
    } );

    public Task<JsonObject> InvokeAsync( string tab, string action, JsonObject args ) => OnUIAsync( async () =>
    {
        var target = Find( tab );
        await target.EnsureLoadedAsync();
        var result = target.Invoke( action, args );
        await Settle();
        return result;
    } );

    public Task<byte[]> ScreenshotAsync() => OnUIAsync( async () =>
    {
        await Settle();
        var size = window.ClientSize;
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap( new PixelSize( Math.Max( 1, ( int )( size.Width * scale ) ), Math.Max( 1, ( int )( size.Height * scale ) ) ), new Vector( 96 * scale, 96 * scale ) );
        bitmap.Render( window );
        using var stream = new MemoryStream();
        bitmap.Save( stream, new PngBitmapEncoderOptions() );
        return stream.ToArray();
    } );

    public Task SetThemeAsync( string variant ) => OnUIAsync( async () =>
    {
        vm.SetThemeCommand.Execute( variant );
        await Settle();
        return true;
    } );

    public Task<JsonObject> GetWindowInfoAsync() => OnUI( () => new JsonObject
    {
        ["width"] = window.Bounds.Width,
        ["height"] = window.Bounds.Height,
        ["theme"] = vm.Theme,
        ["backdrop"] = vm.Backdrop,
        ["transparency"] = window.ActualTransparencyLevel.ToString(),
        ["actualTheme"] = window.ActualThemeVariant.ToString(),
        ["folder"] = vm.Data?.Folder,
        ["selectedTab"] = vm.SelectedTab?.Key,
        ["mcpRunning"] = vm.IsMcpRunning,
        ["mcpUrl"] = vm.IsMcpRunning ? vm.McpUrl : null
    } );

    private TabViewModel Find( string tab ) =>
        vm.FindTab( tab ) ?? throw new ArgumentException( $"No tab '{tab}'. Tabs: {string.Join( ", ", vm.Tabs.Select( t => t.Header ) )}" );

    /// <summary>Lets bindings, layout and rendering catch up with view-model changes.</summary>
    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync( () => { }, DispatcherPriority.Background );
    }

    private static Task<T> OnUI<T>( Func<T> action ) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult( action() ) : Dispatcher.UIThread.InvokeAsync( action ).GetTask();

    private static Task<T> OnUIAsync<T>( Func<Task<T>> action ) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync( action );
}

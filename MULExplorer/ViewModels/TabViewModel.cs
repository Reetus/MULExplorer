using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using UODataAccess;

namespace MULExplorer.ViewModels;

/// <summary>
///     Base for every tab. A tab loads lazily the first time it is shown (or driven via MCP) and
///     reloads when the client folder changes. The automation hooks (<see cref="GetState" />,
///     <see cref="SelectEntry" />, <see cref="Search" />, <see cref="Invoke" />) are what
///     <c>ui_*</c> MCP tools and headless tests call, so every tab is testable the same way.
/// </summary>
public abstract partial class TabViewModel : ObservableObject
{
    private Task? _loading;

    protected TabViewModel( string key, string header )
    {
        Key = key;
        Header = header;
    }

    /// <summary>Stable, non-localized name used by MCP and tests (e.g. "Items", "MultiMap").</summary>
    public string Key { get; }

    /// <summary>Localized tab header.</summary>
    public string Header { get; }

    /// <summary>False for tabs that are still placeholders.</summary>
    public virtual bool IsImplemented => true;

    public UOData? Data { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; protected set; }

    /// <summary>Tab-specific action names accepted by <see cref="Invoke" />.</summary>
    public virtual IReadOnlyList<string> Actions => [];

    /// <summary>Switches the tab to a new client folder (or none). The tab reloads next time it is shown.</summary>
    public void SetData( UOData? data )
    {
        Data = data;
        _loading = null;
        IsLoaded = false;
        ErrorMessage = data is null ? Strings.NoFolder : null;
        Reset();
    }

    /// <summary>Loads the tab once per folder; concurrent callers share the same load.</summary>
    public Task EnsureLoadedAsync()
    {
        if ( IsLoaded || Data is null || !IsImplemented )
        {
            return Task.CompletedTask;
        }

        return _loading ??= LoadCoreAsync( Data );
    }

    private async Task LoadCoreAsync( UOData data )
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await LoadAsync( data );

            if ( ReferenceEquals( data, Data ) )
            {
                IsLoaded = true;
            }
        }
        catch ( Exception e ) when ( e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or InvalidOperationException )
        {
            ErrorMessage = string.Format( Strings.LoadFailed, Header, e.Message );
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads what the tab needs. Do heavy work with <c>Task.Run</c>; assign bound properties after awaiting.</summary>
    protected abstract Task LoadAsync( UOData data );

    /// <summary>Drops everything derived from the previous folder.</summary>
    protected virtual void Reset()
    {
    }

    /// <summary>JSON snapshot for MCP/tests. Override and extend <c>base.GetState()</c>.</summary>
    public virtual JsonObject GetState() => new()
    {
        ["tab"] = Key,
        ["header"] = Header,
        ["implemented"] = IsImplemented,
        ["loaded"] = IsLoaded,
        ["error"] = ErrorMessage,
        ["actions"] = new JsonArray( Actions.Select( a => ( JsonNode? )a ).ToArray() )
    };

    /// <summary>Selects an entry by id. Returns false if the tab has no such entry.</summary>
    public virtual bool SelectEntry( int id ) => false;

    /// <summary>Applies the search/filter box. Returns the number of visible entries, or -1 if the tab has no search.</summary>
    public virtual int Search( string text ) => -1;

    public virtual JsonObject Invoke( string action, JsonObject args ) =>
        throw new ArgumentException( $"Tab '{Key}' has no action '{action}'. Available: {string.Join( ", ", Actions )}" );
}

/// <summary>Stand-in for tabs that are not built yet.</summary>
public abstract class PlaceholderTabViewModel( string key, string header ) : TabViewModel( key, header )
{
    public override bool IsImplemented => false;

    public string Message => Strings.NotImplemented;

    protected override Task LoadAsync( UOData data ) => Task.CompletedTask;
}

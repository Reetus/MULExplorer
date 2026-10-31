using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Mcp;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>
///     Speech tab (UOFiddler's SpeechControl, read-only): speech.mul keywords in file order. The
///     filter matches keyword text, or the id when it is a number (decimal or 0x hex).
/// </summary>
public sealed partial class SpeechViewModel : TabViewModel
{
    private IReadOnlyList<SpeechEntry> _all = [];

    public SpeechViewModel() : base( "Speech", Strings.TabSpeech )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<SpeechEntry> VisibleEntries { get; private set; } = [];

    [ObservableProperty]
    public partial SpeechEntry? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    protected override async Task LoadAsync( UOData data )
    {
        _all = await Task.Run( () => data.Speech.Entries );
        ApplyFilter();

        if ( SelectedEntry is null && VisibleEntries.Count > 0 )
        {
            SelectedEntry = VisibleEntries[0];
        }
    }

    protected override void Reset()
    {
        _all = [];
        VisibleEntries = [];
        SelectedEntry = null;
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedEntry;
        var text = SearchText.Trim();

        if ( text.Length == 0 )
        {
            VisibleEntries = _all;
        }
        else
        {
            var id = ToolArgs.ParseInt( text );
            VisibleEntries = _all.Where( e => e.Id == id || e.Keyword.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();
        }

        // Keep the selection if it survived the filter (the ListBox drops it when its items change).
        SelectedEntry = selected is not null && VisibleEntries.Contains( selected ) ? selected : null;

        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleEntries.Count, _all.Count );
    }

    /// <summary>Selects the first keyword with speech id <paramref name="id" />.</summary>
    public override bool SelectEntry( int id )
    {
        var entry = _all.FirstOrDefault( e => e.Id == id );

        if ( entry is null )
        {
            return false;
        }

        if ( !VisibleEntries.Contains( entry ) )
        {
            SearchText = "";
        }

        SelectedEntry = entry;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleEntries.Count;
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleEntries.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedEntry?.Id ?? -1;
        state["selectedOrder"] = SelectedEntry?.Order;
        state["selectedKeyword"] = SelectedEntry?.Keyword;
        return state;
    }
}

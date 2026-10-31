using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MULExplorer.Mcp;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>
///     CliLoc tab, after UOFiddler's ClilocControl: language selector, a virtualized number/text
///     list with a text (or regex) filter, go-to by number, and the selected entry in full.
/// </summary>
public sealed partial class ClilocViewModel : TabViewModel
{
    private Cliloc? _cliloc;
    private bool _switching;

    public ClilocViewModel() : base( "Cliloc", Strings.TabCliloc )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<string> Languages { get; private set; } = [];

    [ObservableProperty]
    public partial string? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ClilocEntry> VisibleEntries { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial string GoToText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedText { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string? Message { get; private set; }

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public ClilocEntry? SelectedEntry => ( uint )SelectedIndex < ( uint )VisibleEntries.Count ? VisibleEntries[SelectedIndex] : null;

    public override IReadOnlyList<string> Actions => ["set_language", "goto", "set_regex"];

    protected override async Task LoadAsync( UOData data )
    {
        var languages = Cliloc.AvailableLanguages( data.Files );
        var language = languages.Contains( SelectedLanguage ?? "" ) ? SelectedLanguage! : languages.Contains( "enu" ) ? "enu" : languages.FirstOrDefault()
                       ?? throw new FileNotFoundException( "No Cliloc.* files found." );
        var cliloc = await Task.Run( () => Cliloc.Get( data.Files, language ) );

        _switching = true;
        Languages = languages;
        SelectedLanguage = language;
        _switching = false;
        Apply( cliloc );
    }

    protected override void Reset()
    {
        _cliloc = null;
        Languages = [];
        VisibleEntries = [];
        SelectedIndex = -1;
        Details.Clear();
        SelectedText = "";
        Summary = "";
        Message = null;
    }

    partial void OnSelectedLanguageChanged( string? value )
    {
        if ( !_switching && value is not null && Data is { } data && IsLoaded )
        {
            _ = SwitchAsync( data, value );
        }
    }

    private async Task SwitchAsync( UOData data, string language )
    {
        try
        {
            var cliloc = await Task.Run( () => Cliloc.Get( data.Files, language ) );

            if ( SelectedLanguage == language && ReferenceEquals( data, Data ) )
            {
                Apply( cliloc );
            }
        }
        catch ( Exception e ) when ( e is IOException or InvalidDataException )
        {
            Message = e.Message;
        }
    }

    /// <summary>Shows a language, keeping the selected number if the new language has it.</summary>
    private void Apply( Cliloc cliloc )
    {
        var number = SelectedEntry?.Number;
        _cliloc = cliloc;
        ApplyFilter();

        if ( number is { } n && IndexOfVisible( n ) is >= 0 and var i )
        {
            SelectedIndex = i;
        }
        else if ( SelectedIndex < 0 && VisibleEntries.Count > 0 )
        {
            SelectedIndex = 0;
        }

        UpdateSelection();
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnUseRegexChanged( bool value ) => ApplyFilter();

    partial void OnSelectedIndexChanged( int value ) => UpdateSelection();

    private void ApplyFilter()
    {
        if ( _cliloc is not { } cliloc )
        {
            return;
        }

        var number = SelectedEntry?.Number;
        Message = null;

        try
        {
            VisibleEntries = SearchText.Length == 0 ? cliloc.Entries : cliloc.Search( SearchText, UseRegex ).ToList();
        }
        catch ( Exception e ) when ( e is ArgumentException or RegexMatchTimeoutException )
        {
            Message = string.Format( CultureInfo.CurrentCulture, ClilocStrings.InvalidRegex, e.Message );
            VisibleEntries = [];
        }

        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleEntries.Count, cliloc.Count );
        var index = number is { } n ? IndexOfVisible( n ) : -1;
        SelectedIndex = index >= 0 ? index : VisibleEntries.Count > 0 ? 0 : -1;
        UpdateSelection();
    }

    /// <summary>Visible lists are in ascending number order, so binary search works on filtered lists too.</summary>
    private int IndexOfVisible( int number )
    {
        int lo = 0, hi = VisibleEntries.Count - 1;

        while ( lo <= hi )
        {
            var mid = ( lo + hi ) >>> 1;
            var n = VisibleEntries[mid].Number;

            if ( n == number )
            {
                return mid;
            }

            if ( n < number )
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private void UpdateSelection()
    {
        Details.Clear();
        OnPropertyChanged( nameof( SelectedEntry ) );

        if ( SelectedEntry is not { } e )
        {
            SelectedText = "";
            return;
        }

        SelectedText = e.Text;
        Details.Add( new PropertyRow( ClilocStrings.Number, e.Number.ToString( CultureInfo.InvariantCulture ) ) );
        Details.Add( new PropertyRow( ClilocStrings.Hex, $"0x{e.Number:X}" ) );
        Details.Add( new PropertyRow( ClilocStrings.Flag, e.Flag.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( ClilocStrings.Length, e.Text.Length.ToString( CultureInfo.CurrentCulture ) ) );
    }

    [RelayCommand]
    private void GoTo()
    {
        if ( ToolArgs.ParseInt( GoToText ) is { } number && !SelectEntry( number ) )
        {
            Message = string.Format( CultureInfo.CurrentCulture, ClilocStrings.NotFound, number );
        }
    }

    /// <summary>Selects a cliloc number, clearing the filter if it hides it.</summary>
    public override bool SelectEntry( int id )
    {
        if ( _cliloc is not { } cliloc || !cliloc.TryGet( id, out _ ) )
        {
            return false;
        }

        if ( IndexOfVisible( id ) < 0 )
        {
            SearchText = "";
        }

        Message = null;
        SelectedIndex = IndexOfVisible( id );
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleEntries.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_language":
            {
                var language = args["language"]?.GetValue<string>()?.ToLowerInvariant() ?? throw new ArgumentException( "Missing 'language'." );

                if ( !Languages.Contains( language ) || Data is not { } data )
                {
                    throw new ArgumentException( $"Unknown language '{language}'. Available: {string.Join( ", ", Languages )}" );
                }

                _switching = true;
                SelectedLanguage = language;
                _switching = false;
                Apply( Cliloc.Get( data.Files, language ) );
                return GetState();
            }
            case "goto":
            {
                var number = ToolArgs.ParseInt( args["number"]?.ToString() ) ?? throw new ArgumentException( "Missing 'number'." );
                GoToText = number.ToString( CultureInfo.InvariantCulture );

                if ( !SelectEntry( number ) )
                {
                    throw new ArgumentException( $"Cliloc {number} not found." );
                }

                return GetState();
            }
            case "set_regex":
                UseRegex = args["value"]?.GetValue<bool>() ?? true;
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["language"] = SelectedLanguage;
        state["languages"] = new JsonArray( Languages.Select( l => ( JsonNode? )l ).ToArray() );
        state["total"] = _cliloc?.Count ?? 0;
        state["visible"] = VisibleEntries.Count;
        state["search"] = SearchText;
        state["regex"] = UseRegex;
        state["selectedNumber"] = SelectedEntry?.Number;
        state["selectedText"] = SelectedEntry?.Text;
        state["message"] = Message;
        return state;
    }
}

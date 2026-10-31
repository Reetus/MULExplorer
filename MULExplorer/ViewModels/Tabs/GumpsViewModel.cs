using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>One gump in the list: thumbnail, id, and the name/tags known from the gump list.</summary>
public sealed class GumpRow( int id, GumpName? name, Func<int, Bitmap?> thumbnails, Func<int, string> size )
{
    public int Id { get; } = id;

    public string IdText => $"0x{Id:X4} ({Id})";

    public string? Name => name?.Name;

    public bool HasName => name is not null;

    public string? TagsText => name is { Tags.Count: > 0 } n ? string.Join( " ", n.Tags.Select( t => "#" + t ) ) : null;

    public string SizeText => size( Id );

    public Bitmap? Thumbnail => thumbnails( Id );

    internal GumpName? Known => name;
}

/// <summary>
///     Gumps tab, after UOFiddler's GumpControl: a list of every gump with thumbnail, id, name and
///     tags (filterable by name/id and tag), a full-size preview and an optional hue.
/// </summary>
public sealed partial class GumpsViewModel : TabViewModel
{
    private List<GumpRow> _all = [];
    private BitmapCache? _thumbnails;
    private bool _suppressFilter;

    public GumpsViewModel() : base( "Gumps", Strings.TabGumps )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<GumpRow> VisibleRows { get; private set; } = [];

    [ObservableProperty]
    public partial GumpRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string GoToText { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedTag { get; set; } = GumpsStrings.AllTags;

    [ObservableProperty]
    public partial int PreviewHue { get; set; }

    [ObservableProperty]
    public partial bool OnlyGray { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = "";

    public IReadOnlyList<string> TagOptions { get; } = [GumpsStrings.AllTags, .. GumpNames.Tags];

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public int SelectedId => SelectedRow?.Id ?? -1;

    public override IReadOnlyList<string> Actions => ["set_preview_hue", "set_tag"];

    protected override async Task LoadAsync( UOData data )
    {
        var gumps = data.Gumps;
        var ids = await Task.Run( () => Enumerable.Range( 0, gumps.Count ).Where( gumps.IsValid ).ToList() );

        _thumbnails = new BitmapCache( id => gumps.Get( id ).ToBitmap(), 256 );
        var thumbs = _thumbnails;
        _all = ids.Select( id =>
        {
            GumpNames.TryGet( id, out var name );
            return new GumpRow( id, name, i => thumbs.Get( i ), i => gumps.GetSize( i ) is { } s ? $"{s.Width} × {s.Height}" : "-" );
        } ).ToList();

        ApplyFilter();
        SelectedRow ??= VisibleRows.FirstOrDefault();
        UpdateSelection();
    }

    protected override void Reset()
    {
        _thumbnails?.Clear();
        _thumbnails = null;
        _all = [];
        VisibleRows = [];
        SelectedRow = null;
        Preview = null;
        Details.Clear();
        Summary = "";
        SelectionText = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedTagChanged( string value ) => ApplyFilter();

    partial void OnSelectedRowChanged( GumpRow? value ) => UpdateSelection();

    partial void OnPreviewHueChanged( int value ) => UpdateSelection();

    partial void OnOnlyGrayChanged( bool value ) => UpdateSelection();

    partial void OnGoToTextChanged( string value )
    {
        if ( ParseId( value ) is { } id )
        {
            SelectEntry( id );
        }
    }

    private void ApplyFilter()
    {
        if ( _suppressFilter )
        {
            return;
        }

        var text = SearchText.Trim();
        var tag = SelectedTag == GumpsStrings.AllTags || string.IsNullOrEmpty( SelectedTag ) ? null : SelectedTag;
        var id = ParseId( text );
        IEnumerable<GumpRow> rows = _all;

        if ( tag is not null )
        {
            rows = rows.Where( r => r.Known?.HasTag( tag ) == true );
        }

        if ( text.Length > 0 )
        {
            rows = rows.Where( r => r.Id == id || r.Name?.Contains( text, StringComparison.OrdinalIgnoreCase ) == true );
        }

        var selected = SelectedRow;
        VisibleRows = tag is null && text.Length == 0 ? _all : rows.ToList();
        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleRows.Count, _all.Count );

        // The list may drop its selection when its source is replaced; restore it.
        SelectedRow = selected is not null && VisibleRows.Contains( selected ) ? selected : VisibleRows.FirstOrDefault();
    }

    private void UpdateSelection()
    {
        Details.Clear();
        OnPropertyChanged( nameof( SelectedId ) );

        if ( Data is not { } data || SelectedRow is not { } row )
        {
            Preview = null;
            SelectionText = "";
            return;
        }

        var pixmap = data.Gumps.Get( row.Id );

        if ( pixmap is not null && PreviewHue > 0 )
        {
            pixmap = data.Hues.Apply( pixmap, PreviewHue, OnlyGray );
        }

        Preview = pixmap.ToBitmap();
        var size = pixmap is null ? "-" : $"{pixmap.Width} × {pixmap.Height}";
        SelectionText = $"{Strings.Id}: {row.IdText}    {Strings.Size}: {size}";

        Details.Add( new PropertyRow( Strings.Id, row.IdText ) );
        Details.Add( new PropertyRow( Strings.Size, size ) );

        if ( row.HasName )
        {
            Details.Add( new PropertyRow( Strings.Name, row.Name! ) );
        }

        if ( row.TagsText is { } tags )
        {
            Details.Add( new PropertyRow( GumpsStrings.Tags, tags ) );
        }
    }

    private static int? ParseId( string text )
    {
        var s = text.AsSpan().Trim();

        if ( s.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) )
        {
            return int.TryParse( s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex ) ? hex : null;
        }

        return int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec ) ? dec : null;
    }

    public override bool SelectEntry( int id )
    {
        if ( _all.Find( r => r.Id == id ) is not { } row )
        {
            return false;
        }

        if ( !VisibleRows.Contains( row ) )
        {
            _suppressFilter = true;
            SearchText = "";
            SelectedTag = GumpsStrings.AllTags;
            _suppressFilter = false;
            ApplyFilter();
        }

        SelectedRow = row;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleRows.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_preview_hue":
                OnlyGray = args["only_gray"]?.GetValue<bool>() ?? OnlyGray;
                PreviewHue = args["hue"]?.GetValue<int>() ?? 0;
                return GetState();
            case "set_tag":
                var tag = args["tag"]?.GetValue<string>();
                SelectedTag = string.IsNullOrEmpty( tag ) ? GumpsStrings.AllTags
                    : TagOptions.FirstOrDefault( t => t.Equals( tag, StringComparison.OrdinalIgnoreCase ) ) ?? throw new ArgumentException( $"Unknown tag '{tag}'." );
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleRows.Count;
        state["search"] = SearchText;
        state["tag"] = SelectedTag == GumpsStrings.AllTags ? null : SelectedTag;
        state["selectedId"] = SelectedId;
        state["selectedName"] = SelectedRow?.Name;
        state["previewHue"] = PreviewHue;
        state["onlyGray"] = OnlyGray;
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}

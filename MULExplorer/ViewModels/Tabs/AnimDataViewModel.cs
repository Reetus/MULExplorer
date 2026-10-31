using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Imaging;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>An animated item in the AnimData list.</summary>
public sealed record AnimDataRow( int Id, string Label );

/// <summary>One frame of an item animation: offset, resulting item id and its art.</summary>
public sealed record AnimDataFrameRow( int Index, int Offset, int ItemId, string ItemLabel, Bitmap? Image );

/// <summary>
///     AnimData tab, a read-only take on UOFiddler's AnimDataControl: items with an animdata.mul
///     entry, their frame offsets, timing, and an animated preview cycling through the frame art.
/// </summary>
public sealed partial class AnimDataViewModel : TabViewModel
{
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<AnimDataRow> _all = [];
    private Bitmap?[] _frameBitmaps = [];

    public AnimDataViewModel() : base( "AnimData", Strings.TabAnimData )
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds( 100 ) };
        _timer.Tick += ( _, _ ) => Step();
    }

    [ObservableProperty]
    public partial IReadOnlyList<AnimDataRow> VisibleEntries { get; private set; } = [];

    [ObservableProperty]
    public partial AnimDataRow? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int FrameIndex { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; } = true;

    [ObservableProperty]
    public partial int PreviewHue { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial string FrameLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<AnimDataFrameRow> Frames { get; private set; } = [];

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public int SelectedId => SelectedEntry?.Id ?? -1;

    public override IReadOnlyList<string> Actions => ["set_frame", "play", "pause", "set_hue"];

    protected override async Task LoadAsync( UOData data )
    {
        var rows = await Task.Run( () =>
        {
            var tiles = data.TileData;
            return data.AnimData.Ids.Select( id => new AnimDataRow( id, $"0x{id:X4} {tiles.GetItem( id ).Name}" ) ).ToList();
        } );

        _all = rows;
        ApplyFilter();

        if ( SelectedEntry is null && VisibleEntries.Count > 0 )
        {
            SelectedEntry = VisibleEntries[0];
        }
        else
        {
            UpdateSelection();
        }
    }

    protected override void Reset()
    {
        _timer.Stop();
        _all = [];
        VisibleEntries = [];
        SelectedEntry = null;
        Frames = [];
        Details.Clear();
        _frameBitmaps = [];
        Preview = null;
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedEntryChanged( AnimDataRow? value ) => UpdateSelection();

    partial void OnPreviewHueChanged( int value ) => UpdateSelection();

    partial void OnFrameIndexChanged( int value ) => ShowFrame();

    partial void OnIsPlayingChanged( bool value ) => UpdateTimer();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

        VisibleEntries = text.Length == 0
            ? _all
            : _all.Where( r => r.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                               r.Id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) ).ToList();

        Summary = string.Format( CultureInfo.CurrentCulture, AnimDataStrings.ItemsShown, VisibleEntries.Count, _all.Count );

        if ( SelectedEntry is null || !VisibleEntries.Contains( SelectedEntry ) )
        {
            SelectedEntry = VisibleEntries.Count > 0 ? VisibleEntries[0] : null;
        }
    }

    private void UpdateSelection()
    {
        var keepFrame = FrameIndex;
        Details.Clear();
        _frameBitmaps = [];

        if ( Data is not { } data || SelectedEntry is not { } row || data.AnimData.Get( row.Id ) is not { } entry )
        {
            Frames = [];
            Preview = null;
            FrameLabel = "";
            _timer.Stop();
            return;
        }

        var partial = data.TileData.GetItem( row.Id ).Flags.HasFlag( TileFlag.PartialHue );
        var arts = new Pixmap?[entry.FrameCount];

        for ( var i = 0; i < arts.Length; i++ )
        {
            var art = data.Art.GetItem( entry.FrameItem( i ) );
            arts[i] = art is not null && PreviewHue > 0 ? data.Hues.Apply( art, PreviewHue, partial ) : art;
        }

        // Bottom-centre every frame on one canvas so the preview does not jump.
        var width = Math.Max( 1, arts.Max( a => a?.Width ?? 0 ) );
        var height = Math.Max( 1, arts.Max( a => a?.Height ?? 0 ) );
        _frameBitmaps = arts.Select( a =>
        {
            if ( a is null )
            {
                return null;
            }

            var canvas = new Pixmap( width, height );
            canvas.Blit( a, ( width - a.Width ) / 2, height - a.Height );
            return ( Bitmap? )canvas.ToBitmap();
        } ).ToArray();

        Frames = Enumerable.Range( 0, entry.FrameCount ).Select( i =>
        {
            var item = entry.FrameItem( i );
            return new AnimDataFrameRow( i, entry.Offsets[Math.Min( i, entry.Offsets.Length - 1 )], item,
                $"0x{item:X4} {data.TileData.GetItem( item ).Name}", _frameBitmaps[i] );
        } ).ToList();

        Details.Add( new PropertyRow( Strings.Id, $"0x{row.Id:X4} ({row.Id})" ) );
        Details.Add( new PropertyRow( Strings.Name, data.TileData.GetItem( row.Id ).Name ) );
        Details.Add( new PropertyRow( AnimDataStrings.FrameCount, entry.FrameCount.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( AnimDataStrings.FrameDelay,
            string.Format( CultureInfo.CurrentCulture, AnimDataStrings.DelayValue, entry.FrameInterval, entry.FrameDelayMilliseconds ) ) );
        Details.Add( new PropertyRow( AnimDataStrings.StartDelay,
            string.Format( CultureInfo.CurrentCulture, AnimDataStrings.DelayValue, entry.StartInterval, entry.StartInterval * 100 ) ) );
        Details.Add( new PropertyRow( AnimDataStrings.Unknown, entry.Unknown.ToString( CultureInfo.CurrentCulture ) ) );

        _timer.Interval = TimeSpan.FromMilliseconds( entry.FrameDelayMilliseconds );

        // Replacing the frame list resets its selection (and through the binding FrameIndex), so restore it.
        FrameIndex = keepFrame >= 0 && keepFrame < _frameBitmaps.Length ? keepFrame : 0;
        OnPropertyChanged( nameof( FrameIndex ) );
        ShowFrame();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if ( IsPlaying && _frameBitmaps.Length > 1 )
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Step()
    {
        if ( _frameBitmaps.Length > 0 )
        {
            FrameIndex = ( FrameIndex + 1 ) % _frameBitmaps.Length;
        }
    }

    private void ShowFrame()
    {
        Preview = ( uint )FrameIndex < ( uint )_frameBitmaps.Length ? _frameBitmaps[FrameIndex] : null;
        FrameLabel = _frameBitmaps.Length == 0 ? "" : string.Format( CultureInfo.CurrentCulture, AnimDataStrings.CurrentFrame, FrameIndex, _frameBitmaps.Length );
    }

    public override bool SelectEntry( int id )
    {
        if ( _all.FirstOrDefault( r => r.Id == id ) is not { } row )
        {
            return false;
        }

        if ( !VisibleEntries.Contains( row ) )
        {
            SearchText = "";
        }

        SelectedEntry = row;
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
            case "set_frame":
                var frame = args["frame"]?.GetValue<int>() ?? 0;
                IsPlaying = false;
                FrameIndex = ( uint )frame < ( uint )_frameBitmaps.Length ? frame : throw new ArgumentException( $"Frame must be 0-{_frameBitmaps.Length - 1}." );
                break;
            case "play":
                IsPlaying = true;
                break;
            case "pause":
                IsPlaying = false;
                break;
            case "set_hue":
                PreviewHue = args["hue"]?.GetValue<int>() ?? 0;
                break;
            default:
                return base.Invoke( action, args );
        }

        return GetState();
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleEntries.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedId;
        state["selectedName"] = Details.FirstOrDefault( d => d.Label == Strings.Name )?.Value;
        state["frameCount"] = Frames.Count;
        state["frame"] = FrameIndex;
        state["frameItems"] = new JsonArray( Frames.Select( f => ( JsonNode? )$"0x{f.ItemId:X4}" ).ToArray() );
        state["playing"] = IsPlaying;
        state["hue"] = PreviewHue;
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}

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

/// <summary>A node of the body tree: a group (Body = -1) with children, or a body.</summary>
public sealed partial class AnimationTreeNode( int body, string label, IReadOnlyList<AnimationTreeNode> children ) : ObservableObject
{
    public int Body { get; } = body;

    public string Label { get; } = label;

    public IReadOnlyList<AnimationTreeNode> Children { get; } = children;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>An action in the action picker.</summary>
public sealed record ActionChoice( int Action, string Label )
{
    public override string ToString() => Label;
}

/// <summary>One frame in the frame strip.</summary>
public sealed record FrameThumb( int Index, Bitmap? Image );

/// <summary>
///     Animations tab, after UOFiddler's AnimationListControl: bodies grouped by type, an action
///     picker, 8 directions (5 stored, 3 mirrored), a frame strip, playback, hue preview and frame
///     info. A thumbnail list shows every body's first frame.
/// </summary>
public sealed partial class AnimationsViewModel : TabViewModel
{
    private static readonly MobType[] GroupOrder = [MobType.Monster, MobType.SeaMonster, MobType.Animal, MobType.Human, MobType.Equipment];

    private readonly DispatcherTimer _timer;
    private IReadOnlyList<(int Body, string Label, MobType Group)> _all = [];
    private BitmapCache? _thumbnails;
    private AnimationFrame[] _frames = [];
    private Bitmap?[] _frameBitmaps = [];
    private bool _syncing;

    public AnimationsViewModel() : base( "Animations", Strings.TabAnimations )
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds( 150 ) };
        _timer.Tick += ( _, _ ) => Step();
    }

    public ObservableCollection<AnimationTreeNode> Groups { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<int> VisibleBodies { get; private set; } = [];

    [ObservableProperty]
    public partial object? SelectedNode { get; set; }

    [ObservableProperty]
    public partial int SelectedBody { get; set; } = -1;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public ObservableCollection<ActionChoice> ActionChoices { get; } = [];

    [ObservableProperty]
    public partial ActionChoice? SelectedActionChoice { get; set; }

    /// <summary>0..7; 5..7 mirror 3..1.</summary>
    [ObservableProperty]
    public partial int Direction { get; set; } = 1;

    [ObservableProperty]
    public partial int FrameIndex { get; set; }

    [ObservableProperty]
    public partial int FrameCount { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool ShowFrameBounds { get; set; }

    /// <summary>Game hue applied to the frames (0 = none; Body.def's hue is used for remapped bodies).</summary>
    [ObservableProperty]
    public partial int PreviewHue { get; set; }

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial string DirectionLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string StatusGraphic { get; private set; } = "";

    [ObservableProperty]
    public partial string StatusBaseGraphic { get; private set; } = "";

    [ObservableProperty]
    public partial string StatusHue { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<FrameThumb> FrameStrip { get; private set; } = [];

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public Func<int, Bitmap?> ThumbnailProvider => id => _thumbnails?.Get( id );

    public int SelectedAction => SelectedActionChoice?.Action ?? -1;

    public override IReadOnlyList<string> Actions => ["set_action", "set_direction", "set_frame", "play", "pause", "set_hue", "show_frame_bounds"];

    protected override async Task LoadAsync( UOData data )
    {
        var entries = await Task.Run( () =>
        {
            var animations = data.Animations;
            var list = new List<(int Body, string Label, MobType Group)>();

            foreach ( var body in animations.CandidateBodies() )
            {
                if ( !animations.HasAnimation( body ) )
                {
                    continue;
                }

                var name = AnimationNames.Get( body );
                var group = name?.Group ?? animations.GetBody( body ).Type;
                list.Add( ( body, $"{name?.Name ?? "Body"} (0x{body:X})", group ) );
            }

            return list;
        } );

        _thumbnails = new BitmapCache( body => FirstFrame( data, body ).ToBitmap() );
        _all = entries;
        ApplyFilter();
        OnPropertyChanged( nameof( ThumbnailProvider ) );

        if ( SelectedBody < 0 && entries.Count > 0 )
        {
            SelectEntry( entries.Any( e => e.Body == 400 ) ? 400 : entries[0].Body );
        }
        else
        {
            UpdateBody();
        }
    }

    protected override void Reset()
    {
        IsPlaying = false;
        _thumbnails?.Clear();
        _thumbnails = null;
        _all = [];
        Groups.Clear();
        VisibleBodies = [];
        ActionChoices.Clear();
        FrameStrip = [];
        Details.Clear();
        _frames = [];
        _frameBitmaps = [];
        Preview = null;
        Summary = "";
    }

    private static Pixmap? FirstFrame( UOData data, int body )
    {
        var info = data.Animations.GetBody( body );

        if ( !info.HasAnimation )
        {
            return null;
        }

        var frames = data.Animations.GetFrames( body, info.Actions[0], 1 );

        if ( info.Hue > 0 )
        {
            frames = Animations.ApplyHue( frames, data.Hues, info.Hue );
        }

        return frames.FirstOrDefault( f => !f.IsEmpty ).Image;
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedNodeChanged( object? value )
    {
        if ( value is AnimationTreeNode { Body: >= 0 } node && !_syncing )
        {
            SelectedBody = node.Body;
        }
    }

    partial void OnSelectedBodyChanged( int value )
    {
        SyncTreeSelection();
        UpdateBody();
    }

    partial void OnSelectedActionChoiceChanged( ActionChoice? value )
    {
        if ( !_syncing )
        {
            UpdateFrames();
        }
    }

    partial void OnDirectionChanged( int value )
    {
        if ( !_syncing )
        {
            UpdateFrames();
        }
    }

    partial void OnPreviewHueChanged( int value ) => UpdateFrames();

    partial void OnShowFrameBoundsChanged( bool value ) => UpdateFrames();

    partial void OnFrameIndexChanged( int value ) => ShowFrame();

    partial void OnIsPlayingChanged( bool value )
    {
        if ( value && FrameCount > 1 )
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
        if ( FrameCount > 0 )
        {
            FrameIndex = ( FrameIndex + 1 ) % FrameCount;
        }
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : null;
        var number = hex is not null
            ? int.TryParse( hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h ) ? h : -1
            : int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d ) ? d : -1;

        var visible = text.Length == 0
            ? _all
            : _all.Where( e => e.Body == number || e.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();

        Groups.Clear();

        foreach ( var group in GroupOrder )
        {
            var children = visible.Where( e => e.Group == group ).Select( e => new AnimationTreeNode( e.Body, e.Label, [] ) ).ToList();

            if ( children.Count > 0 )
            {
                Groups.Add( new AnimationTreeNode( -1, $"{GroupLabel( group )} ({children.Count})", children ) { IsExpanded = text.Length > 0 } );
            }
        }

        VisibleBodies = visible.Select( e => e.Body ).ToList();
        Summary = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.BodiesShown, VisibleBodies.Count, _all.Count );
        SyncTreeSelection();
    }

    private static string GroupLabel( MobType group ) => group switch
    {
        MobType.SeaMonster => AnimationsStrings.GroupSea,
        MobType.Animal => AnimationsStrings.GroupAnimals,
        MobType.Human => AnimationsStrings.GroupHumans,
        MobType.Equipment => AnimationsStrings.GroupEquipment,
        _ => AnimationsStrings.GroupMonsters
    };

    private void SyncTreeSelection()
    {
        _syncing = true;

        try
        {
            foreach ( var group in Groups )
            {
                if ( group.Children.FirstOrDefault( c => c.Body == SelectedBody ) is { } node )
                {
                    group.IsExpanded = true;
                    SelectedNode = node;
                    return;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateBody()
    {
        _syncing = true;

        try
        {
            ActionChoices.Clear();

            if ( Data is { } data && SelectedBody >= 0 && _all.Count > 0 )
            {
                var info = data.Animations.GetBody( SelectedBody );

                foreach ( var action in info.Actions )
                {
                    ActionChoices.Add( new ActionChoice( action, $"{action} {data.Animations.GetActionName( SelectedBody, action )}" ) );
                }
            }

            var previous = SelectedActionChoice?.Action ?? -1;
            SelectedActionChoice = ActionChoices.FirstOrDefault( c => c.Action == previous ) ?? ActionChoices.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }

        UpdateFrames();
    }

    private void UpdateFrames()
    {
        var keepFrame = FrameIndex;
        Details.Clear();
        _frames = [];
        _frameBitmaps = [];
        DirectionLabel = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.DirectionValue, Direction, DirectionName( Direction ) );

        if ( Data is not { } data || SelectedBody < 0 || _all.Count == 0 )
        {
            FrameStrip = [];
            FrameCount = 0;
            Preview = null;
            return;
        }

        var info = data.Animations.GetBody( SelectedBody );
        var hue = PreviewHue > 0 ? PreviewHue : info.Hue;

        if ( SelectedActionChoice is { } choice )
        {
            _frames = data.Animations.GetFrames( SelectedBody, choice.Action, Math.Clamp( Direction, 0, 7 ) );

            if ( hue > 0 )
            {
                _frames = Animations.ApplyHue( _frames, data.Hues, hue );
            }
        }

        var images = Animations.RenderLayers( [_frames], 4 );

        if ( ShowFrameBounds )
        {
            DrawBounds( images, _frames );
        }

        _frameBitmaps = images.Select( p => ( Bitmap? )p.ToBitmap() ).ToArray();
        FrameCount = _frameBitmaps.Length;
        // Replacing the strip resets the list's selection (and through the binding FrameIndex), so restore it after.
        FrameStrip = _frameBitmaps.Select( ( b, i ) => new FrameThumb( i, b ) ).ToList();

        Details.Add( new PropertyRow( AnimationsStrings.Graphic, $"0x{SelectedBody:X4} ({SelectedBody})" ) );
        Details.Add( new PropertyRow( Strings.Name, AnimationNames.Get( SelectedBody )?.Name ?? "-" ) );
        Details.Add( new PropertyRow( AnimationsStrings.BaseGraphic, $"0x{info.FileBody:X4} ({info.FileBody})" ) );
        Details.Add( new PropertyRow( AnimationsStrings.Source, info.FileName ) );
        Details.Add( new PropertyRow( AnimationsStrings.Type, info.Type.ToString() ) );
        Details.Add( new PropertyRow( AnimationsStrings.MobFlags, info.HasMobType ? $"0x{( uint )info.Flags:X} ({info.Flags})" : "-" ) );

        if ( info.BodyDefTarget is { } target )
        {
            Details.Add( new PropertyRow( AnimationsStrings.BodyDef, $"0x{target:X4}, {Strings.Hue} {info.Hue}" ) );
        }

        Details.Add( new PropertyRow( AnimationsStrings.Frames, FrameCount.ToString( CultureInfo.CurrentCulture ) ) );

        StatusGraphic = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.StatusGraphic, $"0x{SelectedBody:X} ({SelectedBody})" );
        StatusBaseGraphic = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.StatusBaseGraphic, $"0x{info.FileBody:X} ({info.FileBody})" );
        StatusHue = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.StatusHue, hue );

        FrameIndex = keepFrame >= 0 && keepFrame < FrameCount ? keepFrame : 0;
        OnPropertyChanged( nameof( FrameIndex ) );
        ShowFrame();
        OnIsPlayingChanged( IsPlaying );
    }

    private void ShowFrame()
    {
        Preview = ( uint )FrameIndex < ( uint )_frameBitmaps.Length ? _frameBitmaps[FrameIndex] : null;

        while ( Details.Count > 0 && Details[^1].Label is var label && ( label == AnimationsStrings.FrameSize || label == AnimationsStrings.Center ) )
        {
            Details.RemoveAt( Details.Count - 1 );
        }

        if ( ( uint )FrameIndex < ( uint )_frames.Length )
        {
            var f = _frames[FrameIndex];
            Details.Add( new PropertyRow( AnimationsStrings.FrameSize, f.IsEmpty ? "-" : $"{f.Width} × {f.Height}" ) );
            Details.Add( new PropertyRow( AnimationsStrings.Center, $"{f.CenterX}, {f.CenterY}" ) );
        }
    }

    /// <summary>Outlines each frame's own rectangle on its aligned canvas.</summary>
    private static void DrawBounds( Pixmap[] images, AnimationFrame[] frames )
    {
        var (left, top, _, _) = Animations.Bounds( frames );

        for ( var i = 0; i < images.Length && i < frames.Length; i++ )
        {
            if ( frames[i].IsEmpty )
            {
                continue;
            }

            var x0 = frames[i].Left - left + 4;
            var y0 = frames[i].Top - top + 4;
            var x1 = x0 + frames[i].Width - 1;
            var y1 = y0 + frames[i].Height - 1;
            const uint Color = 0xFFFF3030;

            for ( var x = x0; x <= x1; x++ )
            {
                images[i][x, y0] = Color;
                images[i][x, y1] = Color;
            }

            for ( var y = y0; y <= y1; y++ )
            {
                images[i][x0, y] = Color;
                images[i][x1, y] = Color;
            }
        }
    }

    public static string DirectionName( int direction ) => direction switch
    {
        0 => AnimationsStrings.Dir0,
        1 => AnimationsStrings.Dir1,
        2 => AnimationsStrings.Dir2,
        3 => AnimationsStrings.Dir3,
        4 => AnimationsStrings.Dir4,
        5 => AnimationsStrings.Dir5,
        6 => AnimationsStrings.Dir6,
        _ => AnimationsStrings.Dir7
    };

    public override bool SelectEntry( int id )
    {
        if ( _all.All( e => e.Body != id ) )
        {
            return false;
        }

        if ( !VisibleBodies.Contains( id ) )
        {
            SearchText = "";
        }

        SelectedBody = id;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleBodies.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_action":
                var wanted = args["action"]?.GetValue<int>() ?? 0;
                SelectedActionChoice = ActionChoices.FirstOrDefault( c => c.Action == wanted )
                                       ?? throw new ArgumentException( $"Body 0x{SelectedBody:X} has no action {wanted}." );
                break;
            case "set_direction":
                var direction = args["direction"]?.GetValue<int>() ?? 0;
                Direction = ( uint )direction < 8 ? direction : throw new ArgumentException( "Direction must be 0-7." );
                break;
            case "set_frame":
                var frame = args["frame"]?.GetValue<int>() ?? 0;
                FrameIndex = ( uint )frame < ( uint )FrameCount ? frame : throw new ArgumentException( $"Frame must be 0-{FrameCount - 1}." );
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
            case "show_frame_bounds":
                ShowFrameBounds = args["show"]?.GetValue<bool>() ?? true;
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
        state["visible"] = VisibleBodies.Count;
        state["search"] = SearchText;
        state["selectedBody"] = SelectedBody;
        state["selectedName"] = AnimationNames.Get( SelectedBody )?.Name;
        state["source"] = Details.FirstOrDefault( d => d.Label == AnimationsStrings.Source )?.Value;
        state["definedActions"] = new JsonArray( ActionChoices.Select( c => ( JsonNode? )c.Action ).ToArray() );
        state["action"] = SelectedAction;
        state["direction"] = Direction;
        state["frame"] = FrameIndex;
        state["frameCount"] = FrameCount;
        state["playing"] = IsPlaying;
        state["hue"] = PreviewHue;
        state["showFrameBounds"] = ShowFrameBounds;
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        return state;
    }
}

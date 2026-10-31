using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A wearable item in the item list.</summary>
public sealed record WearableRow( int Id, int Layer, string Label );

/// <summary>An item currently worn.</summary>
public sealed record WornRow( int Layer, int ItemId, int Hue, string Label );

/// <summary>A body in the body picker.</summary>
public sealed record DressBodyChoice( DressBody Body, string Label );

/// <summary>
///     Dress tab, a read-only take on UOFiddler's DressControl: pick a body, put wearable items on
///     (one per layer), and preview the paperdoll and the animated in-world figure. The outfit only
///     lives in memory.
/// </summary>
public sealed partial class DressViewModel : TabViewModel
{
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<WearableRow> _all = [];
    private Bitmap?[] _figureFrames = [];
    private DressRenderer? _renderer;

    public DressViewModel() : base( "Dress", Strings.TabDress )
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds( 150 ) };
        _timer.Tick += ( _, _ ) => Step();
        BodyChoices =
        [
            new DressBodyChoice( DressBody.HumanMale, DressStrings.BodyHumanMale ),
            new DressBodyChoice( DressBody.HumanFemale, DressStrings.BodyHumanFemale ),
            new DressBodyChoice( DressBody.ElfMale, DressStrings.BodyElfMale ),
            new DressBodyChoice( DressBody.ElfFemale, DressStrings.BodyElfFemale ),
            new DressBodyChoice( DressBody.GargoyleMale, DressStrings.BodyGargoyleMale ),
            new DressBodyChoice( DressBody.GargoyleFemale, DressStrings.BodyGargoyleFemale )
        ];
        SelectedBodyChoice = BodyChoices[0];
    }

    public IReadOnlyList<DressBodyChoice> BodyChoices { get; }

    [ObservableProperty]
    public partial DressBodyChoice SelectedBodyChoice { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<WearableRow> VisibleWearables { get; private set; } = [];

    [ObservableProperty]
    public partial WearableRow? SelectedWearable { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public ObservableCollection<WornRow> Worn { get; } = [];

    [ObservableProperty]
    public partial WornRow? SelectedWorn { get; set; }

    [ObservableProperty]
    public partial int BodyHue { get; set; }

    /// <summary>Hue given to the next dressed item (0 = none).</summary>
    [ObservableProperty]
    public partial int ItemHue { get; set; }

    [ObservableProperty]
    public partial int Action { get; set; } = 4;

    [ObservableProperty]
    public partial int Direction { get; set; } = 1;

    [ObservableProperty]
    public partial int FigureFrame { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial Bitmap? Paperdoll { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Figure { get; private set; }

    [ObservableProperty]
    public partial string DirectionLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string ActionLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public DressBody Body => SelectedBodyChoice.Body;

    public override IReadOnlyList<string> Actions =>
        ["equip", "unequip", "undress_all", "set_body", "set_body_hue", "set_action", "set_direction", "set_frame", "play", "pause"];

    protected override async Task LoadAsync( UOData data )
    {
        var renderer = new DressRenderer( data );
        var rows = await Task.Run( () =>
        {
            _ = data.Animations;
            _ = data.Gumps;
            return data.TileData.ItemTable
                .Where( i => renderer.IsWearable( i.Id ) && data.Art.IsValidItem( i.Id ) )
                .Select( i => new WearableRow( i.Id, i.Layer, $"0x{i.Id:X4} {i.Name}" ) )
                .ToList();
        } );

        _renderer = renderer;
        _all = rows;
        ApplyFilter();
        Render();
    }

    protected override void Reset()
    {
        _timer.Stop();
        _renderer = null;
        _all = [];
        VisibleWearables = [];
        Worn.Clear();
        Paperdoll = null;
        Figure = null;
        _figureFrames = [];
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedBodyChoiceChanged( DressBodyChoice value ) => Render();

    partial void OnBodyHueChanged( int value ) => Render();

    partial void OnActionChanged( int value ) => RenderFigure();

    partial void OnDirectionChanged( int value ) => RenderFigure();

    partial void OnFigureFrameChanged( int value ) => ShowFigureFrame();

    partial void OnIsPlayingChanged( bool value ) => UpdateTimer();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;

        VisibleWearables = text.Length == 0
            ? _all
            : _all.Where( r => r.Label.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                               r.Id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) ).ToList();

        Summary = string.Format( CultureInfo.CurrentCulture, DressStrings.ItemsShown, VisibleWearables.Count, _all.Count );
    }

    [RelayCommand]
    private void DressSelected()
    {
        if ( SelectedWearable is { } row )
        {
            Equip( row.Id, ItemHue );
        }
    }

    [RelayCommand]
    private void UndressSelected()
    {
        if ( SelectedWorn is { } worn )
        {
            Unequip( worn.Layer );
        }
    }

    [RelayCommand]
    private void UndressAll()
    {
        Worn.Clear();
        Render();
    }

    /// <summary>Wears an item, replacing whatever is on its layer. False if the item is not wearable.</summary>
    public bool Equip( int itemId, int hue )
    {
        if ( Data is not { } data || _renderer is null || !_renderer.IsWearable( itemId ) )
        {
            return false;
        }

        var item = data.TileData.GetItem( itemId );
        var existing = Worn.FirstOrDefault( w => w.Layer == item.Layer );

        if ( existing is not null )
        {
            Worn.Remove( existing );
        }

        var row = new WornRow( item.Layer, itemId, hue, string.Format( CultureInfo.CurrentCulture, DressStrings.WornItem, item.Name.Length > 0 ? item.Name : "-", itemId, item.Layer ) );
        var index = 0;

        while ( index < Worn.Count && Worn[index].Layer < item.Layer )
        {
            index++;
        }

        Worn.Insert( index, row );
        SelectedWorn = row;
        Render();
        return true;
    }

    public bool Unequip( int layer )
    {
        if ( Worn.FirstOrDefault( w => w.Layer == layer ) is not { } row )
        {
            return false;
        }

        Worn.Remove( row );
        Render();
        return true;
    }

    private List<DressItem> Outfit() => Worn.Select( w => new DressItem( w.ItemId, w.Hue ) ).ToList();

    private void Render()
    {
        Paperdoll = _renderer?.RenderPaperdoll( Body, BodyHue, Outfit() ).ToBitmap();
        RenderFigure();
    }

    private void RenderFigure()
    {
        DirectionLabel = string.Format( CultureInfo.CurrentCulture, AnimationsStrings.DirectionValue, Direction, AnimationsViewModel.DirectionName( Direction ) );
        ActionLabel = $"{Action} {Animations.GetActionName( MobType.Human, Action )}";

        if ( _renderer is null )
        {
            _figureFrames = [];
            ShowFigureFrame();
            return;
        }

        _figureFrames = _renderer.RenderFigure( Body, BodyHue, Outfit(), Action, Math.Clamp( Direction, 0, 7 ) )
            .Select( p => ( Bitmap? )p.ToBitmap() )
            .ToArray();

        if ( FigureFrame >= _figureFrames.Length )
        {
            FigureFrame = 0;
        }

        ShowFigureFrame();
        UpdateTimer();
    }

    private void ShowFigureFrame() => Figure = ( uint )FigureFrame < ( uint )_figureFrames.Length ? _figureFrames[FigureFrame] : null;

    private void UpdateTimer()
    {
        if ( IsPlaying && _figureFrames.Length > 1 )
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
        if ( _figureFrames.Length > 0 )
        {
            FigureFrame = ( FigureFrame + 1 ) % _figureFrames.Length;
        }
    }

    public override bool SelectEntry( int id )
    {
        if ( _all.FirstOrDefault( r => r.Id == id ) is not { } row )
        {
            return false;
        }

        if ( !VisibleWearables.Contains( row ) )
        {
            SearchText = "";
        }

        SelectedWearable = row;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleWearables.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "equip":
                var id = ParseId( args["id"] ) ?? throw new ArgumentException( "Missing 'id'." );

                if ( !Equip( id, ParseId( args["hue"] ) ?? 0 ) )
                {
                    throw new ArgumentException( $"Item 0x{id:X4} is not wearable." );
                }

                break;
            case "unequip":
                var layer = args["layer"] is { } l ? ParseId( l ) : Data?.TileData.GetItem( ParseId( args["id"] ) ?? -1 ).Layer;

                if ( layer is null || !Unequip( layer.Value ) )
                {
                    throw new ArgumentException( "Nothing is worn on that layer." );
                }

                break;
            case "undress_all":
                UndressAll();
                break;
            case "set_body":
                var name = args["body"]?.GetValue<string>() ?? "";
                SelectedBodyChoice = Enum.TryParse<DressBody>( name, true, out var body )
                    ? BodyChoices.First( c => c.Body == body )
                    : throw new ArgumentException( $"Unknown body '{name}'. One of: {string.Join( ", ", Enum.GetNames<DressBody>() )}." );
                break;
            case "set_body_hue":
                BodyHue = ParseId( args["hue"] ) ?? 0;
                break;
            case "set_action":
                Action = Math.Clamp( args["action"]?.GetValue<int>() ?? 0, 0, Animations.MaxActions - 1 );
                break;
            case "set_direction":
                var direction = args["direction"]?.GetValue<int>() ?? 0;
                Direction = ( uint )direction < 8 ? direction : throw new ArgumentException( "Direction must be 0-7." );
                break;
            case "set_frame":
                var frame = args["frame"]?.GetValue<int>() ?? 0;
                FigureFrame = ( uint )frame < ( uint )_figureFrames.Length ? frame : throw new ArgumentException( $"Frame must be 0-{_figureFrames.Length - 1}." );
                break;
            case "play":
                IsPlaying = true;
                break;
            case "pause":
                IsPlaying = false;
                break;
            default:
                return base.Invoke( action, args );
        }

        return GetState();
    }

    private static int? ParseId( JsonNode? node ) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<int>( out var i ) => i,
        JsonValue v when v.TryGetValue<string>( out var s ) => s.StartsWith( "0x", StringComparison.OrdinalIgnoreCase )
            ? int.TryParse( s.AsSpan( 2 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h ) ? h : null
            : int.TryParse( s, CultureInfo.InvariantCulture, out var d ) ? d : null,
        _ => null
    };

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleWearables.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedWearable?.Id ?? -1;
        state["body"] = Body.ToString();
        state["bodyHue"] = BodyHue;
        state["worn"] = new JsonArray( Worn.Select( w => ( JsonNode? )new JsonObject { ["layer"] = w.Layer, ["id"] = $"0x{w.ItemId:X4}", ["hue"] = w.Hue } ).ToArray() );
        state["action"] = Action;
        state["direction"] = Direction;
        state["frame"] = FigureFrame;
        state["frameCount"] = _figureFrames.Length;
        state["playing"] = IsPlaying;
        state["paperdollSize"] = Paperdoll is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        state["figureSize"] = Figure is { } f ? $"{f.PixelSize.Width}x{f.PixelSize.Height}" : null;
        return state;
    }
}

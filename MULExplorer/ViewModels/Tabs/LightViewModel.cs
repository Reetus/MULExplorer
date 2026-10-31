using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Mcp;
using MULExplorer.Mcp.Tools;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>One light in the list.</summary>
public sealed record LightRow( int Id, string Label );

/// <summary>
///     Light tab, after UOFiddler's LightControl: the list of light ids, the light map (raw, or
///     "in game" over a floor of a chosen land tile around a light-source item) and its light levels.
/// </summary>
public sealed partial class LightViewModel : TabViewModel
{
    private const uint Backdrop = 0xFF000000;

    public LightViewModel() : base( "Light", Strings.TabLight )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<LightRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial LightRow? SelectedRow { get; set; }

    /// <summary>UOFiddler's "IG Preview".</summary>
    [ObservableProperty]
    public partial bool InGame { get; set; }

    [ObservableProperty]
    public partial int LandTile { get; set; } = LightTools.DefaultLandTile;

    [ObservableProperty]
    public partial int LightSource { get; set; } = LightTools.DefaultLightSource;

    [ObservableProperty]
    public partial Bitmap? Preview { get; private set; }

    [ObservableProperty]
    public partial Bitmap? LandPreview { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public int SelectedId => SelectedRow?.Id ?? -1;

    public override IReadOnlyList<string> Actions => ["set_in_game", "set_land", "set_light_source"];

    protected override async Task LoadAsync( UOData data )
    {
        var ids = await Task.Run( () => data.Lights.ValidIds );
        Rows = ids.Select( id => new LightRow( id, string.Format( CultureInfo.CurrentCulture, LightStrings.LightItem, id ) ) ).ToList();
        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, Rows.Count, data.Lights.Count );
        SelectedRow ??= Rows.FirstOrDefault();
        UpdateLand();
        UpdateSelection();
    }

    protected override void Reset()
    {
        Rows = [];
        SelectedRow = null;
        Preview = null;
        LandPreview = null;
        Details.Clear();
        Summary = "";
    }

    partial void OnSelectedRowChanged( LightRow? value ) => UpdateSelection();

    partial void OnInGameChanged( bool value ) => UpdatePreview();

    partial void OnLandTileChanged( int value )
    {
        UpdateLand();
        UpdatePreview();
    }

    partial void OnLightSourceChanged( int value ) => UpdatePreview();

    private void UpdateLand() => LandPreview = Data?.TryGet( d => d.Art )?.GetLand( LandTile ).ToBitmap();

    private void UpdateSelection()
    {
        Details.Clear();
        OnPropertyChanged( nameof( SelectedId ) );

        if ( Data is not { } data || SelectedRow is not { } row )
        {
            Preview = null;
            return;
        }

        var size = data.Lights.GetSize( row.Id );
        var stats = data.Lights.GetStats( row.Id );
        Details.Add( new PropertyRow( Strings.Id, $"{row.Id} (0x{row.Id:X})" ) );
        Details.Add( new PropertyRow( Strings.Size, size is { } s ? $"{s.Width} × {s.Height}" : "-" ) );

        if ( stats is { } st )
        {
            Details.Add( new PropertyRow( LightStrings.RawMin, st.Min.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( LightStrings.RawMax, st.Max.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( LightStrings.AverageIntensity, st.Average.ToString( "0.00", CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( LightStrings.LitPixels, st.LitPixels.ToString( "N0", CultureInfo.CurrentCulture ) ) );
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if ( Data is not { } data || SelectedRow is not { } row )
        {
            Preview = null;
            return;
        }

        if ( InGame && data.TryGet( d => d.Art ) is not null )
        {
            Preview = LightTools.Preview( data, row.Id, LandTile, LightSource ).ToBitmap();
            return;
        }

        var map = data.Lights.Get( row.Id );
        Preview = map is null ? null : FontsTools.OnBackground( map, Backdrop ).ToBitmap();
    }

    public override bool SelectEntry( int id )
    {
        if ( Rows.FirstOrDefault( r => r.Id == id ) is not { } row )
        {
            return false;
        }

        SelectedRow = row;
        return true;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "set_in_game":
                InGame = args["value"]?.GetValue<bool>() ?? true;
                return GetState();
            case "set_land":
                LandTile = ToolArgs.ParseInt( args["id"]?.ToString() ) ?? LandTile;
                return GetState();
            case "set_light_source":
                LightSource = ToolArgs.ParseInt( args["id"]?.ToString() ) ?? LightSource;
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = Rows.Count;
        state["selectedId"] = SelectedId;
        state["inGame"] = InGame;
        state["landTile"] = LandTile;
        state["lightSource"] = LightSource;
        state["previewSize"] = Preview is { } p ? $"{p.PixelSize.Width}x{p.PixelSize.Height}" : null;
        state["details"] = new JsonObject( Details.Select( d => new KeyValuePair<string, JsonNode?>( d.Label, d.Value ) ) );
        return state;
    }
}

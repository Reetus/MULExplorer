using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MULExplorer.Resources;
using MULExplorer.Services;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>One line of the sound list.</summary>
public sealed record SoundRow( SoundInfo Info )
{
    public int Id => Info.Id;

    public string IdText => $"0x{Info.Id:X3}";

    public string Name => Info.Name;

    public string DurationText => string.Format( CultureInfo.CurrentCulture, SoundsStrings.DurationFormat, Info.Duration );
}

/// <summary>
///     Sounds tab (UOFiddler's SoundsControl, read-only): every playable sound with its name and
///     duration, a filter, sort by name, and play/stop with a progress bar.
/// </summary>
public sealed partial class SoundsViewModel : TabViewModel
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private IReadOnlyList<SoundRow> _all = [];
    private double _playingDuration;

    public SoundsViewModel() : base( "Sounds", Strings.TabSounds )
    {
        _timer = new DispatcherTimer( TimeSpan.FromMilliseconds( 50 ), DispatcherPriority.Background, ( _, _ ) => Tick() );
    }

    /// <summary>Audio backend. Tests replace it so nothing is actually played.</summary>
    public ISoundPlayer Player
    {
        get;
        set
        {
            field.Stop();
            field = value;
            OnPropertyChanged();
            OnPropertyChanged( nameof( CanPlay ) );
            OnPropertyChanged( nameof( PlayToolTip ) );
            PlayCommand.NotifyCanExecuteChanged();
        }
    } = SoundPlayer.CreateDefault();

    [ObservableProperty]
    public partial IReadOnlyList<SoundRow> VisibleSounds { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor( nameof( CanPlay ) )]
    [NotifyCanExecuteChangedFor( nameof( PlayCommand ) )]
    public partial SoundRow? SelectedSound { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool SortByName { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor( nameof( StopCommand ) )]
    public partial bool IsPlaying { get; private set; }

    /// <summary>Playback position, 0..1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string PlaybackStatus { get; private set; } = SoundsStrings.NotPlaying;

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public bool CanPlay => Player.IsAvailable && SelectedSound is not null;

    /// <summary>Explains a disabled Play button.</summary>
    public string? PlayToolTip => Player.IsAvailable ? null : SoundsStrings.PlaybackUnavailable;

    public override IReadOnlyList<string> Actions => ["play", "stop", "sort_by_name"];

    protected override async Task LoadAsync( UOData data )
    {
        var rows = await Task.Run( () => data.Sounds.GetAll().Select( s => new SoundRow( s ) ).ToList() );

        _all = rows;
        ApplyFilter();

        if ( SelectedSound is null && VisibleSounds.Count > 0 )
        {
            SelectedSound = VisibleSounds[0];
        }
    }

    protected override void Reset()
    {
        StopPlayback();
        _all = [];
        VisibleSounds = [];
        SelectedSound = null;
        Details.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSortByNameChanged( bool value ) => ApplyFilter();

    partial void OnSelectedSoundChanged( SoundRow? value ) => UpdateDetails();

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var hex = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;
        IEnumerable<SoundRow> rows = _all;

        if ( text.Length > 0 )
        {
            rows = rows.Where( r => r.Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ||
                                    ( hex.Length > 0 && r.Id.ToString( "X4", CultureInfo.InvariantCulture ).Contains( hex, StringComparison.OrdinalIgnoreCase ) ) );
        }

        if ( SortByName )
        {
            rows = rows.OrderBy( r => r.Name, StringComparer.OrdinalIgnoreCase ).ThenBy( r => r.Id );
        }

        var selected = SelectedSound;
        VisibleSounds = rows.ToList();

        // Keep the selection if it survived the filter (the ListBox drops it when its items change).
        SelectedSound = selected is not null && VisibleSounds.Contains( selected ) ? selected : null;
        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleSounds.Count, _all.Count );
    }

    private void UpdateDetails()
    {
        Details.Clear();

        if ( SelectedSound is not { Info: var info } || Data is null )
        {
            return;
        }

        Details.Add( new PropertyRow( Strings.Id, $"0x{info.Id:X3} ({info.Id})" ) );
        Details.Add( new PropertyRow( Strings.Name, info.Name ) );
        Details.Add( new PropertyRow( SoundsStrings.Duration, string.Format( CultureInfo.CurrentCulture, SoundsStrings.DurationFormat, info.Duration ) ) );
        Details.Add( new PropertyRow( SoundsStrings.SampleData, string.Format( CultureInfo.CurrentCulture, SoundsStrings.BytesFormat, info.SampleBytes ) ) );
        Details.Add( new PropertyRow( SoundsStrings.Format, SoundsStrings.FormatValue ) );
        Details.Add( new PropertyRow( SoundsStrings.Source, Data.Sounds.Source ) );

        if ( info.IsRemapped )
        {
            Details.Add( new PropertyRow( SoundsStrings.RemappedFrom, string.Format( CultureInfo.CurrentCulture, SoundsStrings.RemapFormat, info.SourceId ) ) );
        }
    }

    [RelayCommand( CanExecute = nameof( CanPlay ) )]
    private void Play()
    {
        if ( SelectedSound is not { Info: var info } || Data?.Sounds.GetWav( info.Id ) is not { } wav )
        {
            return;
        }

        Player.Play( wav );
        _playingDuration = info.Duration;
        _clock.Restart();
        Progress = 0;
        IsPlaying = true;
        PlaybackStatus = string.Format( CultureInfo.CurrentCulture, SoundsStrings.Playing, info.Name );
        _timer.Start();
    }

    [RelayCommand( CanExecute = nameof( IsPlaying ) )]
    private void Stop() => StopPlayback();

    private void StopPlayback()
    {
        _timer.Stop();
        _clock.Reset();
        Player.Stop();
        IsPlaying = false;
        Progress = 0;
        PlaybackStatus = SoundsStrings.NotPlaying;
    }

    private void Tick()
    {
        if ( _playingDuration <= 0 || _clock.Elapsed.TotalSeconds >= _playingDuration )
        {
            _timer.Stop();
            IsPlaying = false;
            Progress = 0;
            PlaybackStatus = SoundsStrings.NotPlaying;
            return;
        }

        Progress = _clock.Elapsed.TotalSeconds / _playingDuration;
    }

    public override bool SelectEntry( int id )
    {
        var row = _all.FirstOrDefault( r => r.Id == id );

        if ( row is null )
        {
            return false;
        }

        if ( !VisibleSounds.Contains( row ) )
        {
            SearchText = "";
        }

        SelectedSound = row;
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleSounds.Count;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "play":
                if ( args["id"] is { } id && !SelectEntry( id.GetValue<int>() ) )
                {
                    throw new ArgumentException( $"No sound with id {id}." );
                }

                if ( !Player.IsAvailable )
                {
                    throw new InvalidOperationException( SoundsStrings.PlaybackUnavailable );
                }

                Play();
                return GetState();
            case "stop":
                StopPlayback();
                return GetState();
            case "sort_by_name":
                SortByName = args["enabled"]?.GetValue<bool>() ?? !SortByName;
                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleSounds.Count;
        state["search"] = SearchText;
        state["sortByName"] = SortByName;
        state["selectedId"] = SelectedSound?.Id ?? -1;
        state["selectedName"] = SelectedSound?.Name;
        state["selectedDuration"] = SelectedSound is { } s ? Math.Round( s.Info.Duration, 3 ) : null;
        state["firstVisibleId"] = VisibleSounds.Count > 0 ? VisibleSounds[0].Id : null;
        state["playbackAvailable"] = Player.IsAvailable;
        state["playing"] = IsPlaying;
        return state;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>Patches for one kind of file (e.g. art = file ids 3 and 4).</summary>
public sealed record VerdataTypeGroup( string Name, int[] FileIds, IReadOnlyList<VerdataPatchRow> Patches )
{
    public string Text => string.Format( CultureInfo.CurrentCulture, VerdataStrings.GroupItemFormat, Name, Patches.Count );
}

public sealed record VerdataPatchRow( VerdataPatch Patch )
{
    public string Text => string.Format( CultureInfo.CurrentCulture, VerdataStrings.PatchItemFormat, Patch.Index, Patch.File );
}

/// <summary>
///     Verdata tab (UOFiddler's VerdataControl, read-only): patch types, the patches of the
///     selected type and the selected patch's record plus a hex preview of its data. Clients
///     without verdata.mul get an explanatory message instead.
/// </summary>
public sealed partial class VerdataViewModel : TabViewModel
{
    private const int DumpBytes = 256;

    /// <summary>UOFiddler's patch type grouping (file ids from <see cref="Verdata.GetFileName" />).</summary>
    private static readonly (Func<string> Name, int[] FileIds)[] TypeGroups =
    [
        ( () => VerdataStrings.GroupArt, [3, 4] ),
        ( () => VerdataStrings.GroupGumps, [11, 12] ),
        ( () => VerdataStrings.GroupTextures, [9, 10] ),
        ( () => VerdataStrings.GroupTileData, [30] ),
        ( () => VerdataStrings.GroupAnimData, [31] ),
        ( () => VerdataStrings.GroupMap, [0] ),
        ( () => VerdataStrings.GroupStatics, [1, 2] ),
        ( () => VerdataStrings.GroupAnimations, [5, 6] ),
        ( () => VerdataStrings.GroupSounds, [7, 8] ),
        ( () => VerdataStrings.GroupMulti, [13, 14] ),
        ( () => VerdataStrings.GroupSkills, [15, 16] )
    ];

    public VerdataViewModel() : base( "Verdata", Strings.TabVerdata )
    {
    }

    /// <summary>False when the client has no verdata.mul; the view then shows <see cref="Message" />.</summary>
    [ObservableProperty]
    public partial bool IsPresent { get; private set; }

    /// <summary>"Not present", "no patches" or "truncated" notice; null when there is nothing to say.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<VerdataTypeGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial VerdataTypeGroup? SelectedGroup { get; set; }

    /// <summary>Patches of <see cref="SelectedGroup" />.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<VerdataPatchRow> VisiblePatches { get; private set; } = [];

    [ObservableProperty]
    public partial VerdataPatchRow? SelectedPatch { get; set; }

    [ObservableProperty]
    public partial string PatchCount { get; private set; } = "";

    [ObservableProperty]
    public partial string HexDump { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public override IReadOnlyList<string> Actions => ["select_type"];

    protected override async Task LoadAsync( UOData data )
    {
        var verdata = await Task.Run( () => data.Verdata );

        IsPresent = verdata.IsPresent;
        Groups = BuildGroups( verdata.Patches );
        Message = !verdata.IsPresent ? VerdataStrings.NotPresent
            : verdata.IsTruncated ? string.Format( CultureInfo.CurrentCulture, VerdataStrings.Truncated, verdata.DeclaredCount, verdata.Patches.Count )
            : verdata.Patches.Count == 0 ? VerdataStrings.NoPatches
            : null;
        Summary = verdata.Path ?? "";
        SelectedGroup = Groups.FirstOrDefault();
    }

    protected override void Reset()
    {
        IsPresent = false;
        Message = null;
        Groups = [];
        SelectedGroup = null;
        Summary = "";
    }

    private static List<VerdataTypeGroup> BuildGroups( IReadOnlyList<VerdataPatch> patches )
    {
        var groups = new List<VerdataTypeGroup>();

        List<VerdataPatchRow> Rows( Func<VerdataPatch, bool> match ) =>
            patches.Where( match ).OrderBy( p => p.Index ).Select( p => new VerdataPatchRow( p ) ).ToList();

        foreach ( var (name, ids) in TypeGroups )
        {
            var rows = Rows( p => ids.Contains( p.File ) );

            if ( rows.Count > 0 )
            {
                groups.Add( new VerdataTypeGroup( name(), ids, rows ) );
            }
        }

        var known = TypeGroups.SelectMany( g => g.FileIds ).ToHashSet();

        foreach ( var file in patches.Select( p => p.File ).Where( f => !known.Contains( f ) ).Distinct().Order() )
        {
            groups.Add( new VerdataTypeGroup( string.Format( CultureInfo.CurrentCulture, VerdataStrings.GroupUnknown, file ), [file], Rows( p => p.File == file ) ) );
        }

        return groups;
    }

    partial void OnSelectedGroupChanged( VerdataTypeGroup? value )
    {
        // Swap the list before selecting so the ListBox does not clear the new selection.
        VisiblePatches = value?.Patches ?? [];
        SelectedPatch = VisiblePatches.FirstOrDefault();
        PatchCount = value is null ? "" : string.Format( CultureInfo.CurrentCulture, VerdataStrings.PatchCount, value.Patches.Count );
    }

    partial void OnSelectedPatchChanged( VerdataPatchRow? value ) => UpdateDetails();

    private void UpdateDetails()
    {
        Details.Clear();
        HexDump = "";

        if ( SelectedPatch is not { Patch: var p } || Data is null )
        {
            return;
        }

        Details.Add( new PropertyRow( VerdataStrings.File, string.Format( CultureInfo.CurrentCulture, VerdataStrings.FileValueFormat, p.File, p.FileName ) ) );
        Details.Add( new PropertyRow( VerdataStrings.Index, $"0x{p.Index:X4}  ({p.Index})" ) );
        Details.Add( new PropertyRow( VerdataStrings.Lookup, $"0x{p.Lookup:X}" ) );
        Details.Add( new PropertyRow( VerdataStrings.Length, p.Length.ToString( "N0", CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( VerdataStrings.Extra, $"0x{p.Extra:X}" ) );

        var bytes = Data.Verdata.GetData( p );
        HexDump = FormatHex( bytes[..Math.Min( bytes.Length, DumpBytes )] );
    }

    public static string FormatHex( ReadOnlySpan<byte> bytes )
    {
        var sb = new StringBuilder();

        for ( var row = 0; row < bytes.Length; row += 16 )
        {
            var line = bytes.Slice( row, Math.Min( 16, bytes.Length - row ) );
            sb.Append( CultureInfo.InvariantCulture, $"{row:X4}  " );

            for ( var i = 0; i < 16; i++ )
            {
                sb.Append( i < line.Length ? line[i].ToString( "X2", CultureInfo.InvariantCulture ) + " " : "   " );
            }

            sb.Append( ' ' );

            foreach ( var b in line )
            {
                sb.Append( b is >= 0x20 and < 0x7F ? ( char )b : '.' );
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Selects the patch for entry <paramref name="id" />, preferring the selected type.</summary>
    public override bool SelectEntry( int id )
    {
        var group = SelectedGroup is { } g && g.Patches.Any( r => r.Patch.Index == id ) ? g : Groups.FirstOrDefault( x => x.Patches.Any( r => r.Patch.Index == id ) );

        if ( group is null )
        {
            return false;
        }

        SelectedGroup = group;
        SelectedPatch = group.Patches.First( r => r.Patch.Index == id );
        return true;
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        if ( action == "select_type" )
        {
            var file = args["file"]?.GetValue<int>() ?? throw new ArgumentException( "select_type needs 'file' (a verdata file id, e.g. 4 for art.mul)." );
            SelectedGroup = Groups.FirstOrDefault( g => g.FileIds.Contains( file ) ) ?? throw new ArgumentException( $"No patches for file id {file}." );
            return GetState();
        }

        return base.Invoke( action, args );
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["present"] = IsPresent;
        state["message"] = Message;
        state["types"] = new JsonArray( Groups.Select( g => ( JsonNode? )g.Text ).ToArray() );
        state["patches"] = Groups.Sum( g => g.Patches.Count );
        state["selectedType"] = SelectedGroup?.Name;
        state["selectedFile"] = SelectedPatch?.Patch.File;
        state["selectedId"] = SelectedPatch?.Patch.Index ?? -1;
        state["selectedLength"] = SelectedPatch?.Patch.Length;
        return state;
    }
}

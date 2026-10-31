using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>One line of the skills table; <see cref="Group" /> comes from skillgrp.mul when present.</summary>
public sealed record SkillRow( SkillInfo Info, string? Group )
{
    public int Index => Info.Index;

    public string Name => Info.Name;

    public bool IsAction => Info.IsAction;
}

/// <summary>Skills tab (UOFiddler's SkillsControl, read-only): index, "is action" flag and name, plus details.</summary>
public sealed partial class SkillsViewModel : TabViewModel
{
    private IReadOnlyList<SkillRow> _all = [];

    public SkillsViewModel() : base( "Skills", Strings.TabSkills )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<SkillRow> VisibleSkills { get; private set; } = [];

    [ObservableProperty]
    public partial SkillRow? SelectedSkill { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    protected override async Task LoadAsync( UOData data )
    {
        _all = await Task.Run( () =>
        {
            var groups = data.TryGet( d => d.SkillGroups );
            return data.Skills.Entries.Select( s => new SkillRow( s, groups?.GetGroupOf( s.Index )?.Name ) ).ToList();
        } );

        ApplyFilter();

        if ( SelectedSkill is null && VisibleSkills.Count > 0 )
        {
            SelectedSkill = VisibleSkills[0];
        }
    }

    protected override void Reset()
    {
        _all = [];
        VisibleSkills = [];
        SelectedSkill = null;
        Details.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => ApplyFilter();

    partial void OnSelectedSkillChanged( SkillRow? value ) => UpdateDetails();

    private void ApplyFilter()
    {
        var selected = SelectedSkill;
        var text = SearchText.Trim();
        var id = Mcp.ToolArgs.ParseInt( text );

        VisibleSkills = text.Length == 0
            ? _all
            : _all.Where( s => s.Index == id || s.Name.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();

        // Keep the selection if it survived the filter (the ListBox drops it when its items change).
        SelectedSkill = selected is not null && VisibleSkills.Contains( selected ) ? selected : null;

        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, VisibleSkills.Count, _all.Count );
    }

    private void UpdateDetails()
    {
        Details.Clear();

        if ( SelectedSkill is not { } skill )
        {
            return;
        }

        Details.Add( new PropertyRow( Strings.Id, skill.Index.ToString( CultureInfo.CurrentCulture ) ) );
        Details.Add( new PropertyRow( Strings.Name, skill.Name ) );
        Details.Add( new PropertyRow( SkillsStrings.IsAction, skill.IsAction ? SkillsStrings.Yes : SkillsStrings.No ) );
        Details.Add( new PropertyRow( SkillsStrings.Extra, $"0x{skill.Info.Extra:X} ({skill.Info.Extra})" ) );

        if ( skill.Group is { } group )
        {
            Details.Add( new PropertyRow( SkillsStrings.Group, group ) );
        }
    }

    public override bool SelectEntry( int id )
    {
        if ( ( uint )id >= ( uint )_all.Count )
        {
            return false;
        }

        if ( !VisibleSkills.Contains( _all[id] ) )
        {
            SearchText = "";
        }

        SelectedSkill = _all[id];
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return VisibleSkills.Count;
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["total"] = _all.Count;
        state["visible"] = VisibleSkills.Count;
        state["search"] = SearchText;
        state["selectedId"] = SelectedSkill?.Index ?? -1;
        state["selectedName"] = SelectedSkill?.Name;
        state["selectedIsAction"] = SelectedSkill?.IsAction;
        state["selectedGroup"] = SelectedSkill?.Group;
        return state;
    }
}

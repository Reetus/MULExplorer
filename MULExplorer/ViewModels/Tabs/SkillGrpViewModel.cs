using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MULExplorer.Resources;
using UODataAccess;
using UODataAccess.Loaders;

namespace MULExplorer.ViewModels.Tabs;

/// <summary>A node of the skill group tree: a group (with its skills as children) or a skill.</summary>
public sealed partial class SkillTreeNode( string text, int groupIndex, int skillIndex, IReadOnlyList<SkillTreeNode> children ) : ObservableObject
{
    public string Text { get; } = text;

    public int GroupIndex { get; } = groupIndex;

    /// <summary>Skill index for skill nodes, -1 for groups.</summary>
    public int SkillIndex { get; } = skillIndex;

    public IReadOnlyList<SkillTreeNode> Children { get; } = children;

    public bool IsGroup => SkillIndex < 0;

    /// <summary>Child count for groups, the skill id for skills.</summary>
    public string Badge => IsGroup ? $"({Children.Count})" : $"#{SkillIndex}";

    /// <summary>The implicit group 0, highlighted like UOFiddler does.</summary>
    public bool IsMisc => IsGroup && GroupIndex == 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>SkillGrp tab (UOFiddler's SkillGroupControl, read-only): a tree of skill groups and their skills.</summary>
public sealed partial class SkillGrpViewModel : TabViewModel
{
    private SkillGroups? _groups;
    private Skills? _skills;

    public SkillGrpViewModel() : base( "SkillGrp", Strings.TabSkillGrp )
    {
    }

    [ObservableProperty]
    public partial IReadOnlyList<SkillTreeNode> Roots { get; private set; } = [];

    [ObservableProperty]
    public partial SkillTreeNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ObservableCollection<PropertyRow> Details { get; } = [];

    public override IReadOnlyList<string> Actions => ["select_group", "expand_all", "collapse_all"];

    protected override async Task LoadAsync( UOData data )
    {
        var (groups, skills) = await Task.Run( () => ( data.SkillGroups, data.TryGet( d => d.Skills ) ) );

        _groups = groups;
        _skills = skills;
        Rebuild();
    }

    protected override void Reset()
    {
        _groups = null;
        _skills = null;
        Roots = [];
        SelectedNode = null;
        Details.Clear();
        Summary = "";
    }

    partial void OnSearchTextChanged( string value ) => Rebuild();

    partial void OnSelectedNodeChanged( SkillTreeNode? value ) => UpdateDetails();

    private string SkillName( int index ) => _skills?.Get( index )?.Name ?? $"#{index}";

    private void Rebuild()
    {
        var selected = SelectedNode;
        SelectedNode = null;

        if ( _groups is null )
        {
            Roots = [];
            return;
        }

        var text = SearchText.Trim();
        var filtering = text.Length > 0;
        var roots = new List<SkillTreeNode>();
        var skillCount = 0;

        foreach ( var group in _groups.Groups )
        {
            var groupMatches = filtering && group.Name.Contains( text, StringComparison.OrdinalIgnoreCase );
            var children = group.Skills
                .Where( s => !filtering || groupMatches || SkillName( s ).Contains( text, StringComparison.OrdinalIgnoreCase ) )
                .Select( s => new SkillTreeNode( SkillName( s ), group.Index, s, [] ) )
                .ToList();

            if ( filtering && !groupMatches && children.Count == 0 )
            {
                continue;
            }

            skillCount += children.Count;
            roots.Add( new SkillTreeNode( group.Name, group.Index, -1, children ) { IsExpanded = filtering } );
        }

        Roots = roots;
        Summary = string.Format( CultureInfo.CurrentCulture, Strings.EntriesShown, skillCount, _groups.SkillGroupIndices.Count );

        if ( selected is not null && Find( selected.GroupIndex, selected.SkillIndex ) is { } again )
        {
            Select( again );
        }
    }

    private SkillTreeNode? Find( int group, int skill )
    {
        var root = Roots.FirstOrDefault( r => r.GroupIndex == group );
        return skill < 0 ? root : root?.Children.FirstOrDefault( c => c.SkillIndex == skill );
    }

    private void Select( SkillTreeNode node )
    {
        if ( !node.IsGroup )
        {
            Find( node.GroupIndex, -1 )!.IsExpanded = true;
        }

        SelectedNode = node;
    }

    private void UpdateDetails()
    {
        Details.Clear();

        if ( SelectedNode is not { } node || _groups is null )
        {
            return;
        }

        var group = _groups.Groups[node.GroupIndex];

        if ( node.IsGroup )
        {
            Details.Add( new PropertyRow( SkillGrpStrings.Group, group.Name ) );
            Details.Add( new PropertyRow( Strings.Id, group.Index.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( SkillGrpStrings.Skills, string.Format( CultureInfo.CurrentCulture, SkillGrpStrings.SkillCountFormat, group.Skills.Count ) ) );
        }
        else
        {
            Details.Add( new PropertyRow( SkillGrpStrings.Skill, SkillName( node.SkillIndex ) ) );
            Details.Add( new PropertyRow( Strings.Id, node.SkillIndex.ToString( CultureInfo.CurrentCulture ) ) );
            Details.Add( new PropertyRow( SkillGrpStrings.Group, $"{group.Name} ({group.Index})" ) );
        }

        Details.Add( new PropertyRow( SkillGrpStrings.Format, _groups.IsUnicode ? SkillGrpStrings.FormatUnicode : SkillGrpStrings.FormatAscii ) );
    }

    /// <summary>Selects skill <paramref name="id" /> in its group, expanding the group.</summary>
    public override bool SelectEntry( int id )
    {
        if ( _groups?.GetGroupOf( id ) is not { } group )
        {
            return false;
        }

        if ( Find( group.Index, id ) is null )
        {
            SearchText = "";
        }

        Select( Find( group.Index, id )! );
        return true;
    }

    public override int Search( string text )
    {
        SearchText = text;
        return Roots.Sum( r => r.Children.Count );
    }

    public override JsonObject Invoke( string action, JsonObject args )
    {
        switch ( action )
        {
            case "select_group":
                var index = args["index"]?.GetValue<int>() ?? throw new ArgumentException( "select_group needs 'index'." );

                if ( Find( index, -1 ) is null )
                {
                    SearchText = "";
                }

                Select( Find( index, -1 ) ?? throw new ArgumentException( $"No skill group {index}." ) );
                return GetState();
            case "expand_all":
            case "collapse_all":
                foreach ( var root in Roots )
                {
                    root.IsExpanded = action == "expand_all";
                }

                return GetState();
            default:
                return base.Invoke( action, args );
        }
    }

    public override JsonObject GetState()
    {
        var state = base.GetState();
        state["groups"] = _groups?.Groups.Count ?? 0;
        state["visibleGroups"] = Roots.Count;
        state["visibleSkills"] = Roots.Sum( r => r.Children.Count );
        state["unicode"] = _groups?.IsUnicode;
        state["search"] = SearchText;
        state["expanded"] = new JsonArray( Roots.Where( r => r.IsExpanded ).Select( r => ( JsonNode? )r.Text ).ToArray() );
        state["selectedGroup"] = SelectedNode is { } n ? _groups?.Groups[n.GroupIndex].Name : null;
        state["selectedSkillId"] = SelectedNode?.SkillIndex ?? -1;
        state["selectedText"] = SelectedNode?.Text;
        return state;
    }
}

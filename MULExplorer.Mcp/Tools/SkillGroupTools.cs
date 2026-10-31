using System.Text.Json.Nodes;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>SkillGrp tab: skillgrp.mul groups.</summary>
public sealed class SkillGroupTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_skill_groups",
            "Skill groups from skillgrp.mul (group 0 is the implicit 'Misc') with the index and name of each member skill.",
            null,
            null,
            _ =>
            {
                var data = context.RequireData();
                var groups = data.SkillGroups;
                var skills = data.TryGet( d => d.Skills );

                return Json( new JsonObject
                {
                    ["format"] = groups.IsUnicode ? "unicode" : "ascii",
                    ["skill_count"] = groups.SkillGroupIndices.Count,
                    ["groups"] = new JsonArray( groups.Groups.Select( g => ( JsonNode? )new JsonObject
                    {
                        ["index"] = g.Index,
                        ["name"] = g.Name,
                        ["skills"] = new JsonArray( g.Skills.Select( s => ( JsonNode? )new JsonObject
                        {
                            ["index"] = s,
                            ["name"] = skills?.Get( s )?.Name
                        } ).ToArray() )
                    } ).ToArray() )
                } );
            } );
    }
}

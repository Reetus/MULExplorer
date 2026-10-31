using System.Text.Json.Nodes;
using static MULExplorer.Mcp.ToolRegistry;

namespace MULExplorer.Mcp.Tools;

/// <summary>Skills tab: skills.mul names and action flags.</summary>
public sealed class SkillsTools : IToolProvider
{
    public void Register( ToolRegistry registry )
    {
        var context = registry.Context;

        registry.Add(
            "list_skills",
            "All skills: index (the skill id used by the protocol), name, whether it has an action (use) button, and its skill group when skillgrp.mul exists.",
            Props( ( "name", Str( "Optional case-insensitive name substring." ) ) ),
            null,
            args =>
            {
                var data = context.RequireData();
                var groups = data.TryGet( d => d.SkillGroups );
                var name = args.GetString( "name" );

                return Json( new JsonArray( data.Skills.Entries
                    .Where( s => name is null || s.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) )
                    .Select( s => ( JsonNode? )new JsonObject
                    {
                        ["index"] = s.Index,
                        ["name"] = s.Name,
                        ["is_action"] = s.IsAction,
                        ["extra"] = s.Extra,
                        ["group"] = groups?.GetGroupOf( s.Index )?.Name
                    } ).ToArray() ) );
            } );
    }
}

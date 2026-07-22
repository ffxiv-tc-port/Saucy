using ECommons.LanguageHelpers;

namespace Saucy.TripleTriad;

internal static class TriadDependenciesUi
{
    private static readonly PluginDependenciesUi.DependencyEntry[] Dependencies =
    [
        PluginDependenciesUi.Vnavmesh(
            "Walk to Triple Triad NPCs from Saucy map links after you arrive in the zone.".Loc()),
        PluginDependenciesUi.LifestreamPlugin(
            "Teleport to the nearest aetheryte before pathing when the NPC is far away or in another zone.".Loc()),
        PluginDependenciesUi.QuestionablePlugin(
            "Start Triple Triad unlock quests from Saucy card and NPC search.".Loc())
    ];

    public static void Draw() =>
        PluginDependenciesUi.Draw(
            "Optional plugins for pathing to NPCs on the map, teleporting when needed, and starting unlock quests from Saucy.".Loc(),
            Dependencies);
}

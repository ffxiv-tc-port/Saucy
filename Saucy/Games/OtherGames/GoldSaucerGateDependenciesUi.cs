namespace Saucy.OtherGames;

internal static class GoldSaucerGateDependenciesUi
{
    public static void DrawWindBlows() =>
        PluginDependenciesUi.Draw(
            "Optional plugin for automatic movement to the safe spot. Overlays still work without it.",
            [
                PluginDependenciesUi.Vnavmesh(
                    "Pathfinds you onto the statistical safe spot during the GATE.")
            ]);
}

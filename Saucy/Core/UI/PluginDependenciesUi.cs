using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
using ECommons.Reflection;
using Saucy.IPC;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
namespace Saucy;

internal static class PluginDependenciesUi
{
    // 這裡絕對不能指國際服的外掛庫：那些庫裡的 vnavmesh、Lifestream、Questionable
    // 內部名與台服版完全相同，按下去會把 API15 的版本裝進台服環境並撞同一個已安裝鍵。
    // 一律指本艦隊的 feed。
    public const string TcRepositoryUrl = "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";

    public static DependencyEntry Vnavmesh(string description) =>
        new(
            "vnavmesh",
            "vnavmesh",
            description,
            TcRepositoryUrl,
            [],
            () => IPC.Vnavmesh.IsInstalled);

    // IPCNames.BossMod 是 EzIPC 前綴（BMR 沿用舊名），不是內部名，不能拿來查已安裝清單。
    public static DependencyEntry BossModPlugin(string description) =>
        new(
            "Bossmod Reborn",
            "BossModReborn",
            description,
            TcRepositoryUrl,
            [],
            () => BossMod.IsInstalled);

    public static DependencyEntry LifestreamPlugin(string description) =>
        new(
            "Lifestream",
            "Lifestream",
            description,
            TcRepositoryUrl,
            [],
            () => Lifestream.IsInstalled);

    public static DependencyEntry QuestionablePlugin(string description) =>
        new(
            "Questionable",
            "Questionable",
            description,
            TcRepositoryUrl,
            [],
            () => Questionable.IsInstalled);

    public static void Draw(string intro, ReadOnlySpan<DependencyEntry> dependencies)
    {
        ImGui.TextWrapped(intro);
        ImGui.Dummy(new(0, 4));

        foreach (var entry in dependencies)
        {
            DrawDependency(entry);
        }
    }

    private static void DrawDependency(DependencyEntry entry)
    {
        using var id = ImRaii.PushId(entry.InternalName);
        var state = GetState(entry.InternalName, entry.IsInstalled);

        ImGui.TextColored(SaucyTheme.ColorOr(SaucyTheme.SectionTitle, ImGuiCol.Text), entry.DisplayName);
        ImGui.TextWrapped(entry.Description);

        ImGui.Spacing();
        DrawStatus(state);

        if (state == DependencyState.Ready)
        {
            return;
        }

        var repoAdded = IsRepositoryAdded(entry);
        var showAddRepo = !repoAdded && state == DependencyState.NotInstalled;
        var showInstall = state == DependencyState.NotInstalled;

        if (!showAddRepo && !showInstall)
        {
            return;
        }

        ImGui.Spacing();
        var firstButton = true;

        if (showAddRepo)
        {
            if (ImGui.Button("Add repository".Loc()))
            {
                TryAddRepository(entry);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Add ?? to Custom Plugin Repositories.".Loc(entry.PrimaryRepositoryUrl));
            }

            firstButton = false;
        }

        if (showInstall)
        {
            if (!firstButton)
            {
                ImGui.SameLine();
            }

            if (ImGui.Button("Install plugin".Loc()))
            {
                TryInstallPlugin(entry);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Install ?? from its plugin repository.".Loc(entry.DisplayName));
            }
        }

        ImGui.Dummy(new(0, 6));
    }

    private static bool IsRepositoryAdded(DependencyEntry entry)
    {
        foreach (var url in entry.RepositoryUrls)
        {
            if (DalamudReflector.HasRepo(url))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryAddRepository(DependencyEntry entry)
    {
        if (DalamudReflector.HasRepo(entry.PrimaryRepositoryUrl))
        {
            Svc.Chat.Print("[Saucy] " + "?? repository is already added.".Loc(entry.DisplayName));
            return;
        }

        DalamudReflector.AddRepo(entry.PrimaryRepositoryUrl, true);
        DalamudReflector.SaveDalamudConfig();
        DalamudReflector.ReloadPluginMasters();
        Svc.Chat.Print("[Saucy] " + "Added ?? repository.".Loc(entry.DisplayName));
    }

    private static void TryInstallPlugin(DependencyEntry entry)
    {
        var repoUrl = entry.RepositoryUrls.FirstOrDefault(DalamudReflector.HasRepo) ?? entry.PrimaryRepositoryUrl;
        _ = InstallPluginAsync(entry, repoUrl);
    }

    private static async Task InstallPluginAsync(DependencyEntry entry, string repoUrl)
    {
        if (await DalamudReflector.AddPlugin(repoUrl, entry.InternalName))
        {
            Svc.Chat.Print("[Saucy] " + "Installed ??.".Loc(entry.DisplayName));
        }
        else
        {
            Svc.Chat.PrintError("[Saucy] " + "Could not install ??. Check the plugin installer for details.".Loc(entry.DisplayName));
        }
    }

    private static void DrawStatus(DependencyState state)
    {
        switch (state)
        {
            case DependencyState.Ready:
                DrawStatusLine(FontAwesomeIcon.Check, ImGuiColors.HealerGreen, "Installed".Loc());
                break;
            case DependencyState.InstalledNotLoaded:
                DrawStatusLine(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Installed but not loaded".Loc());
                ImGui.SameLine();
                if (ImGui.Button("Open installer".Loc()))
                {
                    Svc.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, string.Empty);
                }
                break;
            default:
                DrawStatusLine(FontAwesomeIcon.Times, ImGuiColors.DalamudRed, "Not installed".Loc());
                break;
        }
    }

    private static void DrawStatusLine(FontAwesomeIcon icon, Vector4 color, string text) =>
        ImGuiLayout.DrawStatusIconText(icon, color, text);

    private static DependencyState GetState(string internalName, Func<bool> isReady)
    {
        if (isReady())
        {
            return DependencyState.Ready;
        }

        var plugin = Svc.PluginInterface.InstalledPlugins.FirstOrDefault(x => x.InternalName == internalName);
        if (plugin != null)
        {
            return DependencyState.InstalledNotLoaded;
        }

        return DependencyState.NotInstalled;
    }

    private enum DependencyState
    {
        NotInstalled,
        InstalledNotLoaded,
        Ready
    }

    internal sealed record DependencyEntry
    (
        string DisplayName,
        string InternalName,
        string Description,
        string PrimaryRepositoryUrl,
        string[] AlternateRepositoryUrls,
        Func<bool> IsInstalled)
    {
        public string[] RepositoryUrls
            => AlternateRepositoryUrls.Length == 0
                ? [PrimaryRepositoryUrl]
                : [PrimaryRepositoryUrl, .. AlternateRepositoryUrls];
    }
}

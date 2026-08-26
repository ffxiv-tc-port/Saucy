using Dalamud.Bindings.ImGui;
namespace Saucy;

public static unsafe class ImGuiExtensions
{
    public static bool PassFilterBool(this ImGuiTextFilterPtr self, string text)
    {
        return self.PassFilter(text);
    }
}

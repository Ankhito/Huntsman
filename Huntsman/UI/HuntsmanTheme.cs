using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Huntsman.UI;

internal static class HuntsmanTheme
{
    public static readonly Vector4 Gold = new(1.00f, 0.76f, 0.24f, 1f);
    public static readonly Vector4 GoldSoft = new(0.85f, 0.58f, 0.18f, 1f);
    public static readonly Vector4 Black = new(0.055f, 0.050f, 0.045f, 0.96f);
    public static readonly Vector4 Panel = new(0.095f, 0.085f, 0.070f, 1f);
    public static readonly Vector4 PanelSoft = new(0.130f, 0.115f, 0.090f, 1f);
    public static readonly Vector4 BorderGold = new(1.00f, 0.76f, 0.24f, 0.28f);
    public static readonly Vector4 Text = new(0.95f, 0.93f, 0.88f, 1f);
    public static readonly Vector4 Dimmed = new(0.62f, 0.58f, 0.50f, 1f);
    public static readonly Vector4 Green = new(0.36f, 0.82f, 0.45f, 1f);
    public static readonly Vector4 Red = new(0.96f, 0.42f, 0.42f, 1f);
    public static readonly Vector4 Warn = Gold;

    private const int ThemeColors = 29;
    private const int ThemeVars = 9;

    public static void Push()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));

        Color(ImGuiCol.Text, Text);
        Color(ImGuiCol.TextDisabled, Dimmed);
        Color(ImGuiCol.WindowBg, Black);
        Color(ImGuiCol.ChildBg, Panel);
        Color(ImGuiCol.PopupBg, new Vector4(0.07f, 0.06f, 0.05f, 0.98f));
        Color(ImGuiCol.Border, BorderGold);
        Color(ImGuiCol.FrameBg, PanelSoft);
        Color(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.17f, 0.12f, 1f));
        Color(ImGuiCol.FrameBgActive, new Vector4(0.26f, 0.21f, 0.13f, 1f));
        Color(ImGuiCol.TitleBg, new Vector4(0.07f, 0.06f, 0.05f, 1f));
        Color(ImGuiCol.TitleBgActive, new Vector4(0.18f, 0.13f, 0.07f, 1f));
        Color(ImGuiCol.TitleBgCollapsed, new Vector4(0.07f, 0.06f, 0.05f, 0.75f));
        Color(ImGuiCol.Button, PanelSoft);
        Color(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.22f, 0.12f, 1f));
        Color(ImGuiCol.ButtonActive, new Vector4(0.42f, 0.30f, 0.12f, 1f));
        Color(ImGuiCol.Header, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.15f));
        Color(ImGuiCol.HeaderHovered, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.28f));
        Color(ImGuiCol.HeaderActive, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.38f));
        Color(ImGuiCol.CheckMark, Gold);
        Color(ImGuiCol.SliderGrab, GoldSoft);
        Color(ImGuiCol.SliderGrabActive, Gold);
        Color(ImGuiCol.Separator, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.24f));
        Color(ImGuiCol.SeparatorHovered, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.70f));
        Color(ImGuiCol.Tab, Panel);
        Color(ImGuiCol.TabHovered, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.26f));
        Color(ImGuiCol.TabActive, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.20f));
        Color(ImGuiCol.ScrollbarBg, new Vector4(0.06f, 0.05f, 0.045f, 0.65f));
        Color(ImGuiCol.ScrollbarGrab, new Vector4(0.24f, 0.20f, 0.14f, 1f));
        Color(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.36f, 0.27f, 0.13f, 1f));
    }

    public static void Pop()
    {
        ImGui.PopStyleColor(ThemeColors);
        ImGui.PopStyleVar(ThemeVars);
    }

    private static void Color(ImGuiCol idx, Vector4 color) => ImGui.PushStyleColor(idx, color);
}

using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Huntsman.UI;

internal static class HuntsmanWidgets
{
    public static CardScope Card(string id, Vector2 size, Vector4 accent)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, HuntsmanTheme.Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, 0.42f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.BeginChild(id, size, true);
        return new CardScope();
    }

    public static void Section(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(HuntsmanTheme.Gold, label.ToUpperInvariant());
        ImGui.Separator();
    }

    public static void KeyValue(string label, string value)
    {
        ImGui.TextColored(HuntsmanTheme.Dimmed, label);
        ImGui.SameLine(190f);
        ImGui.PushStyleColor(ImGuiCol.Text, HuntsmanTheme.Text);
        ImGui.TextWrapped(value);
        ImGui.PopStyleColor();
    }

    public static void Pill(string label, Vector4 color)
    {
        var pad = new Vector2(8f, 3f);
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(label) + pad * 2f;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.16f)), 999f);
        draw.AddRect(pos, pos + size, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.56f)), 999f);
        ImGui.SetCursorScreenPos(pos + pad);
        ImGui.TextColored(color, label);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + size.X, pos.Y));
        ImGui.Dummy(size);
    }

    public static bool GoldButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, 0.26f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, 0.42f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, 0.62f));
        var clicked = size == default ? ImGui.Button(label) : ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    public static bool Toggle(string id, ref bool value)
    {
        var draw = ImGui.GetWindowDrawList();
        var frame = ImGui.GetFrameHeight();
        var height = frame * 0.82f;
        var width = height * 1.85f;
        var radius = height * 0.5f;
        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(width, frame));
        var changed = false;
        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        var hovered = ImGui.IsItemHovered();
        var yOffset = (frame - height) * 0.5f;
        var p0 = new Vector2(origin.X, origin.Y + yOffset);
        var p1 = new Vector2(origin.X + width, origin.Y + yOffset + height);
        var bg = value
            ? new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, hovered ? 0.96f : 0.84f)
            : new Vector4(0.24f, 0.22f, 0.18f, hovered ? 1f : 0.9f);

        draw.AddRectFilled(p0, p1, ImGui.GetColorU32(bg), radius);
        draw.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, value ? 0.50f : 0.20f)), radius);
        var cx = value ? p1.X - radius : p0.X + radius;
        draw.AddCircleFilled(new Vector2(cx, p0.Y + radius), radius - 2f, ImGui.GetColorU32(HuntsmanTheme.Text), 24);
        return changed;
    }

    public static bool NavItem(string label, bool selected)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight() + 6f);
        var clicked = ImGui.Selectable($"##nav_{label}", selected, ImGuiSelectableFlags.None, size);
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        var bg = selected
            ? new Vector4(HuntsmanTheme.Gold.X, HuntsmanTheme.Gold.Y, HuntsmanTheme.Gold.Z, 0.16f)
            : hovered ? new Vector4(1f, 1f, 1f, 0.045f) : Vector4.Zero;

        if (bg.W > 0f)
            draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), 7f);
        if (selected)
            draw.AddRectFilled(pos, new Vector2(pos.X + 3f, pos.Y + size.Y), ImGui.GetColorU32(HuntsmanTheme.Gold), 2f);

        var textPos = new Vector2(pos.X + 13f, pos.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f);
        draw.AddText(textPos, ImGui.GetColorU32(selected ? HuntsmanTheme.Text : HuntsmanTheme.Dimmed), label);
        return clicked;
    }

    public readonly ref struct CardScope
    {
        public void Dispose()
        {
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }
    }
}

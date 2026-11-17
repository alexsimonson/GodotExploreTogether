using System;
using System.Linq;
using ExploreTogether.Core.Items;
using Godot;
using GDict = Godot.Collections.Dictionary;

namespace ExploreTogether.UI;

/// <summary>
/// The hotbar along the bottom of the screen: six keys (1 to 6), each showing the item it equips. The one in hand
/// is outlined; an item no longer carried is dimmed.
///
/// Setting a key: drag an item from the inventory onto it, or hover an inventory (or equipment) slot and press the
/// number. Right-click a key to clear it. The server keeps the hotbar (per character); this only shows it.
/// </summary>
public partial class HotbarBar : CanvasLayer
{
    /// <summary>key, container, slot: put the item in that slot on the key.</summary>
    public event Action<int, ItemContainer, int>? BindRequested;
    /// <summary>key: clear it.</summary>
    public event Action<int>? ClearRequested;

    private const float KeySize = 64;
    private readonly KeySlot[] _keys = new KeySlot[Hotbar.Size];

    public override void _Ready()
    {
        Layer = 2; // above the inventory screen, so items can be dropped onto it while that's open
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        AddChild(row);
        for (var i = 0; i < Hotbar.Size; i++)
        {
            var key = new KeySlot(i);
            var index = i;
            key.Dropped += (c, s) => BindRequested?.Invoke(index, c, s);
            key.Cleared += () => ClearRequested?.Invoke(index);
            row.AddChild(key);
            _keys[i] = key;
        }
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        row.GrowHorizontal = Control.GrowDirection.Both;
        row.GrowVertical = Control.GrowDirection.Begin;
        row.OffsetBottom = -12;
    }

    /// <summary>Redraws from the server's hotbar and what the player carries and holds.</summary>
    public void Show(int[] ids, int[] inventoryQuads, int[] gear)
    {
        for (var i = 0; i < Hotbar.Size; i++)
        {
            var id = i < ids.Length && ids[i] >= 0 ? ids[i] : (int?)null;
            var equipped = id is { } e && gear.Contains(e);
            var carried = equipped || id is { } c && Enumerable.Range(0, inventoryQuads.Length / 4).Any(q => inventoryQuads[q * 4 + 1] == c);
            _keys[i].Set(id, equipped, carried);
        }
    }

    private sealed partial class KeySlot : PanelContainer
    {
        public event Action<ItemContainer, int>? Dropped;
        public event Action? Cleared;

        private readonly TextureRect _icon;
        private readonly Label _name;
        private readonly StyleBoxFlat _style;

        public KeySlot(int index)
        {
            CustomMinimumSize = new Vector2(KeySize, KeySize);
            MouseFilter = MouseFilterEnum.Stop;
            _style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color(1, 1, 1, 0.25f) };
            _style.SetBorderWidthAll(2);
            _style.SetCornerRadiusAll(4);
            AddThemeStyleboxOverride("panel", _style);

            var stack = new Control { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(stack);
            _icon = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = TextureFilterEnum.Nearest,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _icon.OffsetTop = 8; _icon.OffsetBottom = -8; _icon.OffsetLeft = 8; _icon.OffsetRight = -8;
            stack.AddChild(_icon);
            _name = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore,
            };
            _name.AddThemeFontSizeOverride("font_size", 11);
            _name.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            stack.AddChild(_name);
            var number = new Label { Text = (index + 1).ToString(), MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(3, 0) };
            number.AddThemeFontSizeOverride("font_size", 12);
            number.AddThemeColorOverride("font_outline_color", Colors.Black);
            number.AddThemeConstantOverride("outline_size", 4);
            stack.AddChild(number);
        }

        public void Set(int? itemId, bool equipped, bool carried)
        {
            var def = itemId is { } id ? ItemCatalog.Get(id) : null;
            _icon.Texture = def == null ? null : IconCache.Get(def.Icon);
            _name.Text = def != null && _icon.Texture == null ? def.Name : ""; // no icon: the name instead
            TooltipText = def?.Name ?? "Drag an item here, or hover it in the inventory and press this number";
            Modulate = new Color(1, 1, 1, def == null || carried ? 1 : 0.4f);
            _style.BorderColor = equipped ? new Color(1, 0.85f, 0.3f) : new Color(1, 1, 1, 0.25f);
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) =>
            data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary() is var d && d.ContainsKey("slot")
            && (ItemContainer)d["container"].AsInt32() is ItemContainer.Inventory or ItemContainer.Gear;

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            var d = data.AsGodotDictionary();
            Dropped?.Invoke((ItemContainer)d["container"].AsInt32(), d["slot"].AsInt32());
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                Cleared?.Invoke();
                AcceptEvent();
            }
        }
    }
}

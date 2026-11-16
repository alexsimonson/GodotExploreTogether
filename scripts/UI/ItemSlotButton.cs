using System;
using ExploreTogether.Core.Items;
using Godot;
using GDict = Godot.Collections.Dictionary;

namespace ExploreTogether.UI;

/// <summary>
/// One slot in the inventory or equipment UI: port of the InventorySlotButton / EquipmentSlotButton
/// prefabs with InventorySlot / EquipmentSlot / SlotContainer behavior:
/// - 100x100, item name at the top, icon in the middle, stack size at the bottom
///   (empty equipment slots show their type, like EquipmentSlot's label);
/// - drag an item to another slot (the slot goes semi-transparent while dragging, like the CanvasGroup alpha 0.6);
/// - Ctrl + right-click drops the item into the world.
/// It only reports what the player did; the server decides the outcome and the UI redraws from its state.
/// </summary>
public partial class ItemSlotButton : PanelContainer
{
    public const float SlotSize = 100;

    /// <summary>from container, from slot, to container, to slot</summary>
    public event Action<ItemContainer, int, ItemContainer, int>? ItemDropped;
    /// <summary>container, slot</summary>
    public event Action<ItemContainer, int>? DropToWorldRequested;
    /// <summary>Ctrl + left-click: send the item to the other open container.</summary>
    public event Action<ItemContainer, int>? QuickTransferRequested;
    /// <summary>Alt + left-click: equip the item (into the player's matching gear slot).</summary>
    public event Action<ItemContainer, int>? QuickEquipRequested;

    public ItemContainer Container { get; }
    public int Index { get; }

    private readonly Label _name;
    private readonly TextureRect _icon;
    private readonly Label _count;
    private int? _itemId;

    public ItemSlotButton(ItemContainer container, int index)
    {
        Container = container;
        Index = index;
        CustomMinimumSize = new Vector2(SlotSize, SlotSize);
        MouseFilter = MouseFilterEnum.Stop;

        var stack = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(stack);

        _icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest, // pixel-art icons stay crisp
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _icon.OffsetTop = 22; _icon.OffsetBottom = -20; _icon.OffsetLeft = 18; _icon.OffsetRight = -18;
        stack.AddChild(_icon);

        _name = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _name.AddThemeFontSizeOverride("font_size", 13);
        _name.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        stack.AddChild(_name);

        _count = new Label { HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = MouseFilterEnum.Ignore };
        _count.AddThemeFontSizeOverride("font_size", 14);
        _count.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        _count.OffsetTop = -22;
        stack.AddChild(_count);

        SetItem(null, 0);
    }

    /// <param name="data">The item's state, e.g. a gun's loaded rounds (shown as "7/10").</param>
    public void SetItem(int? itemId, int count, int? data = null)
    {
        _itemId = itemId;
        var def = itemId is { } id ? ItemCatalog.Get(id) : null;
        if (def == null)
        {
            // Empty: equipment slots show their type (like EquipmentSlot's stack-size text did).
            _name.Text = Container == ItemContainer.Gear ? Gear.TypeOf(Index).ToString() : "";
            _name.Modulate = new Color(1, 1, 1, 0.45f);
            _icon.Texture = null;
            _count.Text = "";
            TooltipText = "";
            return;
        }
        _name.Text = def.Name;
        _name.Modulate = Colors.White;
        _icon.Texture = IconCache.Get(def.Icon);
        _count.Text = def.MagazineSize is { } size && data is { } loaded
            ? $"{loaded}/{size}"                             // a gun: rounds loaded
            : count > 0 ? count.ToString() : "";             // the original showed the stack size whenever it was > 0
        TooltipText = string.IsNullOrEmpty(def.Description) ? def.Name : $"{def.Name}\n{def.Description}";
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (_itemId == null) return default;
        var preview = new TextureRect
        {
            Texture = _icon.Texture,
            CustomMinimumSize = new Vector2(64, 64),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
            Modulate = new Color(1, 1, 1, 0.8f),
        };
        SetDragPreview(preview);
        Modulate = new Color(1, 1, 1, 0.6f);
        return new GDict { ["container"] = (int)Container, ["slot"] = Index };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary().ContainsKey("slot");

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        var d = data.AsGodotDictionary();
        ItemDropped?.Invoke((ItemContainer)d["container"].AsInt32(), d["slot"].AsInt32(), Container, Index);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd) Modulate = Colors.White;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right, CtrlPressed: true } && _itemId != null)
        {
            DropToWorldRequested?.Invoke(Container, Index);
            AcceptEvent();
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _itemId != null
                 && (click.CtrlPressed || click.AltPressed))
        {
            if (click.AltPressed) QuickEquipRequested?.Invoke(Container, Index);
            else QuickTransferRequested?.Invoke(Container, Index);
            AcceptEvent();
        }
    }
}

/// <summary>Loads item icons once.</summary>
public static class IconCache
{
    private static readonly System.Collections.Generic.Dictionary<string, Texture2D?> Cache = new();

    public static Texture2D? Get(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        if (!Cache.TryGetValue(file, out var tex))
            Cache[file] = tex = ResourceLoader.Exists($"res://assets/icons/{file}") ? GD.Load<Texture2D>($"res://assets/icons/{file}") : null;
        return tex;
    }
}

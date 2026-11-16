using System;
using System.Collections.Generic;
using ExploreTogether.Core.Items;
using Godot;

namespace ExploreTogether.UI;

/// <summary>
/// The inventory + equipment screen (port of the HUD's InventoryUI and GearUI, opened together like
/// the original's onToggleInventory):
/// - Equipment: the 12 EquipmentSlotButtons at their original paper-doll positions (EquipmentScrollView prefab);
/// - Inventory: a scrolling 4-column grid of 100x100 slots with 10 px spacing (InventoryScrollView prefab).
/// Drags and drops are forwarded as requests; the screen redraws from the state the server sends.
/// </summary>
public partial class InventoryScreen : CanvasLayer
{
    /// <summary>from container, from slot, to container, to slot</summary>
    public event Action<ItemContainer, int, ItemContainer, int>? MoveRequested;
    public event Action<ItemContainer, int>? DropRequested;

    // Original EquipmentScrollView anchored positions (Unity UI: center origin, y up) per Equipment.Type.
    private static readonly Dictionary<EquipmentType, Vector2> UnityGearPositions = new()
    {
        [EquipmentType.Eye] = new(-117, 66), [EquipmentType.Head] = new(0, 65),
        [EquipmentType.Coat] = new(-117, -50), [EquipmentType.Neck] = new(0, -49), [EquipmentType.Back] = new(117, -50),
        [EquipmentType.Weapon] = new(-117, -167), [EquipmentType.Torso] = new(0, -168), [EquipmentType.Offhand] = new(117, -169),
        [EquipmentType.Leg] = new(0, -287),
        [EquipmentType.Glove] = new(-117, -408), [EquipmentType.Shoe] = new(0, -408), [EquipmentType.Ring] = new(117, -408),
    };

    private readonly List<ItemSlotButton> _inventorySlots = new();
    private readonly List<ItemSlotButton> _gearSlots = new();
    private Control _root = null!;

    public override void _Ready()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 8);
        center.AddChild(_root);

        var panels = new HBoxContainer();
        panels.AddThemeConstantOverride("separation", 16);
        _root.AddChild(panels);

        panels.AddChild(BuildGearPanel());
        panels.AddChild(BuildInventoryPanel());

        var hint = new Label
        {
            Text = "Drag to move or equip  ·  Ctrl + right-click to drop  ·  Tab to close",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        hint.AddThemeConstantOverride("outline_size", 6);
        _root.AddChild(hint);

        // Scale down to fit small windows (the original layout assumed a large screen).
        GetViewport().SizeChanged += FitToScreen;
        FitToScreen();
    }

    private Control BuildGearPanel()
    {
        var box = PanelWithTitle("Equipment", out var content);
        // Convert the Unity positions to a top-left layout: x offset by half the panel, y flipped.
        const float left = 117 + ItemSlotButton.SlotSize / 2, top = 66 + ItemSlotButton.SlotSize / 2;
        var area = new Control { CustomMinimumSize = new Vector2(2 * left, top + 408 + ItemSlotButton.SlotSize / 2) };
        content.AddChild(area);
        for (int i = 0; i < Gear.SlotCount; i++)
        {
            var slot = NewSlot(ItemContainer.Gear, i);
            var p = UnityGearPositions[Gear.TypeOf(i)];
            slot.Position = new Vector2(left + p.X - ItemSlotButton.SlotSize / 2, top - p.Y - ItemSlotButton.SlotSize / 2);
            area.AddChild(slot);
            _gearSlots.Add(slot);
        }
        return box;
    }

    private Control BuildInventoryPanel()
    {
        var box = PanelWithTitle("Inventory", out var content);
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(4 * ItemSlotButton.SlotSize + 3 * 10 + 14, 524),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        content.AddChild(scroll);
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        scroll.AddChild(grid);
        for (int i = 0; i < Inventory.DefaultMaxSlots; i++)
        {
            var slot = NewSlot(ItemContainer.Inventory, i);
            grid.AddChild(slot);
            _inventorySlots.Add(slot);
        }
        return box;
    }

    private ItemSlotButton NewSlot(ItemContainer container, int index)
    {
        var slot = new ItemSlotButton(container, index);
        slot.ItemDropped += (fc, fs, tc, ts) => MoveRequested?.Invoke(fc, fs, tc, ts);
        slot.DropToWorldRequested += (c, s) => DropRequested?.Invoke(c, s);
        return slot;
    }

    private static PanelContainer PanelWithTitle(string title, out VBoxContainer content)
    {
        var panel = new PanelContainer();
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 10);
        panel.AddChild(margin);
        content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 8);
        margin.AddChild(content);
        content.AddChild(UiKit.Title(title));
        return panel;
    }

    /// <summary>Shows the server's state: inventory as (slot, item, count) triples, gear as item id per slot (-1 = empty).</summary>
    public void SetState(int[] inventoryTriples, int[] gear)
    {
        foreach (var s in _inventorySlots) s.SetItem(null, 0);
        for (int i = 0; i + 2 < inventoryTriples.Length; i += 3)
        {
            var slot = inventoryTriples[i];
            if (slot >= 0 && slot < _inventorySlots.Count)
                _inventorySlots[slot].SetItem(inventoryTriples[i + 1], inventoryTriples[i + 2]);
        }
        for (int i = 0; i < _gearSlots.Count; i++)
            _gearSlots[i].SetItem(i < gear.Length && gear[i] >= 0 ? gear[i] : null, 0);
    }

    public void Toggle()
    {
        Visible = !Visible;
        Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    private void FitToScreen()
    {
        if (_root == null) return;
        var available = GetViewport().GetVisibleRect().Size;
        var needed = _root.GetCombinedMinimumSize() + new Vector2(40, 40);
        var scale = Mathf.Min(1f, Mathf.Min(available.X / needed.X, available.Y / needed.Y));
        _root.Scale = new Vector2(scale, scale);
        _root.PivotOffset = _root.GetCombinedMinimumSize() / 2;
    }
}

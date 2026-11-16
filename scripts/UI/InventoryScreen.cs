using System;
using System.Collections.Generic;
using ExploreTogether.Core.Items;
using Godot;

namespace ExploreTogether.UI;

/// <summary>
/// The inventory + equipment screen (port of the HUD's InventoryUI and GearUI, opened together like
/// the original's onToggleInventory):
/// - Equipment: the 12 EquipmentSlotButtons at their original paper-doll positions (EquipmentScrollView prefab);
/// - Inventory: a scrolling 4-column grid of 100x100 slots with 10 px spacing (InventoryScrollView prefab);
/// - Storage (only while the hub's storage chest is open): the same kind of grid, next to the inventory
///   (port of the HUD's storage InventoryUI, which the original showed together with the player's inventory).
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
    private readonly List<ItemSlotButton> _storageSlots = new();
    private Control _root = null!;
    private Control _storagePanel = null!;
    private Label _hint = null!;

    /// <summary>True while the storage panel is showing.</summary>
    public bool StorageOpen => Visible && _storagePanel.Visible;

    public override void _Ready()
    {
        // Centered and scaled by hand in FitToScreen (a CenterContainer would reset the scale on every layout).
        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 8);
        AddChild(_root);

        var panels = new HBoxContainer();
        panels.AddThemeConstantOverride("separation", 16);
        _root.AddChild(panels);

        panels.AddChild(BuildGearPanel());
        panels.AddChild(BuildInventoryPanel());
        _storagePanel = BuildStoragePanel();
        _storagePanel.Visible = false;
        panels.AddChild(_storagePanel);

        _hint = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hint.AddThemeConstantOverride("outline_size", 6);
        _root.AddChild(_hint);
        UpdateHint();

        // Scale down to fit small windows (the original layout assumed a large screen).
        GetViewport().SizeChanged += FitToScreen;
        _root.MinimumSizeChanged += FitToScreen; // e.g. the storage panel opening or closing
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

    private Control BuildInventoryPanel() =>
        SlotGridPanel("Inventory", ItemContainer.Inventory, Inventory.DefaultMaxSlots, _inventorySlots);

    private Control BuildStoragePanel() =>
        SlotGridPanel("Storage", ItemContainer.Storage, Inventory.HubStorageSlots, _storageSlots);

    /// <summary>A titled, scrolling 4-column grid of slots (the original InventoryScrollView).</summary>
    private Control SlotGridPanel(string title, ItemContainer container, int count, List<ItemSlotButton> into)
    {
        var box = PanelWithTitle(title, out var content);
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
        for (int i = 0; i < count; i++)
        {
            var slot = NewSlot(container, i);
            grid.AddChild(slot);
            into.Add(slot);
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

    /// <summary>
    /// Shows the server's state: inventory as (slot, item, count, data) quads, gear as item id and data per slot
    /// (-1 = empty / none).
    /// </summary>
    public void SetState(int[] inventoryQuads, int[] gear, int[] gearData)
    {
        Fill(_inventorySlots, inventoryQuads);
        for (int i = 0; i < _gearSlots.Count; i++)
            _gearSlots[i].SetItem(i < gear.Length && gear[i] >= 0 ? gear[i] : null, 0,
                i < gearData.Length && gearData[i] >= 0 ? gearData[i] : null);
    }

    /// <summary>Shows the storage (from the server's (slot, item, count, data) quads), opening the screen if needed.</summary>
    public void ShowStorage(int[] storageQuads)
    {
        Fill(_storageSlots, storageQuads);
        if (_storagePanel.Visible && Visible) return;
        _storagePanel.Visible = true;
        UpdateHint();
        if (!Visible) Toggle();
    }

    /// <summary>Updates the storage panel without opening it (e.g. after a move the server answered late).</summary>
    public void UpdateStorage(int[] storageQuads) => Fill(_storageSlots, storageQuads);

    /// <summary>Hides the storage panel (walked away, closed the screen, or left the hub).</summary>
    public void CloseStorage()
    {
        if (!_storagePanel.Visible) return;
        _storagePanel.Visible = false;
        UpdateHint();
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (!Visible) CloseStorage();
        Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    private static void Fill(List<ItemSlotButton> slots, int[] quads)
    {
        foreach (var s in slots) s.SetItem(null, 0);
        for (int i = 0; i + 3 < quads.Length; i += 4)
        {
            var slot = quads[i];
            if (slot >= 0 && slot < slots.Count)
                slots[slot].SetItem(quads[i + 1], quads[i + 2], quads[i + 3] >= 0 ? quads[i + 3] : null);
        }
    }

    private void UpdateHint() => _hint.Text = _storagePanel.Visible
        ? "Drag between inventory and storage  ·  Ctrl + right-click to drop  ·  Tab to close"
        : "Drag to move or equip  ·  Ctrl + right-click to drop  ·  Tab to close";

    private void FitToScreen()
    {
        if (_root == null) return;
        var available = GetViewport().GetVisibleRect().Size;
        var size = _root.GetCombinedMinimumSize();
        var needed = size + new Vector2(40, 40);
        var scale = Mathf.Min(1f, Mathf.Min(available.X / needed.X, available.Y / needed.Y));
        _root.Size = size;
        _root.Scale = new Vector2(scale, scale);
        _root.Position = ((available - size * scale) / 2).Floor();
    }
}

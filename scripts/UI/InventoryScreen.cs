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
/// - Body (new, only while searching someone who's down): their equipment and inventory, laid out exactly like the
///   player's own (paper doll + grid), next to the player's; take-only, with "Take all".
/// - Debug supply (new, dev servers): every item, unlimited; drag one out to take a copy, drag an item onto it to delete it.
/// Drags and drops are forwarded as requests; the screen redraws from the state the server sends.
/// </summary>
public partial class InventoryScreen : CanvasLayer
{
    /// <summary>from container, from slot, to container, to slot</summary>
    public event Action<ItemContainer, int, ItemContainer, int>? MoveRequested;
    public event Action<ItemContainer, int>? DropRequested;
    /// <summary>The loot panel's "Take all" button.</summary>
    public event Action? TakeAllRequested;



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
    private readonly List<ItemSlotButton> _lootSlots = new();
    private readonly List<ItemSlotButton> _lootGearSlots = new();
    private readonly List<ItemSlotButton> _supplySlots = new();
    private Control _root = null!;
    private Control _storagePanel = null!;
    private Control _lootPanel = null!;
    private Control _supplyPanel = null!;
    private Label _lootGearTitle = null!, _lootInventoryTitle = null!;
    private Label _hint = null!;

    /// <summary>True while the storage panel is showing.</summary>
    public bool StorageOpen => Visible && _storagePanel.Visible;

    /// <summary>True while a body's equipment and inventory are showing.</summary>
    public bool LootOpen => Visible && _lootPanel.Visible;

    /// <summary>True while the debug supply is showing.</summary>
    public bool SupplyOpen => Visible && _supplyPanel.Visible;

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
        _lootPanel = BuildLootPanel();
        _lootPanel.Visible = false;
        panels.AddChild(_lootPanel);
        _supplyPanel = SlotGridPanel("Debug supply (unlimited)", ItemContainer.Supply, DebugSupply.Items.Count, _supplySlots);
        _supplyPanel.Visible = false;
        panels.AddChild(_supplyPanel);

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

    private Control BuildGearPanel() => GearPanel("Equipment", ItemContainer.Gear, _gearSlots);

    /// <summary>The paper doll: the 12 equipment slots at the original positions.</summary>
    private Control GearPanel(string title, ItemContainer container, List<ItemSlotButton> into)
    {
        var box = PanelWithTitle(title, out var content);
        // Convert the Unity positions to a top-left layout: x offset by half the panel, y flipped.
        const float left = 117 + ItemSlotButton.SlotSize / 2, top = 66 + ItemSlotButton.SlotSize / 2;
        var area = new Control { CustomMinimumSize = new Vector2(2 * left, top + 408 + ItemSlotButton.SlotSize / 2) };
        content.AddChild(area);
        for (int i = 0; i < Gear.SlotCount; i++)
        {
            var slot = NewSlot(container, i);
            var p = UnityGearPositions[Gear.TypeOf(i)];
            slot.Position = new Vector2(left + p.X - ItemSlotButton.SlotSize / 2, top - p.Y - ItemSlotButton.SlotSize / 2);
            area.AddChild(slot);
            into.Add(slot);
        }
        return box;
    }

    private Control BuildInventoryPanel() =>
        SlotGridPanel("Inventory", ItemContainer.Inventory, Inventory.DefaultMaxSlots, _inventorySlots);

    private Control BuildStoragePanel() =>
        SlotGridPanel("Storage", ItemContainer.Storage, Inventory.HubStorageSlots, _storageSlots);

    /// <summary>Someone else's equipment and inventory, laid out like the player's own, with "Take all".</summary>
    private Control BuildLootPanel()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var gear = GearPanel("Equipment", ItemContainer.LootGear, _lootGearSlots);
        _lootGearTitle = TitleOf(gear);
        row.AddChild(gear);
        var inventory = SlotGridPanel("Inventory", ItemContainer.Loot, Inventory.DefaultMaxSlots, _lootSlots);
        _lootInventoryTitle = TitleOf(inventory);
        var takeAll = new Button { Text = "Take all" };
        takeAll.Pressed += () => TakeAllRequested?.Invoke();
        ((VBoxContainer)inventory.GetChild(0).GetChild(0)).AddChild(takeAll);
        row.AddChild(inventory);
        return row;
    }

    private static Label TitleOf(Control panel) => (Label)panel.GetChild(0).GetChild(0).GetChild(0);

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
        FillGear(_gearSlots, gear, gearData);
    }

    private static void FillGear(List<ItemSlotButton> slots, int[] gear, int[] gearData)
    {
        for (int i = 0; i < slots.Count; i++)
            slots[i].SetItem(i < gear.Length && gear[i] >= 0 ? gear[i] : null, 0,
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

    /// <summary>Shows a downed person's equipment and inventory (whose: <paramref name="name"/>), opening the screen if needed.</summary>
    public void ShowLoot(string name, int[] quads, int[] gear, int[] gearData)
    {
        _lootGearTitle.Text = $"{name}: equipment";
        _lootInventoryTitle.Text = $"{name}: inventory";
        UpdateLoot(quads, gear, gearData);
        if (_lootPanel.Visible && Visible) return;
        _lootPanel.Visible = true;
        UpdateHint();
        if (!Visible) Toggle();
    }

    public void UpdateLoot(int[] quads, int[] gear, int[] gearData)
    {
        Fill(_lootSlots, quads);
        FillGear(_lootGearSlots, gear, gearData);
    }

    /// <summary>Shows the debug supply, opening the screen if needed.</summary>
    public void ShowSupply(int[] quads)
    {
        Fill(_supplySlots, quads);
        if (_supplyPanel.Visible && Visible) return;
        _supplyPanel.Visible = true;
        UpdateHint();
        if (!Visible) Toggle();
    }

    public void CloseSupply()
    {
        if (!_supplyPanel.Visible) return;
        _supplyPanel.Visible = false;
        UpdateHint();
    }

    public void CloseLoot()
    {
        if (!_lootPanel.Visible) return;
        _lootPanel.Visible = false;
        UpdateHint();
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (!Visible)
        {
            CloseStorage();
            CloseLoot();
            CloseSupply();
        }
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

    private void UpdateHint() => _hint.Text = _lootPanel.Visible
        ? "Drag items from them to take them  ·  Tab to close"
        : _supplyPanel.Visible
        ? "Drag an item out to take a copy  ·  Drag one of yours onto the supply to delete it  ·  Tab to close"
        : _storagePanel.Visible
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

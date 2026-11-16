using System.Collections.Generic;
using ExploreTogether.Core.Items;
using Godot;

namespace ExploreTogether.UI;

/// <summary>Minimal inventory view (Tab): the 28 slots as a grid of item names and counts.</summary>
public partial class InventoryPanel : CanvasLayer
{
    private GridContainer _grid = null!;
    private readonly List<Label> _slots = new();

    public override void _Ready()
    {
        var box = UiKit.CenteredPanel(this, 640);
        box.AddChild(UiKit.Title("Inventory   [Tab to close]"));
        _grid = new GridContainer { Columns = 4 };
        _grid.AddThemeConstantOverride("h_separation", 6);
        _grid.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(_grid);
        for (int i = 0; i < Inventory.DefaultMaxSlots; i++)
        {
            var cell = new PanelContainer { CustomMinimumSize = new Vector2(150, 34) };
            var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cell.AddChild(label);
            _grid.AddChild(cell);
            _slots.Add(label);
        }
    }

    /// <param name="triples">(slot, item id, count) for each occupied slot.</param>
    public void SetContents(int[] triples)
    {
        foreach (var l in _slots) l.Text = "";
        for (int i = 0; i + 2 < triples.Length; i += 3)
        {
            var (slot, itemId, count) = (triples[i], triples[i + 1], triples[i + 2]);
            if (slot < 0 || slot >= _slots.Count) continue;
            _slots[slot].Text = count > 1 ? $"{ItemCatalog.NameOf(itemId)} x{count}" : ItemCatalog.NameOf(itemId);
        }
    }

    public void Toggle()
    {
        Visible = !Visible;
        Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }
}

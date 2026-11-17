using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;

namespace ExploreTogether.Core.Tests;

public class HotbarTests
{
    private const int Pistol = 1337, Sword = 1515;

    private static (Inventory Inv, Gear Gear) Kit()
    {
        var inv = new Inventory();
        inv.AddItem(Pistol, uid: 11);
        inv.AddItem(Sword, uid: 12);
        inv.AddItem(ItemCatalog.BatId, uid: 13);
        inv.AddItem(ItemCatalog.LogId);
        return (inv, new Gear());
    }

    [Fact]
    public void Key_EquipsItsItem_AndPutsItAwayOnSecondPress()
    {
        var (inv, gear) = Kit();
        var bar = new Hotbar();
        Assert.True(bar.Bind(0, inv.slots[0]));
        Assert.True(bar.Bind(1, inv.slots[1]));
        Assert.True(bar.Use(0, inv, gear));
        Assert.Equal(Pistol, gear.EquippedWeapon?.Id);
        Assert.True(bar.Use(1, inv, gear)); // the sword swaps in, the pistol goes back
        Assert.Equal(Sword, gear.EquippedWeapon?.Id);
        Assert.Equal((ItemContainer.Inventory, 1), bar.Find(0, inv, gear));
        Assert.True(bar.Use(1, inv, gear)); // again: put away, hands empty
        Assert.Null(gear.EquippedWeapon);
    }

    [Fact]
    public void OnlyEquipment_AndOneKeyPerItem()
    {
        var (inv, _) = Kit();
        var bar = new Hotbar();
        Assert.False(bar.Bind(0, inv.slots[3])); // a log
        Assert.True(bar.Bind(0, inv.slots[2]));
        Assert.True(bar.Bind(4, inv.slots[2])); // moved to key 5
        Assert.Null(bar[0]);
        Assert.Equal(ItemCatalog.BatId, bar[4]?.ItemId);
        Assert.True(bar.Bind(4, null));
        Assert.Null(bar[4]);
    }

    [Fact]
    public void ItemsWithSerials_AreFoundBySerial()
    {
        var inv = new Inventory();
        inv.AddItem(Pistol, uid: 1);
        inv.AddItem(Pistol, uid: 2);
        var bar = new Hotbar();
        bar.Bind(0, inv.slots[1]);
        Assert.Equal((ItemContainer.Inventory, 1), bar.Find(0, inv, new Gear()));
        inv.RemoveItem(1);
        Assert.Null(bar.Find(0, inv, new Gear())); // that pistol is gone, even if another is carried
        Assert.False(bar.Use(0, inv, new Gear()));
    }

    [Fact]
    public void Hotbar_IsSavedPerCharacter()
    {
        using var db = GameDatabase.OpenInMemory();
        var acct = db.CreateAccount("kermit", "h")!;
        var c = db.CreateCharacter(acct.Id, "Kermit", "000000").Character!;
        var (inv, _) = Kit();
        var bar = new Hotbar();
        bar.Bind(2, inv.slots[0]);
        bar.Bind(5, inv.slots[2]);
        db.SaveHotbar(c.Id, bar.Occupied());
        var loaded = new Hotbar();
        loaded.Load(db.LoadHotbar(c.Id));
        Assert.Equal(new HotbarEntry(Pistol, 11), loaded[2]);
        Assert.Equal(new HotbarEntry(ItemCatalog.BatId, 13), loaded[5]);
        Assert.Null(loaded[0]);
    }
}

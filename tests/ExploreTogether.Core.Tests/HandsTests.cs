using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Tests;

public class HandsTests
{
    private static int Find(Inventory inv, int id) => inv.FindItemInSlot(id);

    [Fact]
    public void FlashlightGoesInTheOffhand_WithAOneHandedWeapon()
    {
        var inv = new Inventory();
        inv.AddItem(ItemCatalog.FlashlightId);
        inv.AddItem(1337); // pistol
        var gear = new Gear();
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, Find(inv, 1337), ItemContainer.Gear, (int)EquipmentType.Weapon));
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, Find(inv, ItemCatalog.FlashlightId), ItemContainer.Gear, (int)EquipmentType.Offhand));
        Assert.Equal(ItemCatalog.FlashlightId, gear.slots[(int)EquipmentType.Offhand].itemId);
    }

    [Fact]
    public void TwoHandedWeapons_AndTheOffhand_DontMix()
    {
        var inv = new Inventory();
        inv.AddItem(ItemCatalog.FlashlightId);
        inv.AddItem(ItemCatalog.PumpShotgunId);
        var gear = new Gear();
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, Find(inv, ItemCatalog.FlashlightId), ItemContainer.Gear, (int)EquipmentType.Offhand));
        var shotgun = Find(inv, ItemCatalog.PumpShotgunId);
        Assert.NotNull(SlotContainerLogic.HandsBlock(gear, ItemContainer.Inventory, shotgun, ItemContainer.Gear, (int)EquipmentType.Weapon, ItemCatalog.PumpShotgunId));
        Assert.False(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, shotgun, ItemContainer.Gear, (int)EquipmentType.Weapon));
        // Put the flashlight away: now the shotgun fits, and the flashlight can't go back.
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Gear, (int)EquipmentType.Offhand, ItemContainer.Inventory, inv.FindEmptySlot()));
        Assert.True(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, shotgun, ItemContainer.Gear, (int)EquipmentType.Weapon));
        Assert.False(SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, Find(inv, ItemCatalog.FlashlightId), ItemContainer.Gear, (int)EquipmentType.Offhand));
    }

    [Fact]
    public void Furniture_IsNeverLoot()
    {
        var random = new Random(3);
        for (var i = 0; i < 2000; i++)
            Assert.False(ItemCatalog.GenerateItem(random).BreaksOnHit);
    }
}

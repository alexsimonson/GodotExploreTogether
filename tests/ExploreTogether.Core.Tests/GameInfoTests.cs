using ExploreTogether.Core;

namespace ExploreTogether.Core.Tests;

public class GameInfoTests
{
    [Fact]
    public void DefaultPort_IsInValidRange()
    {
        Assert.InRange(GameInfo.DefaultPort, 1024, 65535);
    }
}

namespace ExploreTogether.Core.Persistence;

public sealed record AccountRecord(long Id, string Username, string PasswordHash);

/// <summary>A saved spot in the hub (feet position and facing).</summary>
public readonly record struct SavedPosition(float X, float Y, float Z, float Yaw);

public sealed record CharacterRecord(
    long Id,
    long AccountId,
    string Name,
    string ColorHex,
    DateTime CreatedAt,
    DateTime? LastPlayedAt,
    double PlayTimeSeconds,
    SavedPosition? HubPosition);

public enum CreateResult
{
    Created,
    NameTaken,
    LimitReached,
}

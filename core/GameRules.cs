using System.Text.RegularExpressions;

namespace ExploreTogether.Core;

/// <summary>Validation rules shared by client (early feedback) and server (authoritative).</summary>
public static partial class GameRules
{
    public const int MaxCharactersPerAccount = 5;

    public const int UsernameMin = 3, UsernameMax = 20;
    public const int PasswordMin = 4, PasswordMax = 128;
    public const int CharacterNameMin = 3, CharacterNameMax = 16;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex UsernamePattern();

    // Letters/digits, with single spaces, hyphens or underscores between them.
    [GeneratedRegex("^[A-Za-z0-9]+([ _-][A-Za-z0-9]+)*$")]
    private static partial Regex CharacterNamePattern();

    public static string? ValidateUsername(string username)
    {
        if (username.Length < UsernameMin || username.Length > UsernameMax)
            return $"Username must be {UsernameMin}-{UsernameMax} characters.";
        if (!UsernamePattern().IsMatch(username))
            return "Username may only contain letters, digits and underscores.";
        return null;
    }

    public static string? ValidatePassword(string password)
    {
        if (password.Length < PasswordMin || password.Length > PasswordMax)
            return $"Password must be {PasswordMin}-{PasswordMax} characters.";
        return null;
    }

    public static string? ValidateCharacterName(string name)
    {
        if (name.Length < CharacterNameMin || name.Length > CharacterNameMax)
            return $"Character name must be {CharacterNameMin}-{CharacterNameMax} characters.";
        if (!CharacterNamePattern().IsMatch(name))
            return "Character name may only contain letters, digits, and single spaces, hyphens or underscores between them.";
        return null;
    }

    /// <summary>Normalizes a color to lowercase "rrggbb", or returns null if it isn't one.</summary>
    public static string? NormalizeColorHex(string hex)
    {
        hex = hex.Trim().TrimStart('#').ToLowerInvariant();
        if (hex.Length == 8) hex = hex[..6]; // drop alpha
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex : null;
    }
}

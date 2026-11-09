namespace Sluice.Core.Profiles;

/// <summary>How proxy passwords are stored in a profile file (Profile → Passwords Encryption).</summary>
public enum PasswordEncryptionMode
{
    /// <summary>Plain text.</summary>
    Disabled,
    /// <summary>Obfuscated with a static key: anyone with the app can read them.</summary>
    Basic,
    /// <summary>Windows DPAPI, current user: only this Windows account can read them.</summary>
    CurrentUser,
    /// <summary>Encrypted with a profile master password the user types in.</summary>
    MasterPassword,
}

/// <summary>Supplies the master password when a profile needs one. Return null to cancel.</summary>
public delegate string? MasterPasswordProvider(string profileName, bool confirmNew);

/// <summary>Encodes/decodes individual password fields for one profile file.</summary>
public interface IPasswordCodec
{
    string Encode(string plain);
    string Decode(string stored);
}

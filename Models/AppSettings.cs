using System.Text.Json.Serialization;

namespace KnightWinNotes.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppThemeMode
{
    Sync,
    Light,
    Dark
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccentPreset
{
    Sync,
    Red,
    Purple,
    Matcha,
    Beige,
    Orange
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PadColorPreset
{
    Accent,
    Yellow,
    White
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecureSavePasswordPolicy
{
    UseMasterPassword,
    AskEveryTime
}

public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Sync;
    public AccentPreset Accent { get; set; } = AccentPreset.Sync;
    public PadColorPreset PadColor { get; set; } = PadColorPreset.White;
    public bool AdaptPadInDarkMode { get; set; } = true;

    public SecureSavePasswordPolicy PasswordPolicy { get; set; } = SecureSavePasswordPolicy.UseMasterPassword;
    public string? MasterPasswordSaltBase64 { get; set; }
    public string? MasterPasswordVerifierBase64 { get; set; }
    public string? MasterPasswordProtectedBlobBase64 { get; set; }

    public string EditorFontFamily { get; set; } = "Segoe UI Variable Text, Segoe UI, Noto Sans, sans-serif";
    public double EditorFontSize { get; set; } = 15.0;
    public bool WordWrap { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;

    public List<string> RecentFiles { get; set; } = new();

    [JsonIgnore]
    public bool HasMasterPassword =>
        !string.IsNullOrWhiteSpace(MasterPasswordSaltBase64) &&
        !string.IsNullOrWhiteSpace(MasterPasswordVerifierBase64) &&
        !string.IsNullOrWhiteSpace(MasterPasswordProtectedBlobBase64);
}

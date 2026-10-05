using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GmailDesktop.Models;

public sealed class AccountProfile : INotifyPropertyChanged
{
    private string _displayName = "Gmail";
    private string _email = string.Empty;
    private string _color = "#2563EB";
    private bool _useGmailAvatar;
    private string _gmailAvatarUrl = string.Empty;
    private string _lastUrl = "https://mail.google.com/";
    private bool _isSelected;
    private string _id = Guid.NewGuid().ToString("N");

    public string Id
    {
        get => _id;
        set => _id = value ?? string.Empty;
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, string.IsNullOrWhiteSpace(value) ? "Gmail" : value.Trim());
    }

    public string Email
    {
        get => _email;
        set => SetField(ref _email, value?.Trim() ?? string.Empty);
    }

    public string Color
    {
        get => _color;
        set => SetField(ref _color, IsValidColor(value) ? value : "#2563EB");
    }

    public bool UseGmailAvatar
    {
        get => _useGmailAvatar;
        set => SetField(ref _useGmailAvatar, value);
    }

    public string GmailAvatarUrl
    {
        get => _gmailAvatarUrl;
        set => SetField(ref _gmailAvatarUrl, IsValidGoogleAvatarUrl(value) ? value : string.Empty);
    }

    public string LastUrl
    {
        get => _lastUrl;
        set => SetField(ref _lastUrl, string.IsNullOrWhiteSpace(value) ? "https://mail.google.com/" : value);
    }

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    [JsonIgnore]
    public string Initials => CreateInitials(string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName);

    public static string CreateInitials(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "G";

        var parts = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            return string.Concat(parts.Take(2).Select(part => StringInfo.GetNextTextElement(part))).ToUpperInvariant();

        var name = new StringInfo(parts[0]);
        return name.SubstringByTextElements(0, Math.Min(2, name.LengthInTextElements)).ToUpperInvariant();
    }

    [JsonIgnore]
    public string? ActiveAvatarUrl => UseGmailAvatar && !string.IsNullOrWhiteSpace(GmailAvatarUrl)
        ? GmailAvatarUrl
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static bool IsValidColor(string? value) =>
        value is { Length: 7 } &&
        value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static bool IsValidGoogleAvatarUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return IsHostOrSubdomainOf(uri.Host, "googleusercontent.com") ||
               IsHostOrSubdomainOf(uri.Host, "ggpht.com");
    }

    private static bool IsHostOrSubdomainOf(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(DisplayName) or nameof(Email))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Initials)));
        if (propertyName is nameof(UseGmailAvatar) or nameof(GmailAvatarUrl))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveAvatarUrl)));
    }
}

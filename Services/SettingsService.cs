using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmailDesktop.Models;

namespace GmailDesktop.Services;

public sealed class SettingsService
{
    private static readonly byte[] SettingsEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("GmailDesktop.Settings.v1"));

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GmailDesktop");

    public string ProfilesDirectory => Path.Combine(AppDataDirectory, "Profiles");
    private string SettingsPath => Path.Combine(AppDataDirectory, "settings.dat");

    public AppSettings Load()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        QueueDeletedProfileCleanup();

        var settings = File.Exists(SettingsPath) ? LoadEncryptedSettings() : new AppSettings();
        return Normalize(settings);
    }

    private AppSettings LoadEncryptedSettings()
    {
        for (var attempt = 0; ; attempt++)
        {
            byte[]? plaintext = null;
            try
            {
                var encrypted = File.ReadAllBytes(SettingsPath);
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    SettingsEntropy,
                    DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AppSettings>(plaintext, _jsonOptions) ?? new AppSettings();
            }
            catch (IOException) when (attempt < 2)
            {
                // A scanner or backup process can briefly lock the file. Retry instead
                // of quarantining valid settings as though their contents were corrupt.
                Thread.Sleep(50 * (1 << attempt));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                PreserveUnreadableEncryptedSettings();
                return new AppSettings();
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var tempPath = SettingsPath + ".tmp";
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(settings, _jsonOptions);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(
                plaintext,
                SettingsEntropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, SettingsPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (1 << attempt));
            }
        }
    }

    public string GetProfileDirectory(AccountProfile account)
    {
        if (account.Id.Length != 32 || !Guid.TryParseExact(account.Id, "N", out _))
            throw new InvalidOperationException("The browser profile ID is invalid.");

        var profilePath = Path.GetFullPath(Path.Combine(ProfilesDirectory, account.Id));
        var rootPath = Path.GetFullPath(ProfilesDirectory) + Path.DirectorySeparatorChar;
        if (!profilePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The browser profile path is invalid.");
        return profilePath;
    }

    public Task<bool> DeleteProfileAsync(AccountProfile account)
    {
        var path = GetProfileDirectory(account);
        return Task.Run(() => DeleteProfile(path));
    }

    private static bool DeleteProfile(string path)
    {
        if (!Directory.Exists(path)) return true;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 4) Thread.Sleep(100 * (1 << attempt));
            }
        }

        return TryMarkForDeletion(path);
    }

    private static bool TryMarkForDeletion(string path)
    {
        try
        {
            var parent = Directory.GetParent(path)?.FullName;
            if (parent is null) return false;
            Directory.Move(path, Path.Combine(parent, $".deleted-{Guid.NewGuid():N}"));
            return true;
        }
        catch
        {
            // Leave the original in place so the caller can report the exact path.
            return false;
        }
    }

    private void QueueDeletedProfileCleanup()
    {
        var profilesDirectory = ProfilesDirectory;
        _ = Task.Run(() => CleanupDeletedProfiles(profilesDirectory));
    }

    private static void CleanupDeletedProfiles(string profilesDirectory)
    {
        try
        {
            if (!Directory.Exists(profilesDirectory)) return;
            foreach (var directory in Directory.EnumerateDirectories(profilesDirectory, ".deleted-*"))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Retry during a later launch.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must never prevent application startup.
        }
    }

    private void PreserveUnreadableEncryptedSettings()
    {
        var backupPath = Path.Combine(
            AppDataDirectory,
            $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.dat");
        // Do not return empty settings if preservation fails: a later save would
        // overwrite the only recoverable copy of the account metadata.
        File.Move(SettingsPath, backupPath);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Accounts ??= [];
        if (!Enum.IsDefined(settings.MemoryMode)) settings.MemoryMode = MemoryMode.LowestMemory;
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = ThemePreference.System;
        if (!Enum.IsDefined(settings.AppScale)) settings.AppScale = AppScalePreference.Normal;

        var normalizedAccounts = new List<AccountProfile>(settings.Accounts.Count);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? selectedId = null;

        foreach (var account in settings.Accounts)
        {
            if (account is null) continue;

            var originalId = account.Id;
            var normalizedId = Guid.TryParseExact(originalId, "N", out var id)
                ? id.ToString("N")
                : Guid.NewGuid().ToString("N");
            if (!usedIds.Add(normalizedId))
            {
                normalizedId = Guid.NewGuid().ToString("N");
                usedIds.Add(normalizedId);
            }

            account.Id = normalizedId;
            account.IsSelected = false;
            normalizedAccounts.Add(account);

            if (selectedId is null && string.Equals(
                    settings.SelectedAccountId,
                    originalId,
                    StringComparison.OrdinalIgnoreCase))
            {
                selectedId = normalizedId;
            }
        }

        settings.Accounts = normalizedAccounts;
        settings.SelectedAccountId = selectedId;
        return settings;
    }

}

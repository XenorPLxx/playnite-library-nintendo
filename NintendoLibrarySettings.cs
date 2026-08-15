using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnite;

namespace NintendoLibrary;

public partial class NintendoLibraryPluginSettings : ObservableObject
{
    [ObservableProperty] private bool connectAccount = true;
    [ObservableProperty] private bool excludeAddOnOnlyEntries;
}

[INotifyPropertyChanged]
public partial class NintendoLibrarySettingsHandler : PluginSettingsHandler
{
    private const string SettingsErrorNotification = "nintendo_settings_error";

    private static readonly ILogger logger = LogManager.GetLogger();

    /// <summary>Cancels a check that a newer one has superseded.</summary>
    private CancellationTokenSource? authenticationCheck;
    private readonly NintendoLibraryPlugin plugin;

    [ObservableProperty] private NintendoLibraryPluginSettings settings = new();
    [ObservableProperty] private bool? isUserLoggedIn;

    public string AuthenticationStatus => IsUserLoggedIn switch
    {
        true => Loc.logged_in(),
        false => Loc.not_logged_in(),
        _ => Loc.login_checking()
    };

    public NintendoLibrarySettingsHandler(NintendoLibraryPlugin plugin, Plugin.GetSettingsHandlerArgs settingsArgs)
    {
        this.plugin = plugin;
    }

    public override UserControl GetEditView(GetSettingsViewArgs args)
    {
        return new NintendoLibrarySettingsView { DataContext = this };
    }

    public override Task BeginEditAsync(BeginEditArgs args)
    {
        Settings = Clone(plugin.Settings);
        _ = UpdateIsUserLoggedInAsync();
        return Task.CompletedTask;
    }

    public override Task CancelEditAsync(CancelEditArgs args)
    {
        Settings = Clone(plugin.Settings);
        return Task.CompletedTask;
    }

    public override Task EndEditAsync(EndEditArgs args)
    {
        if (plugin.SaveSettings(Clone(Settings)))
        {
            plugin.PlayniteApi.Notifications.Remove(SettingsErrorNotification);
        }
        else
        {
            // The dialog is already closing, so a notification outlives it where a dialog would not.
            plugin.PlayniteApi.Notifications.Add(new NotificationMessage(
                SettingsErrorNotification,
                Loc.nintendo_settings_save_failed(),
                NotificationSeverity.Error,
                async () => await plugin.PlayniteApi.MainView.OpenPluginSettingsAsync(NintendoLibraryPlugin.Id)));
        }

        return Task.CompletedTask;
    }

    public override Task<ICollection<string>> VerifySettingsAsync(VerifySettingsArgs args)
    {
        return Task.FromResult<ICollection<string>>([]);
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            var client = new NintendoAccountClient(plugin.PlayniteApi);
            await client.LoginAsync();
            await UpdateIsUserLoggedInAsync();
        }
        catch (Exception e) when (!plugin.PlayniteApi.AppInfo.ThrowAllErrors)
        {
            logger.Error(e, "Failed to authenticate Nintendo account.");
            IsUserLoggedIn = false;
        }
    }

    private async Task UpdateIsUserLoggedInAsync()
    {
        // Supersede any check still running, so an older, slower one cannot report over a newer result.
        var checkTokenSource = new CancellationTokenSource();
        var previousTokenSource = Interlocked.Exchange(ref authenticationCheck, checkTokenSource);
        previousTokenSource?.Cancel();
        previousTokenSource?.Dispose();
        var cancellationToken = checkTokenSource.Token;

        IsUserLoggedIn = null;
        try
        {
            var client = new NintendoAccountClient(plugin.PlayniteApi);
            var loggedIn = await client.GetIsUserLoggedInAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                IsUserLoggedIn = loggedIn;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer check replaced this one and owns the reported status.
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to check Nintendo authentication.");
            if (!cancellationToken.IsCancellationRequested)
            {
                IsUserLoggedIn = false;
            }
        }
    }

    partial void OnIsUserLoggedInChanged(bool? value)
    {
        OnPropertyChanged(nameof(AuthenticationStatus));
    }

    internal static NintendoLibraryPluginSettings LoadSettings(string userDataDir)
    {
        var path = Path.Combine(userDataDir, "settings.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<NintendoLibraryPluginSettings>(File.ReadAllText(path)) ?? new NintendoLibraryPluginSettings()
                : new NintendoLibraryPluginSettings();
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to load Nintendo settings.");
            return new NintendoLibraryPluginSettings();
        }
    }

    /// <summary>
    /// Writes the settings, reporting rather than throwing when the user data folder cannot be
    /// written. Throwing here would surface as the whole settings dialog failing to close.
    /// </summary>
    internal static bool SaveSettings(string userDataDir, NintendoLibraryPluginSettings settings)
    {
        try
        {
            Directory.CreateDirectory(userDataDir);
            var path = Path.Combine(userDataDir, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to save Nintendo settings.");
            return false;
        }
    }

    private static NintendoLibraryPluginSettings Clone(NintendoLibraryPluginSettings source)
    {
        return new NintendoLibraryPluginSettings
        {
            ConnectAccount = source.ConnectAccount,
            ExcludeAddOnOnlyEntries = source.ExcludeAddOnOnlyEntries
        };
    }
}

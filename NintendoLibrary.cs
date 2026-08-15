using Playnite;

namespace NintendoLibrary;

public sealed class NintendoLibraryPlugin : Plugin
{
    private static readonly ILogger logger = LogManager.GetLogger();
    private static readonly IdImportableProperty nintendoSource = new("nintendo", "Nintendo");

    public const string Id = "Xenor.NintendoLibrary";

    public IPlayniteApi PlayniteApi { get; private set; } = null!;

    public NintendoLibraryPluginSettings Settings { get; private set; } = new();

    public NintendoLibraryPlugin()
    {
        LibrarySettings = new LibrarySupport
        {
            LibraryName = "Nintendo",
            CanCloseOriginalClient = false,
            CanOpenOriginalClient = false,
            CanImportPlaytime = false,
            CanImportPlaySessions = false
        };
    }

    public override Task InitializeAsync(InitializeArgs args)
    {
        PlayniteApi = args.Api;
        Loc.Api = args.Api;
        Settings = NintendoLibrarySettingsHandler.LoadSettings(PlayniteApi.UserDataDir);
        return Task.CompletedTask;
    }

    public override async Task<List<ImportableGame>> GetGamesAsync(LibraryGetGamesArgs args)
    {
        if (!Settings.ConnectAccount || args.CancelToken.IsCancellationRequested)
        {
            return [];
        }

        try
        {
            var client = new NintendoAccountClient(PlayniteApi);
            var cards = await client.GetVirtualGameCardsAsync(args.CancelToken);
            var games = new List<ImportableGame>();

            foreach (var card in cards)
            {
                args.CancelToken.ThrowIfCancellationRequested();
                if (Settings.ExcludeAddOnOnlyEntries && card.IsAddOnOnly)
                {
                    continue;
                }

                // Normalization can empty a name that looked usable, e.g. one made only of marks.
                var name = NintendoGameName.Normalize(card.ApplicationName);
                if (string.IsNullOrWhiteSpace(card.ApplicationId) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var game = new ImportableGame(name, Id, card.ApplicationId)
                {
                    Source = nintendoSource,
                    Platforms = GetPlatforms(card)
                };

                if (!string.IsNullOrWhiteSpace(card.IconUrl))
                {
                    game.MediaFiles = [new ImportableFile(BuiltInGameDataId.DesktopIcon, card.IconUrl)];
                }

                games.Add(game);
            }

            PlayniteApi.Notifications.Remove("nintendo_import_error");
            return games
                .GroupBy(game => game.GameId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }
        catch (OperationCanceledException) when (args.CancelToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to import Nintendo games.");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "nintendo_import_error",
                Loc.library_import_error("Nintendo") + Environment.NewLine + e.Message,
                NotificationSeverity.Error,
                async () => await PlayniteApi.MainView.OpenPluginSettingsAsync(Id)));
            return [];
        }
    }

    public override Task<PluginSettingsHandler?> GetSettingsHandlerAsync(GetSettingsHandlerArgs args)
    {
        return Task.FromResult<PluginSettingsHandler?>(new NintendoLibrarySettingsHandler(this, args));
    }

    internal bool SaveSettings(NintendoLibraryPluginSettings settings)
    {
        Settings = settings;
        return NintendoLibrarySettingsHandler.SaveSettings(PlayniteApi.UserDataDir, settings);
    }

    private static List<ImportableProperty> GetPlatforms(VirtualGameCard card)
    {
        var platforms = new List<ImportableProperty>();
        if (card.ApparentPlatform == "NX" || card.HasNxApplication || card.HasNxAddOnContents)
        {
            platforms.Add(new SpecImportableProperty("nintendo_switch"));
        }

        if (card.ApparentPlatform == "OUNCE" || card.HasOunceApplication || card.HasOunceAddOnContents)
        {
            platforms.Add(new SpecImportableProperty("nintendo_switch2"));
        }

        return platforms;
    }

}

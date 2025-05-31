using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using NintendoLibrary.Models;
using NintendoLibrary.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace NintendoLibrary
{
  [LoadPlugin]
  public class NintendoLibrary : LibraryPluginBase<NintendoLibrarySettingsViewModel>
  {
    public NintendoLibrary(IPlayniteAPI api) : base(
        "Nintendo",
        Guid.Parse("e4ac81cb-1b1a-4ec9-8639-9a9633989a72"),
        new LibraryPluginProperties { CanShutdownClient = false, HasCustomizedGameImport = true, HasSettings = true },
        null,
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png"),
        (_) => new NintendoLibrarySettingsView(),
        api)
    {
      SettingsViewModel = new NintendoLibrarySettingsViewModel(this, api);
    }

    private string ParsePlatform(string platformId)
    {
      string platform = null;

      switch (platformId)
      {
        case "HAC":
          platform = "nintendo_switch";
          break;

        case "CTR":
          platform = "nintendo_3ds";
          break;

        case "WUP":
          platform = "nintendo_wiiu";
          break;

        default:
          break;
      }
      return platform;
    }

    private string ParseVgcPlatform(string platformId)
    {
      string platform = null;

      switch (platformId)
      {
        case "NX":
          platform = "nintendo_switch";
          break;

        case "NX2":
          platform = "nintendo_switch_2";
          break;

        default:
          break;
      }
      return platform;
    }

    private string FixGameName(string name)
    {
      var gameName = name.
          RemoveTrademarks(" ").
          NormalizeGameName().
          Replace("full game", "", StringComparison.OrdinalIgnoreCase).
          Trim();
      return Regex.Replace(gameName, @"\s+", " ");
    }

    private List<GameMetadata> parseGames(List<PurchasedList.Transaction> gamesToParse)
    {
      var parsedGames = new List<GameMetadata>();
      foreach (var title in gamesToParse)
      {
        string[] nonGameContentTypes = { "consumable", "service", "premium_ticket", "patch", "service_item" };
        string[] gameContentTypes = { "title", "bundle", "aoc" };

        if (!nonGameContentTypes.Any(s => title.content_type.Contains(s)))
        {
          var gameName = FixGameName(title.title);

          string platform = ParsePlatform(title.device_type);

          parsedGames.Add(new GameMetadata
          {
            GameId = title.transaction_id.ToString(),
            Name = gameName,
            Platforms = platform != null ? new HashSet<MetadataProperty> { new MetadataSpecProperty(platform) } : null
          });
        }
      }

      return parsedGames;
    }

    private List<GameMetadata> parseVirtualGameCards(List<VirtualGameCardsList.View> gamesToParse)
    {
      var parsedGames = new List<GameMetadata>();
      foreach (var title in gamesToParse)
      {
        var gameName = FixGameName(title.applicationName);

        string platform = ParseVgcPlatform(title.apparentPlatform);

        parsedGames.Add(new GameMetadata
        {
          GameId = title.applicationId,
          Name = gameName,
          Platforms = platform != null ? new HashSet<MetadataProperty> { new MetadataSpecProperty(platform) } : null
        });
      }

      return parsedGames;
    }


    private List<GameMetadata> ParsePlayedList(NintendoAccountClient clientApi)
    {
      var gamesToParse = clientApi.GetPurchasedList().GetAwaiter().GetResult();
      return parseGames(gamesToParse);
    }

    private List<GameMetadata> ParseVirtualGameCardsList(NintendoAccountClient clientApi)
    {
      var gamesToParse = clientApi.GetVirtualGameCardsList().GetAwaiter().GetResult();
      return parseVirtualGameCards(gamesToParse);
    }

    public override IEnumerable<Game> ImportGames(LibraryImportGamesArgs args)
    {
      var importedGames = new List<Game>();

      Exception importError = null;
      if (!SettingsViewModel.Settings.ConnectAccount)
      {
        return importedGames;
      }

      try
      {
        var clientApi = new NintendoAccountClient(this, PlayniteApi);
        var allGames = new List<GameMetadata>();
        allGames.AddRange(ParseVirtualGameCardsList(clientApi));

        if (SettingsViewModel.Settings.Migration) { MigrateGames.call(this, allGames); }

        // This need to happen to merge games from different APIs
        foreach (var group in allGames.GroupBy(a => a.GameId))
        {
          var game = group.First();
          if (PlayniteApi.ApplicationSettings.GetGameExcludedFromImport(game.GameId, Id))
          {
            continue;
          }

          var alreadyImported = PlayniteApi.Database.Games.FirstOrDefault(a => a.GameId == game.GameId && a.PluginId == Id);
          if (alreadyImported == null)
          {
            game.Source = new MetadataNameProperty("Nintendo");
            importedGames.Add(PlayniteApi.Database.ImportGame(game, this));
          }
        }
      }
      catch (Exception e) when (!Debugger.IsAttached)
      {
        Logger.Error(e, "Failed to import Nintendo games.");
        importError = e;
      }

      if (importError != null)
      {
        PlayniteApi.Notifications.Add(new NotificationMessage(
            ImportErrorMessageId,
            string.Format(PlayniteApi.Resources.GetString("LOCLibraryImportError"), Name) +
            System.Environment.NewLine + importError.Message,
            NotificationType.Error,
            () => OpenSettingsView()));
      }
      else
      {
        PlayniteApi.Notifications.Remove(ImportErrorMessageId);
      }

      return importedGames;
    }

    public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
    {
      if (args.Game.PluginId != Id)
      {
        yield break;
      }
      PlayniteApi.Dialogs.ShowMessage("This will NOT work.\n\r\n\rInstalling Nintendo games from the Nintendo library plugin is not supported. It is not possible to play Nintendo games this way; the Nintendo plugin is designed as a library management tool only.\n\r\n\rPlay this game via a console or an emulator instead.");
    }

  }
}
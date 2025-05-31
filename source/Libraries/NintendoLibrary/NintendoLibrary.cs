using NintendoLibrary.Models;
using NintendoLibrary.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

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

    private IEnumerable<MetadataProperty> GetPlatforms(VirtualGameCardsList.View game)
    {
      if (game.apparentPlatform == "NX" || game.hasNxApplication || game.hasNxAddOnContents)
        yield return new MetadataSpecProperty("nintendo_switch");

      if (game.apparentPlatform == "OUNCE" || game.hasOunceApplication || game.hasOunceAddOnContents)
        yield return new MetadataNameProperty("Nintendo Switch 2");
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

    private List<GameMetadata> parseVirtualGameCards(List<VirtualGameCardsList.View> gamesToParse)
    {
      var parsedGames = new List<GameMetadata>();
      foreach (var title in gamesToParse)
      {
        parsedGames.Add(new GameMetadata
        {
          GameId = title.applicationId,
          Name = FixGameName(title.applicationName),
          Platforms = GetPlatforms(title).ToHashSet(),
        });
      }

      return parsedGames;
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
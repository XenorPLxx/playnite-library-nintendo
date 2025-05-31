using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NintendoLibrary.Services
{
  internal class MigrateGames
  {
    public static void call(NintendoLibrary nintendoLibrary, List<GameMetadata> games)
    {
      nintendoLibrary.SettingsViewModel.BeginEdit();
      nintendoLibrary.SettingsViewModel.Settings.Migration = false;
      nintendoLibrary.SettingsViewModel.EndEdit();

      var pluginGames = nintendoLibrary.PlayniteApi.Database.Games.Where(x => x.PluginId == nintendoLibrary.Id);

      if (pluginGames.Count() > 0)
      {
        foreach (var game in pluginGames)
        {
          var name = game.Name;
          string newGameId = games.FirstOrDefault(
            c => Regex.Replace(
              Regex.Replace(
                c.Name.ToLower(),
                @"[^0-9a-zA-Z_]",
                string.Empty),
              @"The ",
              String.Empty) ==
              Regex.Replace(
                Regex.Replace(name.ToLower(), 
                @"[^0-9a-zA-Z_]", 
                string.Empty),
                @"The ",
                String.Empty))?.GameId 
            ?? null;
          if (newGameId != null && game.GameId != newGameId)
          {
            game.GameId = newGameId;
            nintendoLibrary.PlayniteApi.Database.Games.Update(game);
          }
        }
      }
    }
  }
}


using Playnite.SDK;
using NintendoLibrary.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NintendoLibrary
{
  public class NintendoLibrarySettings
  {
    public bool ConnectAccount { get; set; } = true;
    public bool ExcludeAddOnOnlyEntries { get; set; } = false;
    // Successful NAS mappings and English fallbacks, keyed by the full Nintendo portal locale.
    public Dictionary<string, string> NasLanguageMappings { get; set; } = new Dictionary<string, string>();
  }

  public class NintendoLibrarySettingsViewModel : PluginSettingsViewModel<NintendoLibrarySettings, NintendoLibrary>
  {
    private NintendoAccountClient clientApi;

    public bool IsUserLoggedIn
    {
      get
      {
        try
        {
          clientApi.CheckAuthentication().GetAwaiter().GetResult();
          return true;
        }
        catch
        {
          return false;
        }
      }
    }

    public RelayCommand<object> LoginCommand
    {
      get => new RelayCommand<object>((a) =>
      {
        Login();
      });
    }

    public NintendoLibrarySettingsViewModel(NintendoLibrary plugin, IPlayniteAPI api) : base(plugin, api)
    {
      var savedSettings = LoadSavedSettings();
      if (savedSettings != null)
      {
        Settings = savedSettings;
      }
      else
      {
        Settings = new NintendoLibrarySettings();
      }
      clientApi = new NintendoAccountClient(plugin, api);
    }

    internal void SaveNasLanguageMapping(string portalLanguage, string nasLanguage)
    {
      SetNasLanguageMapping(Settings, portalLanguage, nasLanguage);
      if (EditingClone != null)
      {
        SetNasLanguageMapping(EditingClone, portalLanguage, nasLanguage);
      }

      try
      {
        // Save the fallback without committing other edits in an open settings dialog.
        var savedSettings = LoadSavedSettings() ?? new NintendoLibrarySettings();
        SetNasLanguageMapping(savedSettings, portalLanguage, nasLanguage);
        Plugin.SavePluginSettings(savedSettings);
      }
      catch (Exception e)
      {
        Logger.Error(e, "Failed to save Nintendo NAS language fallback setting.");
      }
    }

    private static void SetNasLanguageMapping(NintendoLibrarySettings settings, string portalLanguage, string nasLanguage)
    {
      var mappings = settings.NasLanguageMappings == null
        ? new Dictionary<string, string>()
        : new Dictionary<string, string>(settings.NasLanguageMappings);
      mappings[portalLanguage] = nasLanguage;
      settings.NasLanguageMappings = mappings;
    }

    private void Login()
    {
      try
      {
        clientApi.Login();
        OnPropertyChanged(nameof(IsUserLoggedIn));
      }
      catch (Exception e) when (!Debugger.IsAttached)
      {
        Logger.Error(e, "Failed to authenticate user.");
      }
    }
  }
}

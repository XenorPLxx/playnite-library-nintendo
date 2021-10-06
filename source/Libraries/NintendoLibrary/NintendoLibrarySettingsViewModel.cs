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
            clientApi = new NintendoAccountClient(plugin, api);
            var savedSettings = LoadSavedSettings();
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new NintendoLibrarySettings();
            }
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
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Data;
using NintendoLibrary.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using System.Web;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Net.Http.Headers;
using System.Security;

namespace NintendoLibrary.Services
{
    public class ApiRedirectResponse
    {
        public string redirectUrl { get; set; }
        public string sid { get; set; }
    }
    public class NintendoAccountClient
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private IPlayniteAPI api;
        //private MobileTokens mobileToken;
        private readonly NintendoLibrary library;
        private readonly string tokenPath;
        private const int pageRequestLimit = 100;
        private const string loginUrl = @"https://accounts.nintendo.com/login?post_login_redirect_uri=https%3A%2F%2Fec.nintendo.com/my%2F";        
        private const string purchasesListUrl = "https://ec.nintendo.com/api/my/transactions?limit={0}&offset={1}";

        public NintendoAccountClient(NintendoLibrary library, IPlayniteAPI api)
        {
            this.library = library;
            this.api = api;
            tokenPath = Path.Combine(library.GetPluginUserDataPath(), "token.json");
        }

        public void Login()
        {
            var loggedIn = false;
            

            using (var view = api.WebViews.CreateView(580, 700))
            {
                view.LoadingChanged += (s, e) =>
                {
                    var address = view.GetCurrentAddress();
                    if (address.StartsWith(@"https://ec.nintendo.com"))
                    {
                        loggedIn = true;
                        view.Close();
                    }
                };

                view.DeleteDomainCookies("ec.nintendo.com");
                view.DeleteDomainCookies("accounts.nintendo.com");
                view.DeleteDomainCookies("api.accounts.nintendo.com");
                view.DeleteDomainCookies("api.ec.nintendo.com");
                view.DeleteDomainCookies("apps.accounts.nintendo.com");
                view.Navigate(loginUrl);
                view.OpenDialog();
            }

            if (!loggedIn)
            {
                return;
            }
                  
            dumpCookies();

            return;
        }
  
        private IEnumerable<Playnite.SDK.HttpCookie> dumpCookies()
        {
            var view = api.WebViews.CreateOffscreenView();

            var cookies = view.GetCookies(); 


            var cookieContainer = new CookieContainer();
            foreach (var cookie in cookies)
            {
                if (cookie.Domain == "ec.nintendo.com")
                {
                    cookieContainer.Add(new Uri("https://ec.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
                if (cookie.Domain == ".nintendo.com")
                {
                    cookieContainer.Add(new Uri("https://ec.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
                if (cookie.Domain == "accounts.nintendo.com")                {

                    cookieContainer.Add(new Uri("https://accounts.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
                if (cookie.Domain == "https://api.accounts.nintendo.com")
                {
                    cookieContainer.Add(new Uri("https://api.accounts.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
                if (cookie.Domain == "https://api.ec.nintendo.com")
                {
                    cookieContainer.Add(new Uri("https://api.ec.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
                if (cookie.Domain == "https://apps.accounts.nintendo.com")
                {
                    cookieContainer.Add(new Uri("https://apps.accounts.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
                }
            }

            WriteCookiesToDisk(cookieContainer);

            view.Dispose();
            return cookies;
        }

        private void WriteCookiesToDisk(CookieContainer cookieJar)
        {
            File.Delete(tokenPath);
            using (Stream stream = File.Create(tokenPath))
            {
                try
                {
                    Console.Out.Write("Writing cookies to disk... ");
                    BinaryFormatter formatter = new BinaryFormatter();
                    formatter.Serialize(stream, cookieJar);
                    Console.Out.WriteLine("Done.");
                }
                catch (Exception e)
                {
                    Console.Out.WriteLine("Problem writing cookies to disk: " + e.GetType());
                }
            }
        }

        private CookieContainer ReadCookiesFromDisk()
        {
            try
            {
                using (Stream stream = File.Open(tokenPath, FileMode.Open))
                {
                    Console.Out.Write("Reading cookies from disk... ");
                    BinaryFormatter formatter = new BinaryFormatter();
                    Console.Out.WriteLine("Done.");
                    return (CookieContainer)formatter.Deserialize(stream);
                }
            }
            catch (Exception e)
            {
                Console.Out.WriteLine("Problem reading cookies from disk: " + e.GetType());
                return new CookieContainer();
            }
        }

        public async Task CheckAuthentication()
        {
            if (!File.Exists(tokenPath))
            {
                throw new Exception("User is not authenticated.");
            }
            else
            {
                if (!await GetIsUserLoggedIn())
                {
                    TryRefreshCookies();
                    if (!await GetIsUserLoggedIn())
                    {
                        throw new Exception("User is not authenticated.");
                    }
                }
            }
        }        

        public async Task<List<PurchasedList.Transaction>> GetPurchasedList()
        {
            await CheckAuthentication();

            var titles = new List<PurchasedList.Transaction>();

            var cookieContainer = ReadCookiesFromDisk();
            using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
            using (var httpClient = new HttpClient(handler))
            {
                var itemCount = 0;
                var offset = -pageRequestLimit;

                do
                {
                    object[] args = { offset, pageRequestLimit };
                    var resp = await httpClient.GetAsync(purchasesListUrl.Format(pageRequestLimit, offset + pageRequestLimit));
                    var strResponse = await resp.Content.ReadAsStringAsync();
                    var titles_part = Serialization.FromJson<PurchasedList>(strResponse);
                    titles.AddRange(titles_part.transactions);
                    offset = titles_part.offset;
                    itemCount = titles_part.total;
                } while (offset + pageRequestLimit < itemCount);
            }

            return titles;
        }

        private void TryRefreshCookies()
        {
            string address;
            using (var webView = api.WebViews.CreateOffscreenView())
            {
                webView.LoadingChanged += (s, e) =>
                {
                    address = webView.GetCurrentAddress();
                    webView.Close();
                };

                webView.NavigateAndWait(loginUrl);
            }

            dumpCookies();
        }

        public async Task<bool> GetIsUserLoggedIn()
        {
            if (!File.Exists(tokenPath))
            {
                return false;
            }

            try
            {
                var cookieContainer = ReadCookiesFromDisk();
                using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
                using (var httpClient = new HttpClient(handler))
                {

                    var resp = httpClient.GetAsync(purchasesListUrl.Format(10, 0)).GetAwaiter().GetResult();
                    var strResponse = await resp.Content.ReadAsStringAsync();

                    if (Serialization.TryFromJson<AuthError>(strResponse, out var error) && error.error != null)
                    {
                        return false;
                    }

                    if (Serialization.TryFromJson<PurchasedList>(strResponse, out var purchasedList) && purchasedList?.transactions != null)
                    {
                        return true;
                    }

                }
                return false;
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to check if user is authenticated into Nintendo.");
                return false;
            }
        }
    }
}

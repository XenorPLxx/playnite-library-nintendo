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
using System.Web.UI.WebControls;
using System.Collections;
using System.Globalization;
using System.Net.Mime;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Metadata.W3cXsd2001;
using System.Security.Cryptography;
using System.Windows;

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
    private const int vgcPageRequestLimit = 300;
    private const string purchasesListUrl = "https://ec.nintendo.com/api/my/transactions?offset={1}&limit={0}";
    private const string vgcMainPageUrl = "https://accounts.nintendo.com/portal/vgcs/?sort=activated_date&order=desc";
    private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    public NintendoAccountClient(NintendoLibrary library, IPlayniteAPI api)
    {
      this.library = library;
      this.api = api;
      tokenPath = Path.Combine(library.GetPluginUserDataPath(), "token.json");
    }

    public void Login()
    {
      var loggedIn = false;


      WebViewSettings webViewSettings = new WebViewSettings();
      webViewSettings.WindowHeight = 800;
      webViewSettings.WindowWidth = 1100;
      webViewSettings.UserAgent = userAgent;

      using (var view = api.WebViews.CreateView(webViewSettings))
      {
        view.LoadingChanged += (s, e) =>
        {
          if (e.IsLoading)
            return;

          var address = view.GetCurrentAddress();
          if (address != null && address.StartsWith("https://accounts.nintendo.com/portal/vgcs", StringComparison.OrdinalIgnoreCase))
          {
            loggedIn = true;
            view.Close();
          }
        };

        view.DeleteDomainCookies(".nintendo.com");
        view.DeleteDomainCookies("ec.nintendo.com");
        view.DeleteDomainCookies("accounts.nintendo.com");
        view.DeleteDomainCookies("api.accounts.nintendo.com");
        view.DeleteDomainCookies("api.ec.nintendo.com");
        view.DeleteDomainCookies("apps.accounts.nintendo.com");
        view.Navigate(vgcMainPageUrl);
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
        if (cookie.Domain == "https://ec.nintendo.com")
        {
          cookieContainer.Add(new Uri("https://ec.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
        }
        if (cookie.Domain == ".nintendo.com")
        {
          cookieContainer.Add(new Uri("https://ec.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
          cookieContainer.Add(new Uri("https://accounts.nintendo.com"), new Cookie(cookie.Name, cookie.Value));
        }
        if (cookie.Domain == "accounts.nintendo.com")
        {

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

    public async Task CheckAuthentication(CancellationToken cancellationToken = default(CancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!File.Exists(tokenPath))
      {
        throw new Exception("User is not authenticated.");
      }
      else
      {
        if (!await GetIsUserLoggedIn(cancellationToken))
        {
          cancellationToken.ThrowIfCancellationRequested();
          TryRefreshCookies();
          if (!await GetIsUserLoggedIn(cancellationToken))
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

    public async Task<List<VirtualGameCardsList.View>> GetVirtualGameCardsList(CancellationToken cancellationToken = default(CancellationToken))
    {
      await CheckAuthentication(cancellationToken);

      var titles = new List<VirtualGameCardsList.View>();

      var cookieContainer = ReadCookiesFromDisk();
      using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
      using (var httpClient = new HttpClient(handler))
      {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        var itemCount = 0;
        var currentOffset = 0;
        var queryParamsObject = await GetVirtualGameCardsQueryParams(httpClient, cancellationToken);

        do
        {
          cancellationToken.ThrowIfCancellationRequested();
          var titles_part = await GetVirtualGameCardsPage(httpClient, queryParamsObject, currentOffset, cancellationToken);
          titles.AddRange(titles_part.data.account.vgc.vgcViews.views);
          currentOffset += vgcPageRequestLimit;
          itemCount = titles_part.data.account.vgc.vgcViews.offsetInfo.total;
        } while (currentOffset < itemCount);
      }
      return titles;
    }

    private async Task<VgcQueryParams> GetVirtualGameCardsQueryParams(HttpClient httpClient, CancellationToken cancellationToken)
    {
      string portalResponse;
      using (var response = await httpClient.GetAsync(vgcMainPageUrl, cancellationToken))
      {
        response.EnsureSuccessStatusCode();
        portalResponse = await response.Content.ReadAsStringAsync();
      }
      var match = Regex.Match(portalResponse, @"<div id=""data"" data-json=""(.*?)""");
      if (!match.Success)
      {
        throw new Exception("Nintendo Account portal did not return Virtual Game Cards query parameters.");
      }

      var queryParams = Serialization.FromJson<VgcQueryParams>(HttpUtility.HtmlDecode(match.Groups[1].Value));
      if (!HasVirtualGameCardsQueryParams(queryParams))
      {
        throw new Exception("Nintendo Account portal returned incomplete Virtual Game Cards query parameters.");
      }

      return queryParams;
    }

    private static bool HasVirtualGameCardsQueryParams(VgcQueryParams queryParams)
    {
      return queryParams != null && !string.IsNullOrEmpty(queryParams.idToken) &&
             !string.IsNullOrEmpty(queryParams.savannaClientId) && !string.IsNullOrEmpty(queryParams.shopGraphQLApiUrl);
    }

    private async Task<Vgc> GetVirtualGameCardsPage(HttpClient httpClient, VgcQueryParams queryParams, int offset, CancellationToken cancellationToken)
    {
      var queryObject = new
      {
        query = @"query getVgcs(
                  $idToken: String!
                  $country: CountryCode!
                  $language: LanguageCode!
                  $shopId: Int!
                  $limit: Int!
                  $nasLanguage: String!
                  $offset: Int!
                  $order: RequestableVgcViewOrder!
                  $sortBy: RequestableVgcViewSortBy!
                  $vgcViewType: VgcViewTypeInput
                  $vgcViewStatus: VgcViewStatusInput
                ) @inContext(country: $country, language: $language, shopId: $shopId) {
                  account {
                    vgc {
                      vgcViews(
                        idToken: $idToken,
                        limit: $limit,
                        nasLanguage: $nasLanguage,
                        offset: $offset,
                        order: $order,
                        sortBy: $sortBy,
                        isHidden: false,
                        vgcViewType: $vgcViewType,
                        vgcViewStatus: $vgcViewStatus,
                      ) {
                        offsetInfo {
                          total
                          offset
                        }
                        views {
                          id
                          applicationId
                          applicationName
                          apparentPlatform
                          publisher
                          icon {
                            url
                            upgradedIconUrl
                            sizes
                          }
                          ownerNaId
                          userNaId
                          isHidden
                          isLending
                          isPartialLending
                          lendingExpireDatetime
                          insertedNsDeviceId
                          hasApplication
                          hasAddOnContents
                          hasUpgrade
                          hasNxApplication
                          hasNxAddOnContents
                          hasOunceApplication
                          hasOunceAddOnContents
                          containsReleased
                        }
                      }
                    }
                  }
                }",
        variables = new
        {
          country = "GB",
          idToken = queryParams.idToken,
          language = "en",
          limit = vgcPageRequestLimit,
          nasLanguage = "en-GB",
          offset,
          order = "ASC",
          shopId = 3,
          sortBy = "ACTIVATED_DATE"
        }
      };

      using (var request = new HttpRequestMessage(HttpMethod.Post, queryParams.shopGraphQLApiUrl))
      {
        request.Content = new StringContent(Serialization.ToJson(queryObject), Encoding.UTF8, "application/json");
        request.Headers.Add("x-nintendo-savanna-client-id", queryParams.savannaClientId);

        using (var response = await httpClient.SendAsync(request, cancellationToken))
        {
          if (!response.IsSuccessStatusCode)
          {
            throw new Exception($"Nintendo Virtual Game Cards request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
          }

          var vgc = Serialization.FromJson<Vgc>(await response.Content.ReadAsStringAsync());
          if (vgc?.errors?.Count > 0)
          {
            throw new Exception("Nintendo Virtual Game Cards request failed: " + vgc.errors.FirstOrDefault()?.message);
          }

          if (vgc?.data?.account?.vgc?.vgcViews?.offsetInfo == null || vgc.data.account.vgc.vgcViews.views == null)
          {
            throw new Exception("Nintendo Virtual Game Cards request returned an incomplete response.");
          }

          return vgc;
        }
      }
    }

    private async Task<bool> CheckVirtualGameCardsAuthentication(HttpClient httpClient, VgcQueryParams queryParams, CancellationToken cancellationToken)
    {
      if (!HasVirtualGameCardsQueryParams(queryParams))
      {
        return false;
      }

      var queryObject = new
      {
        query = @"query checkVgcs(
                    $idToken: String!
                    $country: CountryCode!
                    $language: LanguageCode!
                    $shopId: Int!
                    $limit: Int!
                    $nasLanguage: String!
                    $offset: Int!
                    $order: RequestableVgcViewOrder!
                    $sortBy: RequestableVgcViewSortBy!
                    $vgcViewType: VgcViewTypeInput
                    $vgcViewStatus: VgcViewStatusInput
                  ) @inContext(country: $country, language: $language, shopId: $shopId) {
                    account {
                      vgc {
                        vgcViews(
                          idToken: $idToken,
                          limit: $limit,
                          nasLanguage: $nasLanguage,
                          offset: $offset,
                          order: $order,
                          sortBy: $sortBy,
                          isHidden: false,
                          vgcViewType: $vgcViewType,
                          vgcViewStatus: $vgcViewStatus,
                        ) {
                          offsetInfo {
                            total
                          }
                        }
                      }
                    }
                  }",
        variables = new
        {
          country = "GB",
          idToken = queryParams.idToken,
          language = "en",
          limit = 1,
          nasLanguage = "en-GB",
          offset = 0,
          order = "ASC",
          shopId = 3,
          sortBy = "ACTIVATED_DATE"
        }
      };

      using (var request = new HttpRequestMessage(HttpMethod.Post, queryParams.shopGraphQLApiUrl))
      {
        request.Content = new StringContent(Serialization.ToJson(queryObject), Encoding.UTF8, "application/json");
        request.Headers.Add("x-nintendo-savanna-client-id", queryParams.savannaClientId);

        using (var response = await httpClient.SendAsync(request, cancellationToken))
        {
          if (!response.IsSuccessStatusCode)
          {
            return false;
          }

          var vgc = Serialization.FromJson<Vgc>(await response.Content.ReadAsStringAsync());
          return vgc?.data?.account?.vgc?.vgcViews?.offsetInfo != null;
        }
      }
    }

    private void TryRefreshCookies()
    {
      using (var webView = api.WebViews.CreateOffscreenView())
      {
        webView.NavigateAndWait(vgcMainPageUrl);
      }
      dumpCookies();
    }
    public async Task<bool> GetIsUserLoggedIn(CancellationToken cancellationToken = default(CancellationToken))
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
          httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
          var queryParams = await GetVirtualGameCardsQueryParams(httpClient, cancellationToken);
          return await CheckVirtualGameCardsAuthentication(httpClient, queryParams, cancellationToken);
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception e) when (!Debugger.IsAttached)
      {
        logger.Error(e, "Failed to check if user is authenticated into Nintendo.");
        return false;
      }
    }
  }
}

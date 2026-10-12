using NintendoLibrary.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
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
using System.Web;
using System.Net;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;

namespace NintendoLibrary.Services
{
  public class NintendoAccountClient
  {
    private static readonly ILogger logger = LogManager.GetLogger();
    private static readonly Uri[] cookieUris =
    {
      new Uri("https://ec.nintendo.com"),
      new Uri("https://accounts.nintendo.com"),
      new Uri("https://api.accounts.nintendo.com"),
      new Uri("https://api.ec.nintendo.com"),
      new Uri("https://apps.accounts.nintendo.com")
    };
    private static readonly byte[] cookieEncryptionEntropy = Encoding.UTF8.GetBytes("NintendoLibrary.CookieStore.v1");
    private static readonly HttpClient nintendoScriptHttpClient = new HttpClient(new HttpClientHandler { UseCookies = false });
    private static readonly SemaphoreSlim nasLanguageMappingLock = new SemaphoreSlim(1, 1);
    private static Dictionary<string, string> cachedNasLanguageMappings;
    private static DateTime nasLanguageMappingsExpireAt;
    private const string nintendoCommonScriptUrl = "https://cdn.accounts.nintendo.com/account/js/common.js";

    private readonly NintendoLibrary library;
    private readonly HttpClient scriptHttpClient;
    private readonly IPlayniteAPI api;
    private readonly string cookiesPath;
    private readonly string legacyTokenPath;
    private const int vgcPageRequestLimit = 300;
    // Nintendo's portal uses this fixed shop context for virtual game cards.
    private const int vgcOffDeviceShopId = 3;
    private const string vgcMainPageUrl = "https://accounts.nintendo.com/portal/vgcs/?sort=activated_date&order=desc";
    private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    public NintendoAccountClient(NintendoLibrary library, IPlayniteAPI api) : this(library, api, nintendoScriptHttpClient)
    {
    }

    internal NintendoAccountClient(NintendoLibrary library, IPlayniteAPI api, HttpClient scriptHttpClient)
    {
      this.library = library;
      this.api = api;
      this.scriptHttpClient = scriptHttpClient;
      cookiesPath = Path.Combine(library.GetPluginUserDataPath(), "cookies.dat");
      legacyTokenPath = Path.Combine(library.GetPluginUserDataPath(), "token.json");
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

      if (WriteCookiesToDisk(cookieContainer) && File.Exists(legacyTokenPath))
      {
        File.Delete(legacyTokenPath);
      }

      view.Dispose();
      return cookies;
    }

    private bool WriteCookiesToDisk(CookieContainer cookieJar)
    {
      var temporaryCookiesPath = cookiesPath + ".tmp";
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(cookiesPath));
        var storedCookies = GetStoredCookies(cookieJar);
        var encryptedCookies = ProtectedData.Protect(
          Encoding.UTF8.GetBytes(Serialization.ToJson(storedCookies)),
          cookieEncryptionEntropy,
          DataProtectionScope.CurrentUser);
        File.WriteAllBytes(temporaryCookiesPath, encryptedCookies);
        File.Copy(temporaryCookiesPath, cookiesPath, true);
        return true;
      }
      catch (Exception e)
      {
        logger.Error(e, "Failed to save Nintendo authentication cookies.");
        return false;
      }
      finally
      {
        if (File.Exists(temporaryCookiesPath))
        {
          File.Delete(temporaryCookiesPath);
        }
      }
    }

    private CookieContainer ReadCookiesFromDisk()
    {
      if (File.Exists(cookiesPath))
      {
        try
        {
          var decryptedCookies = ProtectedData.Unprotect(
            File.ReadAllBytes(cookiesPath),
            cookieEncryptionEntropy,
            DataProtectionScope.CurrentUser);
          var storedCookies = Serialization.FromJson<List<StoredCookie>>(Encoding.UTF8.GetString(decryptedCookies));
          return CreateCookieContainer(storedCookies);
        }
        catch (Exception e)
        {
          logger.Error(e, "Failed to load saved Nintendo authentication cookies.");
        }
      }

      var legacyCookies = ReadLegacyCookiesFromDisk();
      if (legacyCookies != null)
      {
        if (WriteCookiesToDisk(legacyCookies))
        {
          File.Delete(legacyTokenPath);
        }

        return legacyCookies;
      }

      return new CookieContainer();
    }

    private CookieContainer ReadLegacyCookiesFromDisk()
    {
      if (!File.Exists(legacyTokenPath))
      {
        return null;
      }

      try
      {
        using (var stream = File.Open(legacyTokenPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
          var formatter = new BinaryFormatter();
          return formatter.Deserialize(stream) as CookieContainer;
        }
      }
      catch (Exception e)
      {
        logger.Error(e, "Failed to import legacy Nintendo authentication cookies.");
        return null;
      }
    }

    private static List<StoredCookie> GetStoredCookies(CookieContainer cookieJar)
    {
      var cookies = new List<StoredCookie>();
      var cookieKeys = new HashSet<string>(StringComparer.Ordinal);

      foreach (var uri in cookieUris)
      {
        foreach (Cookie cookie in cookieJar.GetCookies(uri))
        {
          var key = string.Join("\n", cookie.Domain, cookie.Path, cookie.Name);
          if (!cookieKeys.Add(key))
          {
            continue;
          }

          cookies.Add(new StoredCookie
          {
            Domain = cookie.Domain,
            Path = cookie.Path,
            Name = cookie.Name,
            Value = cookie.Value,
            Expires = cookie.Expires == DateTime.MinValue ? null : (DateTime?)cookie.Expires,
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly
          });
        }
      }

      return cookies;
    }

    private static CookieContainer CreateCookieContainer(IEnumerable<StoredCookie> storedCookies)
    {
      var cookieContainer = new CookieContainer();
      if (storedCookies == null)
      {
        return cookieContainer;
      }

      foreach (var storedCookie in storedCookies)
      {
        if (string.IsNullOrEmpty(storedCookie?.Name) || string.IsNullOrEmpty(storedCookie.Domain))
        {
          continue;
        }

        try
        {
          var cookie = new Cookie(
            storedCookie.Name,
            storedCookie.Value ?? string.Empty,
            string.IsNullOrEmpty(storedCookie.Path) ? "/" : storedCookie.Path,
            storedCookie.Domain)
          {
            Secure = storedCookie.Secure,
            HttpOnly = storedCookie.HttpOnly
          };
          if (storedCookie.Expires.HasValue)
          {
            cookie.Expires = storedCookie.Expires.Value;
          }

          cookieContainer.Add(cookie);
        }
        catch (CookieException e)
        {
          logger.Warn(e, "Skipping an invalid saved Nintendo authentication cookie.");
        }
      }

      return cookieContainer;
    }

    private bool HasSavedCookies()
    {
      return File.Exists(cookiesPath) || File.Exists(legacyTokenPath);
    }

    private class StoredCookie
    {
      public string Domain { get; set; }
      public string Path { get; set; }
      public string Name { get; set; }
      public string Value { get; set; }
      public DateTime? Expires { get; set; }
      public bool Secure { get; set; }
      public bool HttpOnly { get; set; }
    }

    public async Task CheckAuthentication(CancellationToken cancellationToken = default(CancellationToken))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!HasSavedCookies())
      {
        throw new Exception("User is not authenticated.");
      }
      try
      {
        if (await CheckSavedAuthentication(cancellationToken))
        {
          return;
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception e) when (!Debugger.IsAttached)
      {
        logger.Error(e, "Failed to check if user is authenticated into Nintendo.");
      }

      cancellationToken.ThrowIfCancellationRequested();
      TryRefreshCookies();
      // Preserve Nintendo's error if refreshing the session does not resolve it.
      if (!await CheckSavedAuthentication(cancellationToken))
      {
        throw new Exception("User is not authenticated.");
      }
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
      var queryParams = GetPortalData<VgcQueryParams>(portalResponse, "data");
      var portalMeta = GetPortalData<VgcPortalMeta>(portalResponse, "meta");
      var portalState = GetPortalData<VgcPortalState>(portalResponse, "state");
      var country = portalMeta?.countries?.FirstOrDefault(a => a.id == portalState?.user?.countryId);

      if (!HasVirtualGameCardsBaseQueryParams(queryParams) || country == null || string.IsNullOrWhiteSpace(country.code) || string.IsNullOrWhiteSpace(portalState?.lang) || portalState.lang.Length < 2)
      {
        throw new Exception("Nintendo Account portal returned incomplete Virtual Game Cards locale information.");
      }

      queryParams.countryCode = country.code;
      queryParams.languageCode = portalState.lang.Substring(0, 2);
      queryParams.portalLanguage = portalState.lang;
      queryParams.nasLanguage = GetNasLanguage(portalState.lang) ?? portalState.lang;
      queryParams.shopId = vgcOffDeviceShopId;
      if (!HasVirtualGameCardsQueryParams(queryParams))
      {
        throw new Exception("Nintendo Account portal returned incomplete Virtual Game Cards query parameters.");
      }

      return queryParams;
    }

    private static string GetNasLanguage(string portalLanguage)
    {
      // Matches Nintendo's portal mapping in account/js/common.js.
      switch (portalLanguage.ToLowerInvariant())
      {
        case "en-us": return "en-US";
        case "en-gb": return "en-GB";
        case "de": case "de-de": return "de";
        case "nl": case "nl-nl": return "nl";
        case "ja": case "ja-jp": return "ja";
        case "fr": case "fr-fr": return "fr";
        case "es": case "es-es": return "es";
        case "it": case "it-it": return "it";
        case "pt": case "pt-pt": return "pt";
        case "ru": case "ru-ru": return "ru";
        case "ko": case "ko-kr": return "ko";
        case "es-mx": return "es-419";
        case "fr-ca": return "fr-CA";
        case "pt-br": return "pt-BR";
        case "zh-tw": return "zh-Hant";
        default: return null;
      }
    }

    private static Dictionary<string, string> ParseNasLanguageMappings(string javascript)
    {
      // Read only flat locale-to-locale objects. Never execute downloaded JavaScript.
      const string locale = @"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,3}";
      var pair = @"[""'](?<portal>" + locale + @")[""']\s*:\s*[""'](?<nas>" + locale + @")[""']";
      var table = @"\{\s*" + pair + @"(?:\s*,\s*" + pair + @")*\s*,?\s*\}";
      Dictionary<string, string> result = null;
      foreach (Match match in Regex.Matches(javascript, table, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
      {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = match.Groups["portal"].Captures;
        var values = match.Groups["nas"].Captures;
        for (var i = 0; i < keys.Count; i++)
        {
          if (mapping.ContainsKey(keys[i].Value))
          {
            mapping.Clear();
            break;
          }
          mapping.Add(keys[i].Value, values[i].Value);
        }

        // Recognize the NAS table among the bundle's many translation objects.
        if (!mapping.TryGetValue("en-US", out var us) || us != "en-US" ||
            !mapping.TryGetValue("en-GB", out var gb) || gb != "en-GB" ||
            !mapping.ContainsKey("ja-JP") || !mapping.ContainsKey("de-DE"))
        {
          continue;
        }
        if (result != null && (result.Count != mapping.Count || result.Any(a => !mapping.TryGetValue(a.Key, out var value) || value != a.Value)))
        {
          return null;
        }
        result = mapping;
      }
      return result;
    }

    private async Task<string> GetNasLanguageFromNintendoScript(string portalLanguage, CancellationToken cancellationToken)
    {
      await nasLanguageMappingLock.WaitAsync(cancellationToken);
      try
      {
        if (cachedNasLanguageMappings == null || DateTime.UtcNow >= nasLanguageMappingsExpireAt)
        {
          Dictionary<string, string> mappings = null;
          try
          {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
              timeout.CancelAfter(TimeSpan.FromSeconds(10));
              using (var response = await scriptHttpClient.GetAsync(nintendoCommonScriptUrl, timeout.Token))
              {
                response.EnsureSuccessStatusCode();
                mappings = ParseNasLanguageMappings(await response.Content.ReadAsStringAsync());
              }
            }
            if (mappings == null)
            {
              logger.Warn("Nintendo JavaScript did not contain a recognized NAS language mapping; continuing with locale fallbacks.");
            }
          }
          catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
          {
            throw;
          }
          catch (Exception e)
          {
            logger.Warn(e, "Could not load Nintendo's NAS language mapping; continuing with locale fallbacks.");
          }
          // Cache failures too, so an unavailable CDN is not queried on every check.
          cachedNasLanguageMappings = mappings ?? new Dictionary<string, string>();
          nasLanguageMappingsExpireAt = DateTime.UtcNow.AddHours(1);
        }
        return cachedNasLanguageMappings.TryGetValue(portalLanguage, out var nasLanguage) ? nasLanguage : null;
      }
      finally
      {
        nasLanguageMappingLock.Release();
      }
    }

    private static T GetPortalData<T>(string portalResponse, string elementId) where T : class
    {
      var match = Regex.Match(portalResponse, $@"<div id=""{elementId}"" data-json=""(.*?)""");
      return match.Success ? Serialization.FromJson<T>(HttpUtility.HtmlDecode(match.Groups[1].Value)) : null;
    }

    private static bool HasVirtualGameCardsQueryParams(VgcQueryParams queryParams)
    {
      return HasVirtualGameCardsBaseQueryParams(queryParams) &&
             !string.IsNullOrEmpty(queryParams.countryCode) && !string.IsNullOrEmpty(queryParams.languageCode) &&
             !string.IsNullOrEmpty(queryParams.nasLanguage) && queryParams.shopId > 0;
    }

    private static bool HasVirtualGameCardsBaseQueryParams(VgcQueryParams queryParams)
    {
      return queryParams != null && !string.IsNullOrEmpty(queryParams.idToken) &&
             !string.IsNullOrEmpty(queryParams.savannaClientId) && !string.IsNullOrEmpty(queryParams.shopGraphQLApiUrl);
    }

    private async Task<Vgc> GetVirtualGameCardsPage(HttpClient httpClient, VgcQueryParams queryParams, int offset, CancellationToken cancellationToken, int limit = vgcPageRequestLimit)
    {
      var settings = library.SettingsViewModel.Settings;
      var portalLanguage = queryParams.portalLanguage ?? queryParams.nasLanguage;
      string savedNasLanguage = null;
      if (settings.NasLanguageMappings != null)
      {
        settings.NasLanguageMappings.TryGetValue(portalLanguage, out savedNasLanguage);
      }
      if (!string.IsNullOrEmpty(savedNasLanguage))
      {
        queryParams.nasLanguage = savedNasLanguage;
      }

      var originalNasLanguage = queryParams.nasLanguage;
      var checkedJavascript = false;
      string javascriptNasLanguage = null;
      var firstNasLanguage = originalNasLanguage;
      if (string.IsNullOrEmpty(savedNasLanguage) && GetNasLanguage(portalLanguage) == null)
      {
        javascriptNasLanguage = await GetNasLanguageFromNintendoScript(portalLanguage, cancellationToken);
        checkedJavascript = true;
        firstNasLanguage = javascriptNasLanguage ?? "en-GB";
      }

      var candidates = new Queue<string>();
      candidates.Enqueue(firstNasLanguage);
      var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var queuedEnglishFallbacks = false;
      var requestErrors = new List<string>();
      VirtualGameCardsRequestException lastError = null;
      while (candidates.Count > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var nasLanguage = candidates.Dequeue();
        if (!attempted.Add(nasLanguage))
        {
          continue;
        }
        var requestParams = new VgcQueryParams
        {
          countryCode = queryParams.countryCode,
          languageCode = queryParams.languageCode,
          nasLanguage = nasLanguage,
          shopId = queryParams.shopId,
          idToken = queryParams.idToken,
          savannaClientId = queryParams.savannaClientId,
          shopGraphQLApiUrl = queryParams.shopGraphQLApiUrl
        };

        try
        {
          var vgc = await SendVirtualGameCardsPage(httpClient, requestParams, offset, cancellationToken, limit);
          queryParams.nasLanguage = nasLanguage;
          if (string.Equals(nasLanguage, javascriptNasLanguage, StringComparison.OrdinalIgnoreCase))
          {
            logger.Info($"Nintendo Virtual Game Cards request succeeded using the JavaScript mapping for portalLanguage={portalLanguage}, nasLanguage={nasLanguage}.");
            library.SettingsViewModel.SaveNasLanguageMapping(portalLanguage, nasLanguage);
          }
          else if (!string.Equals(nasLanguage, originalNasLanguage, StringComparison.OrdinalIgnoreCase))
          {
            logger.Warn($"Nintendo Virtual Game Cards request succeeded using English fallback for country={queryParams.countryCode}, " +
              $"originalLanguage={originalNasLanguage}, nasLanguage={nasLanguage}.");
            library.SettingsViewModel.SaveNasLanguageMapping(portalLanguage, nasLanguage);
          }
          return vgc;
        }
        catch (VirtualGameCardsRequestException e)
        {
          requestErrors.Add(e.Message);
          lastError = e;
          if (!e.CanRetryInEnglish)
          {
            if (requestErrors.Count == 1)
            {
              throw;
            }
            break;
          }
          if (!checkedJavascript)
          {
            javascriptNasLanguage = await GetNasLanguageFromNintendoScript(portalLanguage, cancellationToken);
            checkedJavascript = true;
            if (!string.IsNullOrEmpty(javascriptNasLanguage))
            {
              candidates.Enqueue(javascriptNasLanguage);
            }
          }
          if (!queuedEnglishFallbacks)
          {
            candidates.Enqueue("en-GB");
            candidates.Enqueue("en-US");
            queuedEnglishFallbacks = true;
          }
        }
      }

      throw new Exception(string.Join(Environment.NewLine + "Locale retry also failed: ", requestErrors), lastError);
    }

    private class VirtualGameCardsRequestException : Exception
    {
      public bool CanRetryInEnglish { get; }

      public VirtualGameCardsRequestException(string message, bool canRetryInEnglish) : base(message)
      {
        CanRetryInEnglish = canRetryInEnglish;
      }
    }

    private async Task<Vgc> SendVirtualGameCardsPage(HttpClient httpClient, VgcQueryParams queryParams, int offset, CancellationToken cancellationToken, int limit)
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
                      vgcViews: vgcViewsV2(
                        idToken: $idToken,
                        limit: $limit,
                        language: $nasLanguage,
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
                          hasApplication: hasReleasedApplication
                          hasAddOnContents: hasReleasedAddOnContents
                          hasUpgrade: hasReleasedUpgrade
                          hasNxApplication: hasReleasedNxApplication
                          hasNxAddOnContents: hasReleasedNxAddOnContents
                          hasOunceApplication: hasReleasedOunceApplication
                          hasOunceAddOnContents: hasReleasedOunceAddOnContents
                          containsReleased
                        }
                      }
                    }
                  }
                }",
        variables = new
        {
          country = queryParams.countryCode,
          idToken = queryParams.idToken,
          language = queryParams.languageCode,
          limit,
          nasLanguage = queryParams.nasLanguage,
          offset,
          order = "ASC",
          shopId = queryParams.shopId,
          sortBy = "ACTIVATED_DATE"
        }
      };

      using (var request = new HttpRequestMessage(HttpMethod.Post, queryParams.shopGraphQLApiUrl))
      {
        request.Content = new StringContent(Serialization.ToJson(queryObject), Encoding.UTF8, "application/json");
        request.Headers.Add("x-nintendo-savanna-client-id", queryParams.savannaClientId);

        using (var response = await httpClient.SendAsync(request, cancellationToken))
        {
          string error;
          var canRetryInEnglish = false;
          if (!response.IsSuccessStatusCode)
          {
            error = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
          }
          else
          {
            var vgc = Serialization.FromJson<Vgc>(await response.Content.ReadAsStringAsync());
            if (vgc?.errors?.Count > 0)
            {
              canRetryInEnglish = vgc.errors.All(a => string.Equals(a.message, "error", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(a.extensions?.code));
              error = string.Join("; ", vgc.errors.Select(a => string.IsNullOrEmpty(a.extensions?.code)
                ? a.message
                : $"{a.message} (code: {a.extensions.code})"));
            }
            else if (vgc?.data?.account?.vgc?.vgcViews?.offsetInfo == null || vgc.data.account.vgc.vgcViews.views == null)
            {
              error = "Nintendo returned an incomplete response.";
            }
            else
            {
              return vgc;
            }
          }

          var requestDetails = $"HTTP {(int)response.StatusCode}, " +
            $"country={queryParams.countryCode}, language={queryParams.languageCode}, nasLanguage={queryParams.nasLanguage}, " +
            $"shopId={queryParams.shopId}, offset={offset}, limit={limit}.";
          var errorMessage = "Nintendo Virtual Game Cards request failed: " + error +
            Environment.NewLine + "Request details: " + requestDetails;
          logger.Warn(errorMessage);
          throw new VirtualGameCardsRequestException(errorMessage, canRetryInEnglish);
        }
      }
    }

    private async Task<bool> CheckVirtualGameCardsAuthentication(HttpClient httpClient, VgcQueryParams queryParams, CancellationToken cancellationToken)
    {
      if (!HasVirtualGameCardsQueryParams(queryParams))
      {
        return false;
      }

      await GetVirtualGameCardsPage(httpClient, queryParams, 0, cancellationToken, 1);
      return true;
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
      if (!HasSavedCookies())
      {
        return false;
      }
      try
      {
        return await CheckSavedAuthentication(cancellationToken);
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

    private async Task<bool> CheckSavedAuthentication(CancellationToken cancellationToken)
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
  }
}

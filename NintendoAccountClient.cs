using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Playnite;
using Playnite.WebViews;

namespace NintendoLibrary;

public sealed class NintendoAccountClient
{
    private static readonly ILogger logger = LogManager.GetLogger<NintendoAccountClient>();

    private const string VirtualGameCardsUrl = "https://accounts.nintendo.com/portal/vgcs/?sort=activated_date&order=desc";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";
    private const int PageSize = 300;
    private const int OffDeviceShopId = 3;
    private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string NintendoCommonScriptUrl = "https://cdn.accounts.nintendo.com/account/js/common.js";
    private static readonly HttpClient nintendoScriptHttpClient = new(new HttpClientHandler { UseCookies = false });
    private static readonly SemaphoreSlim nasLanguageMappingLock = new(1, 1);
    private static Dictionary<string, string>? cachedNasLanguageMappings;
    private static DateTime nasLanguageMappingsExpireAt;

    private readonly NintendoLibraryPlugin plugin;
    private readonly IPlayniteApi playniteApi;
    private readonly HttpClient scriptHttpClient;

    public NintendoAccountClient(NintendoLibraryPlugin plugin) : this(plugin, nintendoScriptHttpClient)
    {
    }

    internal NintendoAccountClient(NintendoLibraryPlugin plugin, HttpClient scriptHttpClient)
    {
        this.plugin = plugin;
        playniteApi = plugin.PlayniteApi;
        this.scriptHttpClient = scriptHttpClient;
    }

    public async Task LoginAsync()
    {
        using var view = playniteApi.WebView.CreateView(new WebViewSettings
        {
            WindowWidth = 1100,
            WindowHeight = 800,
            UserAgent = UserAgent
        });

        var signedIn = false;
        List<HttpCookie> previousCookies = [];
        view.LoadingChangedCallbackAsync = async args =>
        {
            // Only act once the page has finished loading; at navigation start the address is
            // already the destination and the dialog would close before sign-in completes.
            if (args.IsLoading)
            {
                return;
            }

            if (view.GetCurrentAddress()?.StartsWith("https://accounts.nintendo.com/portal/vgcs", StringComparison.OrdinalIgnoreCase) == true)
            {
                signedIn = true;
                view.Close();
            }

            await Task.CompletedTask;
        };
        view.WebViewInitializedCallbackAsync = async _ =>
        {
            previousCookies = await GetNintendoCookiesAsync(view);
            await view.DeleteDomainCookiesAsync(".nintendo.com");
            await view.DeleteDomainCookiesAsync("ec.nintendo.com");
            await view.DeleteDomainCookiesAsync("accounts.nintendo.com");
            // Host-only cookies on these subdomains are not covered by ".nintendo.com".
            await view.DeleteDomainCookiesAsync("api.accounts.nintendo.com");
            await view.DeleteDomainCookiesAsync("api.ec.nintendo.com");
            await view.DeleteDomainCookiesAsync("apps.accounts.nintendo.com");
            view.Navigate(VirtualGameCardsUrl);
        };

        await view.OpenDialogAsync();
        if (!signedIn)
        {
            // Sign-in was abandoned, so the session that was cleared to make room for it is restored.
            await RestoreCookiesAsync(view, previousCookies);
        }
    }

    private static async Task<List<HttpCookie>> GetNintendoCookiesAsync(IWebView view)
    {
        try
        {
            var cookies = await view.GetCookiesAsync();
            return cookies?
                .Where(cookie => cookie.Domain?.Contains("nintendo.com", StringComparison.OrdinalIgnoreCase) == true)
                .ToList() ?? [];
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to read the existing Nintendo session.");
            return [];
        }
    }

    private static async Task RestoreCookiesAsync(IWebView view, List<HttpCookie> cookies)
    {
        foreach (var cookie in cookies)
        {
            try
            {
                var host = cookie.Domain!.TrimStart('.');
                await view.SetCookieAsync($"https://{host}{cookie.Path}", cookie);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to restore the Nintendo cookie '{cookie.Name}'.");
            }
        }
    }

    public async Task<bool> GetIsUserLoggedInAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The portal renders even with an expired token, so authentication is only proven by a
            // request that the token actually has to satisfy.
            await GetVirtualGameCardsAsync(cancellationToken, 1);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.Info($"Nintendo authentication check failed: {e.Message}");
            return false;
        }
    }

    /// <param name="maxCards">
    /// Stops once this many cards have been read. Used by the authentication check, which only needs
    /// to prove that the account's token still satisfies a real request.
    /// </param>
    public async Task<List<VirtualGameCard>> GetVirtualGameCardsAsync(CancellationToken cancellationToken, int? maxCards = null)
    {
        using var view = playniteApi.WebView.CreateOffscreenView(new WebViewSettings { UserAgent = UserAgent });
        await view.OpenAsync().WaitAsync(cancellationToken);
        await view.NavigateAndWaitAsync(VirtualGameCardsUrl, TimeSpan.FromSeconds(20)).WaitAsync(cancellationToken);
        if (view.GetCurrentAddress()?.StartsWith("https://accounts.nintendo.com/portal/vgcs", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException("User is not authenticated with Nintendo.");
        }

        var portalData = await view.GetPageSourceAsync().WaitAsync(cancellationToken);
        var queryParameters = GetQueryParameters(portalData);
        using var client = CreateHttpClient(await view.GetCookiesAsync());

        var cards = new List<VirtualGameCard>();
        var offset = 0;
        var total = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = maxCards.HasValue ? Math.Min(PageSize, maxCards.Value - cards.Count) : PageSize;
            var page = await GetVirtualGameCardsPageAsync(client, queryParameters, offset, cancellationToken, limit);
            var views = page.Data?.Account?.Vgc?.VgcViews;
            if (views?.Views == null || views.OffsetInfo == null)
            {
                throw new InvalidOperationException(
                    "The Nintendo virtual game card request returned an incomplete response.");
            }

            cards.AddRange(views.Views);
            total = views.OffsetInfo.Total;
            if (views.Views.Count == 0 || (maxCards.HasValue && cards.Count >= maxCards.Value))
            {
                break;
            }

            offset += views.Views.Count;
        } while (offset < total);

        return cards;
    }

    private static HttpClient CreateHttpClient(IEnumerable<HttpCookie> webViewCookies)
    {
        var cookieContainer = new CookieContainer();
        foreach (var webViewCookie in webViewCookies)
        {
            if (string.IsNullOrWhiteSpace(webViewCookie.Name) || string.IsNullOrWhiteSpace(webViewCookie.Domain))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(
                    webViewCookie.Name,
                    webViewCookie.Value ?? string.Empty,
                    string.IsNullOrWhiteSpace(webViewCookie.Path) ? "/" : webViewCookie.Path,
                    webViewCookie.Domain)
                {
                    Secure = webViewCookie.Secure,
                    HttpOnly = webViewCookie.HttpOnly
                };
                cookieContainer.Add(cookie);
            }
            catch (CookieException)
            {
                // Ignore cookies outside the domains accepted by CookieContainer.
            }
        }

        var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static VgcQueryParameters GetQueryParameters(string portalSource)
    {
        var parameters = GetPortalData<VgcQueryParameters>(portalSource, "data");
        var meta = GetPortalData<VgcPortalMeta>(portalSource, "meta");
        var state = GetPortalData<VgcPortalState>(portalSource, "state");
        var country = meta?.Countries?.FirstOrDefault(item => item.Id == state?.User?.CountryId);
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.IdToken) || string.IsNullOrWhiteSpace(parameters.SavannaClientId) ||
            string.IsNullOrWhiteSpace(parameters.ShopGraphQlApiUrl) || string.IsNullOrWhiteSpace(country?.Code) ||
            string.IsNullOrWhiteSpace(state?.Lang) || state.Lang.Length < 2)
        {
            throw new InvalidOperationException("Nintendo Account did not return complete Virtual Game Cards data.");
        }

        parameters.CountryCode = country.Code;
        parameters.LanguageCode = state.Lang[..2];
        parameters.PortalLanguage = state.Lang;
        parameters.NasLanguage = GetNasLanguage(state.Lang) ?? state.Lang;
        parameters.ShopId = OffDeviceShopId;
        return parameters;
    }

    private static string? GetNasLanguage(string portalLanguage)
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

    private static Dictionary<string, string>? ParseNasLanguageMappings(string javascript)
    {
        // Read only flat locale-to-locale objects. Never execute downloaded JavaScript.
        const string locale = @"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,3}";
        var pair = @"[""'](?<portal>" + locale + @")[""']\s*:\s*[""'](?<nas>" + locale + @")[""']";
        var table = @"\{\s*" + pair + @"(?:\s*,\s*" + pair + @")*\s*,?\s*\}";
        Dictionary<string, string>? result = null;
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

    private async Task<string?> GetNasLanguageFromNintendoScript(string portalLanguage, CancellationToken cancellationToken)
    {
        await nasLanguageMappingLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedNasLanguageMappings == null || DateTime.UtcNow >= nasLanguageMappingsExpireAt)
            {
                Dictionary<string, string>? mappings = null;
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        using (var response = await scriptHttpClient.GetAsync(NintendoCommonScriptUrl, timeout.Token))
                        {
                            response.EnsureSuccessStatusCode();
                            mappings = ParseNasLanguageMappings(await response.Content.ReadAsStringAsync(timeout.Token));
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

    private static T? GetPortalData<T>(string source, string elementId)
    {
        var match = Regex.Match(source, $@"<div id=""{elementId}"" data-json=""(.*?)""", RegexOptions.Singleline);
        return match.Success
            ? JsonSerializer.Deserialize<T>(WebUtility.HtmlDecode(match.Groups[1].Value), jsonOptions)
            : default;
    }

    private async Task<VgcResponse> GetVirtualGameCardsPageAsync(HttpClient httpClient, VgcQueryParameters queryParams, int offset, CancellationToken cancellationToken, int limit = PageSize)
    {
        var settings = plugin.Settings;
        var portalLanguage = queryParams.PortalLanguage!;
        string? savedNasLanguage = null;
        if (settings.NasLanguageMappings != null)
        {
            settings.NasLanguageMappings.TryGetValue(portalLanguage, out savedNasLanguage);
        }
        if (!string.IsNullOrEmpty(savedNasLanguage))
        {
            queryParams.NasLanguage = savedNasLanguage;
        }

        var originalNasLanguage = queryParams.NasLanguage!;
        var checkedJavascript = false;
        string? javascriptNasLanguage = null;
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
        VirtualGameCardsRequestException? lastError = null;
        while (candidates.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nasLanguage = candidates.Dequeue();
            if (!attempted.Add(nasLanguage))
            {
                continue;
            }
            var requestParams = new VgcQueryParameters
            {
                CountryCode = queryParams.CountryCode,
                LanguageCode = queryParams.LanguageCode,
                NasLanguage = nasLanguage,
                ShopId = queryParams.ShopId,
                IdToken = queryParams.IdToken,
                SavannaClientId = queryParams.SavannaClientId,
                ShopGraphQlApiUrl = queryParams.ShopGraphQlApiUrl
            };

            try
            {
                var vgc = await SendVirtualGameCardsPageAsync(httpClient, requestParams, offset, cancellationToken, limit);
                queryParams.NasLanguage = nasLanguage;
                if (string.Equals(nasLanguage, javascriptNasLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info($"Nintendo Virtual Game Cards request succeeded using the JavaScript mapping for portalLanguage={portalLanguage}, nasLanguage={nasLanguage}.");
                    plugin.SaveNasLanguageMapping(portalLanguage, nasLanguage);
                }
                else if (!string.Equals(nasLanguage, originalNasLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warn($"Nintendo Virtual Game Cards request succeeded using English fallback for country={queryParams.CountryCode}, " +
                        $"originalLanguage={originalNasLanguage}, nasLanguage={nasLanguage}.");
                    plugin.SaveNasLanguageMapping(portalLanguage, nasLanguage);
                }
                return vgc;
            }
            catch (VirtualGameCardsRequestException e)
            {
                requestErrors.Add(e.Message);
                lastError = e;
                if (!e.CanRetryWithAnotherLocale)
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
        public bool CanRetryWithAnotherLocale { get; }

        public VirtualGameCardsRequestException(string message, bool canRetryWithAnotherLocale) : base(message)
        {
            CanRetryWithAnotherLocale = canRetryWithAnotherLocale;
        }
    }

    private static async Task<VgcResponse> SendVirtualGameCardsPageAsync(HttpClient client, VgcQueryParameters parameters, int offset, CancellationToken cancellationToken, int limit)
    {
        var requestBody = new
        {
            query = VgcQuery,
            variables = new
            {
                country = parameters.CountryCode,
                idToken = parameters.IdToken,
                language = parameters.LanguageCode,
                limit,
                nasLanguage = parameters.NasLanguage,
                offset,
                order = "ASC",
                shopId = parameters.ShopId,
                sortBy = "ACTIVATED_DATE"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, parameters.ShopGraphQlApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-nintendo-savanna-client-id", parameters.SavannaClientId);
        using var response = await client.SendAsync(request, cancellationToken);
        string error;
        var canRetryWithAnotherLocale = false;
        if (!response.IsSuccessStatusCode)
        {
            error = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
        }
        else
        {
            var result = JsonSerializer.Deserialize<VgcResponse>(await response.Content.ReadAsStringAsync(cancellationToken), jsonOptions);
            if (result?.Errors?.Count > 0)
            {
                canRetryWithAnotherLocale = result.Errors.All(item => string.Equals(item.Message, "error", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(item.Extensions?.Code));
                error = string.Join("; ", result.Errors.Select(item => string.IsNullOrEmpty(item.Extensions?.Code)
                    ? item.Message
                    : $"{item.Message} (code: {item.Extensions.Code})"));
            }
            else if (result?.Data?.Account?.Vgc?.VgcViews?.OffsetInfo == null || result.Data.Account.Vgc.VgcViews.Views == null)
            {
                error = "Nintendo returned an incomplete response.";
            }
            else
            {
                return result;
            }
        }

        var requestDetails = $"HTTP {(int)response.StatusCode}, " +
            $"country={parameters.CountryCode}, language={parameters.LanguageCode}, nasLanguage={parameters.NasLanguage}, " +
            $"shopId={parameters.ShopId}, offset={offset}, limit={limit}.";
        var errorMessage = "Nintendo Virtual Game Cards request failed: " + error +
            Environment.NewLine + "Request details: " + requestDetails;
        logger.Warn(errorMessage);
        throw new VirtualGameCardsRequestException(errorMessage, canRetryWithAnotherLocale);
    }

    private const string VgcQuery = """
        query getVgcs(
          $idToken: String!
          $country: CountryCode!
          $language: LanguageCode!
          $shopId: Int!
          $limit: Int!
          $nasLanguage: String!
          $offset: Int!
          $order: RequestableVgcViewOrder!
          $sortBy: RequestableVgcViewSortBy!
        ) @inContext(country: $country, language: $language, shopId: $shopId) {
          account {
            vgc {
              vgcViews: vgcViewsV2(idToken: $idToken, limit: $limit, language: $nasLanguage, offset: $offset, order: $order, sortBy: $sortBy, isHidden: false) {
                offsetInfo { total offset }
                views {
                  applicationId
                  applicationName
                  icon { url upgradedIconUrl sizes }
                  apparentPlatform
                  hasApplication: hasReleasedApplication
                  hasAddOnContents: hasReleasedAddOnContents
                  hasNxApplication: hasReleasedNxApplication
                  hasNxAddOnContents: hasReleasedNxAddOnContents
                  hasOunceApplication: hasReleasedOunceApplication
                  hasOunceAddOnContents: hasReleasedOunceAddOnContents
                }
              }
            }
          }
        }
        """;
}

public sealed class VirtualGameCard
{
    public string? ApplicationId { get; init; }
    public string? ApplicationName { get; init; }
    public string? ApparentPlatform { get; init; }
    public VirtualGameCardIcon? Icon { get; init; }
    public bool HasApplication { get; init; }
    public bool HasAddOnContents { get; init; }
    public bool HasNxApplication { get; init; }
    public bool HasNxAddOnContents { get; init; }
    public bool HasOunceApplication { get; init; }
    public bool HasOunceAddOnContents { get; init; }

    public bool IsAddOnOnly => !HasApplication && HasAddOnContents;

    /// <summary>The upgraded icon is the higher resolution variant when Nintendo offers one.</summary>
    public string? IconUrl => Icon?.GetUrl(PreferredIconSize);

    /// <summary>Icons are shown small, so the smallest size at or above this is plenty.</summary>
    private const int PreferredIconSize = 256;
}

public sealed class VirtualGameCardIcon
{
    public string? Url { get; init; }
    public string? UpgradedIconUrl { get; init; }
    public int[]? Sizes { get; init; }

    /// <summary>
    /// Nintendo returns the image as a template ending in "_${size}". Left as-is the CDN answers
    /// 404, so the placeholder has to be replaced with one of the offered sizes.
    /// </summary>
    public string? GetUrl(int preferredSize)
    {
        var template = string.IsNullOrWhiteSpace(UpgradedIconUrl) ? Url : UpgradedIconUrl;
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (!template.Contains(SizePlaceholder, StringComparison.Ordinal))
        {
            return template;
        }

        var size = Sizes?.Where(candidate => candidate >= preferredSize).DefaultIfEmpty(0).Min() ?? 0;
        if (size == 0)
        {
            size = Sizes?.DefaultIfEmpty(preferredSize).Max() ?? preferredSize;
        }

        return template.Replace(SizePlaceholder, size.ToString(), StringComparison.Ordinal);
    }

    private const string SizePlaceholder = "${size}";
}

public sealed class VgcQueryParameters
{
    public string? IdToken { get; set; }
    public string? SavannaClientId { get; set; }
    public string? ShopGraphQlApiUrl { get; set; }
    public string? CountryCode { get; set; }
    public string? LanguageCode { get; set; }
    public string? NasLanguage { get; set; }
    public string? PortalLanguage { get; set; }
    public int ShopId { get; set; }
}

public sealed class VgcPortalMeta
{
    public List<VgcCountry>? Countries { get; init; }
}

public sealed class VgcPortalState
{
    public VgcPortalUser? User { get; init; }
    public string? Lang { get; init; }
}

public sealed class VgcPortalUser
{
    public int CountryId { get; init; }
}

public sealed class VgcCountry
{
    public int Id { get; init; }
    public string? Code { get; init; }
}

public sealed class VgcResponse
{
    public VgcResponseData? Data { get; init; }
    public List<VgcError>? Errors { get; init; }
}

public sealed class VgcResponseData
{
    public VgcAccount? Account { get; init; }
}

public sealed class VgcAccount
{
    public VgcLibrary? Vgc { get; init; }
}

public sealed class VgcLibrary
{
    public VgcViews? VgcViews { get; init; }
}

public sealed class VgcViews
{
    public VgcOffsetInfo? OffsetInfo { get; init; }
    public List<VirtualGameCard>? Views { get; init; }
}

public sealed class VgcOffsetInfo
{
    public int Total { get; init; }
}

public sealed class VgcError
{
    public string? Message { get; init; }
    public VgcErrorExtensions? Extensions { get; init; }
}

public sealed class VgcErrorExtensions
{
    public string? Code { get; init; }
}

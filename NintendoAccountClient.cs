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
    private static readonly ILogger logger = LogManager.GetLogger();

    private const string VirtualGameCardsUrl = "https://accounts.nintendo.com/portal/vgcs/?sort=activated_date&order=desc";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";
    private const int PageSize = 300;
    private const int OffDeviceShopId = 3;
    private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IPlayniteApi playniteApi;

    public NintendoAccountClient(IPlayniteApi playniteApi)
    {
        this.playniteApi = playniteApi;
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
            var page = await GetVirtualGameCardsPageAsync(client, queryParameters, offset, cancellationToken);
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
        parameters.NasLanguage = state.Lang;
        parameters.ShopId = OffDeviceShopId;
        return parameters;
    }

    private static T? GetPortalData<T>(string source, string elementId)
    {
        var match = Regex.Match(source, $@"<div id=""{elementId}"" data-json=""(.*?)""", RegexOptions.Singleline);
        return match.Success
            ? JsonSerializer.Deserialize<T>(WebUtility.HtmlDecode(match.Groups[1].Value), jsonOptions)
            : default;
    }

    private static async Task<VgcResponse> GetVirtualGameCardsPageAsync(HttpClient client, VgcQueryParameters parameters, int offset, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            query = VgcQuery,
            variables = new
            {
                country = parameters.CountryCode,
                idToken = parameters.IdToken,
                language = parameters.LanguageCode,
                limit = PageSize,
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
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The Nintendo virtual game card request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        var result = JsonSerializer.Deserialize<VgcResponse>(await response.Content.ReadAsStringAsync(cancellationToken), jsonOptions);
        if (result?.Errors?.Count > 0)
        {
            throw new InvalidOperationException("Nintendo Virtual Game Cards request failed: " + result.Errors[0].Message);
        }

        return result ?? throw new InvalidOperationException("Nintendo Virtual Game Cards response was empty.");
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
              vgcViews(idToken: $idToken, limit: $limit, nasLanguage: $nasLanguage, offset: $offset, order: $order, sortBy: $sortBy, isHidden: false) {
                offsetInfo { total offset }
                views {
                  applicationId
                  applicationName
                  icon { url upgradedIconUrl sizes }
                  apparentPlatform
                  hasApplication
                  hasAddOnContents
                  hasNxApplication
                  hasNxAddOnContents
                  hasOunceApplication
                  hasOunceAddOnContents
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
}

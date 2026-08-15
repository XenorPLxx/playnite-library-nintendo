using System.Collections.Generic;

namespace NintendoLibrary.Models
{
  public class Vgc
  {
    public vgcData data { get; set; }
    public List<Error> errors { get; set; }

    public class Error
    {
      public string message { get; set; }
    }

    public class vgcData
    {
      public vgcAccount account { get; set; }
      public class vgcAccount
      {
        public vgcList vgc { get; set; }
        public class vgcList
        {
          public VirtualGameCardsList vgcViews { get; set; }
        }
      }
    }
  }
  public class VirtualGameCardsList
  {
    public class OffsetInfo
    {
      public int offset { get; set; }
      public int total { get; set; }
    }
    public class View
    {
      public string id { get; set; }
      public string applicationId { get; set; }
      public string applicationName { get; set; }
      public string apparentPlatform { get; set; }
      public bool hasApplication { get; set; }
      public bool hasAddOnContents { get; set; }
      public bool hasNxApplication { get; set; }
      public bool hasNxAddOnContents { get; set; }
      public bool hasOunceApplication { get; set; }
      public bool hasOunceAddOnContents { get; set; }
      public Icon icon { get; set; }

      public class Icon
      {
        public string url { get; set; }
        public string upgradedIconUrl { get; set; }
        public int[] sizes { get; set; }
      }
    }

    public List<View> views { get; set; }
    public OffsetInfo offsetInfo { get; set; }
  }

  public class VgcQueryParams
  {
    public string csrfToken { get; set; }
    public string idToken { get; set; }
    public string savannaClientId { get; set; }
    public string myNintendoAccessToken { get; set; }
    public string shopGraphQLApiUrl { get; set; }
    public string countryCode { get; set; }
    public string languageCode { get; set; }
    public string nasLanguage { get; set; }
    public int shopId { get; set; }
  }

  public class VgcPortalMeta
  {
    public List<VgcCountry> countries { get; set; }
  }

  public class VgcPortalState
  {
    public VgcPortalUser user { get; set; }
    public string lang { get; set; }
  }

  public class VgcPortalUser
  {
    public int countryId { get; set; }
  }

  public class VgcCountry
  {
    public int id { get; set; }
    public string code { get; set; }

  }
}

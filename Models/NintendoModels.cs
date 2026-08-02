using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NintendoLibrary.Models
{
  public class AuthError
  {
    public class Error
    {
      public string code { get; set; }
      public string message { get; set; }
    }
    public Error error;
  }

  public class PurchasedList
  {
    public int length { get; set; }
    public int offset { get; set; }
    public int total { get; set; }
    public class Transaction
    {
      public string content_type { get; set; }
      public string title { get; set; }
      public string device_type { get; set; }
      public DateTime date { get; set; }
      public ulong transaction_id { get; set; }
    }

    public List<Transaction> transactions { get; set; }
  }

  public class Vgc
  {
    public vgcData data { get; set; }
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
      public bool hasNxApplication { get; set; }
      public bool hasNxAddOnContents { get; set; }
      public bool hasOunceApplication { get; set; }
      public bool hasOunceAddOnContents { get; set; }
      public class Icon
      {
        public string url { get; set; }
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

  }

  public class VgcStateParams
  {
    public User user { get; set; }
    public class User
    {
      public int countryId { get; set; }
    }
    public string lang { get; set; }

  }
}

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
}

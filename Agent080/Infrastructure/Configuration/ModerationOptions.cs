using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent080.Infrastructure.Configuration
{
    public class ModerationOptions
    {
        public const string SectionName = "Moderation";

        public string ApiEndpoint { get; set; } = string.Empty;
        public string SubscriptionKey { get; set; } = string.Empty;
        public int CacheMinutes { get; set; } = 1;
    }
}
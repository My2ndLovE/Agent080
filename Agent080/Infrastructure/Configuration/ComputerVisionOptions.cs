using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent080.Infrastructure.Configuration
{
    public class ComputerVisionOptions
    {
        public const string SectionName = "ComputerVision";

        public string PrimaryEndpoint { get; set; } = string.Empty;
        public string PrimaryKey { get; set; } = string.Empty;
        public string? SecondaryEndpoint { get; set; }
        public string? SecondaryKey { get; set; }
        public int CacheExpirationHours { get; set; } = 24;
        public int MaxImageSize { get; set; } = 1024;
        public int MaxConcurrentProcessing { get; set; } = 3;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent080.Infrastructure.Configuration
{
    public class StorageOptions
    {
        public const string SectionName = "Storage";

        public string CosmosConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string TableStorageConnectionString { get; set; } = string.Empty;
        public string MessagesTableName { get; set; } = string.Empty;
    }
}

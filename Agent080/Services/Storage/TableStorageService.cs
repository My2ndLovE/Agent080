using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Agent080.Services.Storage
{
    /// <summary>
    /// Service for interacting with Azure Table Storage
    /// </summary>
    public class TableStorageService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableStorageService> _logger;

        public TableStorageService(ILogger<TableStorageService> logger)
        {
            _logger = logger;

            try
            {
                var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                    ?? throw new InvalidOperationException("TableStorageConnection environment variable is not set");
                var tableName = Environment.GetEnvironmentVariable("MessagesTableName")
                    ?? throw new InvalidOperationException("MessagesTableName environment variable is not set");

                // Create table client
                _tableClient = new TableClient(connectionString, tableName);

                // Create table if it doesn't exist
                _tableClient.CreateIfNotExists();

                _logger.LogInformation("Successfully connected to table storage: {TableName}", tableName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Table Storage connection");
                throw;
            }
        }

        public TableClient TableClient => _tableClient;
    }
}
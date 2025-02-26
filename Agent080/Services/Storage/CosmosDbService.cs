using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;
using System.Net;
using Agent080.Core.Exceptions;
using Agent080.Infrastructure.Configuration;
using Agent080.Utilities.Logging;
using Microsoft.Extensions.Options;

namespace Agent080.Services.Storage
{
    /// <summary>
    /// Service for interacting with Azure Cosmos DB
    /// </summary>
    public class CosmosDbService
    {
        private readonly Container _container;
        private readonly ILogger<CosmosDbService> _logger;
        private readonly JsonSerializerSettings _serializerSettings;

        public CosmosDbService(
    IOptions<StorageOptions> options,
    ILogger<CosmosDbService> logger)
        {
            ArgumentNullException.ThrowIfNull(options, nameof(options));

            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            try
            {
                var storageOptions = options.Value;

                // Validate required options
                if (string.IsNullOrEmpty(storageOptions.CosmosConnectionString))
                    throw new StorageException("CosmosConnectionString is not configured");

                if (string.IsNullOrEmpty(storageOptions.DatabaseName))
                    throw new StorageException("DatabaseName is not configured");

                if (string.IsNullOrEmpty(storageOptions.ContainerName))
                    throw new StorageException("ContainerName is not configured");

                var client = new CosmosClient(storageOptions.CosmosConnectionString);
                Database database = client.CreateDatabaseIfNotExistsAsync(storageOptions.DatabaseName)
                    .GetAwaiter()
                    .GetResult();

                var containerProperties = new ContainerProperties(storageOptions.ContainerName, partitionKeyPath: "/chatId");

                try
                {
                    _container = database.GetContainer(storageOptions.ContainerName);
                    var response = _container.ReadContainerAsync()
                        .GetAwaiter()
                        .GetResult();

                    if (response.Resource.PartitionKeyPath != "/chatId")
                    {
                        _logger.LogError(
                            LogEvents.ServiceStarted,
                            "Container exists but has incorrect partition key path: {CurrentPath}. Expected: /chatId",
                            response.Resource.PartitionKeyPath);
                        throw new StorageException(
                            $"Container {storageOptions.ContainerName} exists but has wrong partition key path: {response.Resource.PartitionKeyPath}");
                    }

                    _logger.LogInformation(
                        LogEvents.ServiceStarted,
                        "Successfully connected to existing container {ContainerName} with correct partition key path",
                        storageOptions.ContainerName);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    _container = database.CreateContainerAsync(containerProperties)
                        .GetAwaiter()
                        .GetResult();
                    _logger.LogInformation(
                        LogEvents.ServiceStarted,
                        "Created new container {ContainerName} with partition key path /chatId",
                        storageOptions.ContainerName);
                }
            }
            catch (Exception ex) when (ex is not StorageException)
            {
                _logger.LogError(ex, "Failed to initialize CosmosDB connection");
                throw new StorageException("Failed to initialize CosmosDB connection", ex);
            }
        }

        // Expose the container and settings for repositories
        public Container Container => _container;
        public JsonSerializerSettings SerializerSettings => _serializerSettings;
    }
}
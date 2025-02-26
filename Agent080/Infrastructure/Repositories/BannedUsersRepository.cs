using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using Agent080.Core.Models.Entities;
using Agent080.Services.Storage;

namespace Agent080.Infrastructure.Repositories
{
    public class BannedUsersRepository
    {
        private readonly Container _container;
        private readonly ILogger<BannedUsersRepository> _logger;
        private readonly JsonSerializerSettings _serializerSettings;

        public BannedUsersRepository(
            CosmosDbService cosmosDbService,
            ILogger<BannedUsersRepository> logger)
        {
            _container = cosmosDbService.Container;
            _serializerSettings = cosmosDbService.SerializerSettings;
            _logger = logger;
        }

        public async Task<BannedUser> AddBannedUserAsync(BannedUser user)
        {
            try
            {
                // Log the incoming data for debugging
                var jsonDocument = JsonConvert.SerializeObject(user, _serializerSettings);
                var partitionKeyRaw = user.ChatId; // Get the raw Int64 value
                var partitionKeyString = partitionKeyRaw.ToString(); // Convert to string for logging

                _logger.LogInformation("""
                    Attempting to create banned user:
                    Username: {Username}
                    UserId: {UserId}
                    ChatId (raw): {ChatIdRaw}
                    ChatId (string): {ChatIdString}
                    Document ID: {DocumentId}
                    Full Document JSON: 
                    {Document}
                    """,
                    user.Username,
                    user.UserId,
                    partitionKeyRaw,
                    partitionKeyString,
                    user.Id,
                    jsonDocument);

                // Use the raw Int64 value for the partition key
                var response = await _container.CreateItemAsync(
                    user,
                    new PartitionKey(partitionKeyRaw),
                    new ItemRequestOptions { EnableContentResponseOnWrite = true }
                );

                _logger.LogInformation("Successfully added banned user {Username} (ID: {UserId}) to chat {ChatId}",
                    user.Username, user.UserId, user.ChatId);

                return response.Resource;
            }
            catch (CosmosException ex)
            {
                _logger.LogError("""
                    CosmosDB Error creating banned user:
                    Status: {Status}
                    SubStatus: {SubStatus}
                    Message: {Message}
                    ActivityId: {ActivityId}
                    ChatId: {ChatId}
                    Full Document: 
                    {Document}
                    """,
                    ex.StatusCode,
                    ex.SubStatusCode,
                    ex.Message,
                    ex.ActivityId,
                    user.ChatId,
                    JsonConvert.SerializeObject(user, _serializerSettings));

                throw;
            }
        }

        public async Task<BannedUser?> GetBannedUserAsync(Int64 chatId, Int64 userId)
        {
            try
            {
                var id = $"{chatId}-{userId}-"; // Partial ID match

                var queryDefinition = new QueryDefinition(
                    "SELECT * FROM c WHERE c.id LIKE @idPrefix AND c.chatId = @chatId AND c.userId = @userId")
                    .WithParameter("@idPrefix", id)
                    .WithParameter("@chatId", chatId)
                    .WithParameter("@userId", userId);

                var iterator = _container.GetItemQueryIterator<BannedUser>(
                    queryDefinition,
                    requestOptions: new QueryRequestOptions
                    {
                        PartitionKey = new PartitionKey(chatId)
                    }
                );

                if (iterator.HasMoreResults)
                {
                    var results = await iterator.ReadNextAsync();
                    return results.FirstOrDefault();
                }

                return null;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }
    }
}
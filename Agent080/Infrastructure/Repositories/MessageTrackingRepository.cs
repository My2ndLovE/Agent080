using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Agent080.Core.Models.Entities;
using Agent080.Services.Storage;

namespace Agent080.Infrastructure.Repositories
{
    public class MessageTrackingRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<MessageTrackingRepository> _logger;

        public MessageTrackingRepository(
            TableStorageService tableStorageService,
            ILogger<MessageTrackingRepository> logger)
        {
            _tableClient = tableStorageService.TableClient;
            _logger = logger;
        }

        public async Task AddMessageAsync(long chatId, long userId, int messageId, string chatTitle)
        {
            var entity = new MessageEntity(chatId, userId, messageId, chatTitle);

            try
            {
                _logger.LogInformation(
                    "Adding message - ChatId: {ChatId}, UserId: {UserId}, MessageId: {MessageId}, ChatTitle: {ChatTitle}, " +
                    "PartitionKey: {PartitionKey}, RowKey: {RowKey}, Status: {Status}, CreatedAt: {CreatedAt}",
                    chatId, userId, messageId, chatTitle, entity.PartitionKey, entity.RowKey, entity.Status, entity.CreatedAt);

                await _tableClient.AddEntityAsync(entity);

                _logger.LogInformation("Successfully added message {MessageId} for user {UserId} in chat {ChatId} ({ChatTitle})",
                    messageId, userId, chatId, chatTitle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to add message {MessageId} for user {UserId} in chat {ChatId} ({ChatTitle})",
                    messageId, userId, chatId, chatTitle);
                throw;
            }
        }

        public async Task<List<int>> GetUserMessagesAsync(
            long chatId,
            long userId,
            TimeSpan timeSpan,
            MessageStatus status = MessageStatus.Active)
        {
            var cutoffTime = DateTime.UtcNow - timeSpan;
            var partitionKey = $"{chatId}-{userId}";

            _logger.LogInformation(
                "Starting message query - Chat: {ChatId}, User: {UserId}, Status: {Status}, Cutoff: {CutoffTime}",
                chatId, userId, status, cutoffTime);

            try
            {
                // Construct filter string for debugging
                var filter = $"PartitionKey eq '{partitionKey}' and Status eq '{status}'";
                _logger.LogDebug("Using filter: {Filter}", filter);

                // Query entities
                var query = _tableClient.QueryAsync<MessageEntity>(filter);
                var messages = new List<int>();
                var processedCount = 0;

                await foreach (var message in query)
                {
                    processedCount++;
                    _logger.LogDebug(
                        "Found entity - MessageId: {MessageId}, Status: {Status}, CreatedAt: {CreatedAt}, " +
                        "PartitionKey: {PartitionKey}, RowKey: {RowKey}",
                        message.MessageId, message.Status, message.CreatedAt,
                        message.PartitionKey, message.RowKey);

                    if (message.CreatedAt >= cutoffTime)
                    {
                        messages.Add(message.MessageId);
                        _logger.LogDebug("Added message {MessageId} to result set", message.MessageId);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Skipped message {MessageId} due to age. CreatedAt: {CreatedAt}, Cutoff: {CutoffTime}",
                            message.MessageId, message.CreatedAt, cutoffTime);
                    }
                }

                _logger.LogInformation(
                    "Query complete - Processed {ProcessedCount} entities, returning {ResultCount} messages " +
                    "for user {UserId} in chat {ChatId} with status {Status}",
                    processedCount, messages.Count, userId, chatId, status);

                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error retrieving messages for user {UserId} in chat {ChatId}",
                    userId, chatId);
                throw;
            }
        }

        public async Task UpdateMessageStatusAsync(
            long chatId,
            IEnumerable<int> messageIds,
            long userId,
            MessageStatus newStatus)
        {
            var partitionKey = $"{chatId}-{userId}";
            var batchOperations = new List<TableTransactionAction>();

            _logger.LogInformation(
                "Starting status update to {NewStatus} for {Count} messages in chat {ChatId}",
                newStatus, messageIds.Count(), chatId);

            try
            {
                // Create a set of message IDs for efficient lookup
                var messageIdSet = new HashSet<int>(messageIds);

                // Query entities with matching PartitionKey and message IDs
                var filter = $"PartitionKey eq '{partitionKey}'";
                var query = _tableClient.QueryAsync<MessageEntity>(filter);

                var processedIds = new HashSet<string>(); // Track processed row keys

                await foreach (var entity in query)
                {
                    // Only process if message ID is in our target set and we haven't processed this row key
                    if (messageIdSet.Contains(entity.MessageId) && processedIds.Add(entity.RowKey))
                    {
                        entity.Status = newStatus;
                        batchOperations.Add(new TableTransactionAction(
                            TableTransactionActionType.UpdateMerge,
                            entity));

                        if (batchOperations.Count == 100)
                        {
                            await SubmitBatch(batchOperations, chatId, newStatus);
                            batchOperations.Clear();
                        }
                    }
                }

                if (batchOperations.Any())
                {
                    await SubmitBatch(batchOperations, chatId, newStatus);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error updating status for messages in chat {ChatId}",
                    chatId);
                throw;
            }
        }

        private async Task SubmitBatch(List<TableTransactionAction> batchOperations, long chatId, MessageStatus newStatus)
        {
            try
            {
                await _tableClient.SubmitTransactionAsync(batchOperations);
                _logger.LogInformation(
                    "Successfully updated status to {NewStatus} for batch of {Count} messages in chat {ChatId}",
                    newStatus, batchOperations.Count, chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error submitting batch update for {Count} messages in chat {ChatId}",
                    batchOperations.Count, chatId);
                throw;
            }
        }

        public async Task DeleteOldMessagesAsync(TimeSpan retention)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.Subtract(retention);
                var filter = $"CreatedAt lt datetime'{cutoffTime:yyyy-MM-ddTHH:mm:ssZ}'";

                var deleteTasks = new List<Task>();

                await foreach (var entity in _tableClient.QueryAsync<MessageEntity>(filter))
                {
                    deleteTasks.Add(_tableClient.DeleteEntityAsync(
                        entity.PartitionKey,
                        entity.RowKey,
                        ETag.All));

                    // Process deletions in batches of 100
                    if (deleteTasks.Count >= 100)
                    {
                        await Task.WhenAll(deleteTasks);
                        deleteTasks.Clear();
                    }
                }

                // Process remaining deletions
                if (deleteTasks.Any())
                {
                    await Task.WhenAll(deleteTasks);
                }

                _logger.LogInformation("Successfully deleted messages older than {Cutoff}", cutoffTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old messages");
                throw;
            }
        }
    }
}
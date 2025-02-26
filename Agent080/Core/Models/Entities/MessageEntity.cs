using Azure;
using Azure.Data.Tables;

namespace Agent080.Core.Models.Entities
{
    public enum MessageStatus
    {
        Active = 0,
        PendingDeletion = 1,
        Deleted = 2
    }

    public class MessageEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!; // Will be chatId-userId
        public string RowKey { get; set; } = default!;  // Will be timestamp-messageId for ordered querying
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public long ChatId { get; set; }
        public long UserId { get; set; }
        public int MessageId { get; set; }
        public string ChatTitle { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public MessageStatus Status { get; set; }

        public MessageEntity()
        {
            ETag = new ETag("*");
        }

        public MessageEntity(long chatId, long userId, int messageId, string chatTitle) : this()
        {
            ChatId = chatId;
            UserId = userId;
            MessageId = messageId;
            ChatTitle = chatTitle;
            CreatedAt = DateTime.UtcNow;
            Status = MessageStatus.Active;

            // Format keys for efficient querying by chat-user combination
            PartitionKey = $"{chatId}-{userId}";
            // Use inverted ticks for reverse chronological order
            var invertedTicks = DateTime.MaxValue.Ticks - CreatedAt.Ticks;
            RowKey = $"{invertedTicks:d19}-{messageId}";
        }
    }
}
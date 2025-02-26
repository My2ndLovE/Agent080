using Newtonsoft.Json;

namespace Agent080.Core.Models.Entities
{
    public class BannedUser
    {
        [JsonProperty("id")]
        public string? Id { get; init; }

        [JsonProperty("chatId")]
        public Int64 ChatId { get; init; }  // We'll use this as partition key in container creation

        [JsonProperty("chatTitle")]
        public string? ChatTitle { get; init; }

        [JsonProperty("userId")]
        public Int64 UserId { get; init; }

        [JsonProperty("username")]
        public string? Username { get; init; }

        [JsonProperty("bannedAt")]
        public DateTime BannedAt { get; init; }

        [JsonProperty("reason")]
        public string? Reason { get; init; }

        [JsonProperty("detectedText")]
        public string? DetectedText { get; init; }

        public BannedUser(Int64 chatId, Int64 userId, string chatTitle)
        {
            ChatId = chatId;
            UserId = userId;
            ChatTitle = chatTitle;
            Id = $"{chatId}-{userId}-{DateTime.UtcNow.Ticks}";
            Username = "";
            Reason = "";
            DetectedText = "";
            BannedAt = DateTime.UtcNow;
        }

        public BannedUser() { } // Required for serialization
    }
}
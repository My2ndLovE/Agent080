﻿using System.Text.Json.Serialization;

namespace Agent080.Core.Models.Telegram
{
    public class PhotoSize
    {
        [JsonPropertyName("file_id")]
        public string FileId { get; set; } = "";

        [JsonPropertyName("file_unique_id")]
        public string FileUniqueId { get; set; } = "";

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("file_size")]
        public int FileSize { get; set; }
    }
}
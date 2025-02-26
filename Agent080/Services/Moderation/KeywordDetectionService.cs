using Microsoft.Extensions.Logging;
using Agent080.Core.Interfaces;
using Agent080.Infrastructure.Configuration;

namespace Agent080.Services.Moderation
{
    /// <summary>
    /// Legacy service for simple keyword detection
    /// </summary>
    public class KeywordDetectionService : IKeywordDetectionService
    {
        private readonly ILogger<KeywordDetectionService> _logger;
        private readonly ProhibitedKeywordConfiguration _configuration;

        public KeywordDetectionService(
            ILogger<KeywordDetectionService> logger,
            ProhibitedKeywordConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public (bool isProhibited, string matchedPattern) CheckContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return (false, string.Empty);

            foreach (var keyword in _configuration.Keywords)
            {
                if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Found prohibited content matching pattern: {Pattern}", keyword);
                    return (true, keyword);
                }
            }

            return (false, string.Empty);
        }
    }
}
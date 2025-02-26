using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Agent080.Core.Interfaces;
using Agent080.Infrastructure.Configuration;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;

namespace Agent080.Services.Moderation
{
    public class MessageModerationService : IMessageModerationService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<MessageModerationService> _logger;
        private readonly ProhibitedKeywordConfiguration _fallbackConfiguration;
        private readonly string _apiUrl;
        private readonly string _subscriptionKey;
        private readonly TimeSpan _cacheExpiration;

        // Cache for keywords
        private DateTime _lastFetchTime = DateTime.MinValue;
        private List<KeywordDto> _cachedKeywords = new List<KeywordDto>();

        // JSON serialization options
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        // Regex for extracting domains from URLs
        private static readonly Regex UrlRegex = new Regex(
            @"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Regex to extract domain name from URL
        private static readonly Regex DomainRegex = new Regex(
            @"https?:\/\/(www\.)?([^\/\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public MessageModerationService(
            HttpClient httpClient,
            ILogger<MessageModerationService> logger,
            ProhibitedKeywordConfiguration fallbackConfiguration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _fallbackConfiguration = fallbackConfiguration;

            // Get configuration from environment variables
            _apiUrl = Environment.GetEnvironmentVariable("ModerationKeyword:Endpoint")
                ?? throw new InvalidOperationException("ModerationKeyword:Endpoint environment variable is not set");

            _subscriptionKey = Environment.GetEnvironmentVariable("ModerationKeyword:SubscriptionKey")
                ?? throw new InvalidOperationException("ModerationKeyword:SubscriptionKey environment variable is not set");

            // Default cache expiration is 1 minutes
            int cacheMinutes = 1;
            string cacheMinsStr = Environment.GetEnvironmentVariable("ModerationKeyword:CacheMinutes");
            if (!string.IsNullOrEmpty(cacheMinsStr) && int.TryParse(cacheMinsStr, out int configuredMinutes))
            {
                cacheMinutes = configuredMinutes;
            }
            _cacheExpiration = TimeSpan.FromMinutes(cacheMinutes);

            // Configure HttpClient
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _subscriptionKey);

            _logger.LogInformation("Initialized MessageModerationService with API URL: {ApiUrl}", _apiUrl);
        }

        private async Task RefreshKeywordsIfNeededAsync()
        {
            if (DateTime.UtcNow - _lastFetchTime < _cacheExpiration && _cachedKeywords.Any())
            {
                _logger.LogDebug("Using cached keywords. Cache age: {CacheAge} minutes",
                    (DateTime.UtcNow - _lastFetchTime).TotalMinutes);
                return;
            }

            try
            {
                _logger.LogInformation("Fetching keywords from API: {Url}", _apiUrl);

                // Set max result count to 1000 to ensure we get all keywords
                var response = await _httpClient.GetAsync($"{_apiUrl}?MaxResultCount=1000&SkipCount=0");
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                // Log a sample of the response for debugging
                if (content.Length > 0)
                {
                    _logger.LogInformation("API response sample (first 200 chars): {ResponseSample}",
                        content.Length <= 200 ? content : content.Substring(0, 200));
                }

                try
                {
                    // First try to deserialize as a direct array
                    List<KeywordDto> keywords;
                    if (content.TrimStart().StartsWith("["))
                    {
                        // The API is returning a direct array
                        _logger.LogInformation("API returned a direct array of keywords");
                        keywords = JsonSerializer.Deserialize<List<KeywordDto>>(content, _jsonOptions);
                    }
                    else
                    {
                        // Try the wrapped object format
                        var result = JsonSerializer.Deserialize<ApiResponse<KeywordDto>>(content, _jsonOptions);
                        keywords = result?.Items ?? new List<KeywordDto>();
                    }

                    if (keywords == null || !keywords.Any())
                    {
                        _logger.LogWarning("API returned null or empty items");

                        // Use fallback if we have no cached data
                        if (!_cachedKeywords.Any())
                        {
                            _logger.LogWarning("Using fallback prohibited keywords");
                            // Don't update _cachedKeywords here as we'll keep trying the API
                        }
                        return;
                    }

                    _cachedKeywords = keywords;
                    _lastFetchTime = DateTime.UtcNow;

                    // Log details about what was fetched
                    var categoryCounts = _cachedKeywords
                        .GroupBy(k => k.Category)
                        .Select(g => $"Category {g.Key}: {g.Count()} items")
                        .ToList();

                    _logger.LogInformation("Successfully fetched {Count} keywords from API. Categories: {Categories}",
                        _cachedKeywords.Count,
                        string.Join(", ", categoryCounts));
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize API response: {ErrorMessage}. Response content: {Content}",
                        ex.Message, content);

                    // Use fallback if we have no cached data
                    if (!_cachedKeywords.Any())
                    {
                        _logger.LogWarning("Using fallback prohibited keywords due to deserialization error");
                        // Don't update _cachedKeywords here as we'll keep trying the API
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch keywords from API: {Url}", _apiUrl);

                // Use fallback if we have no cached data
                if (!_cachedKeywords.Any())
                {
                    _logger.LogWarning("Using fallback prohibited keywords due to API error");
                    // Don't update _cachedKeywords here as we'll keep trying the API
                }
            }
        }

        private List<string> GetProhibitedKeywords()
        {
            var prohibitedKeywords = _cachedKeywords
                .Where(k => k.Category == KeywordCategory.Prohibited)
                .Select(k => k.Keyword)
                .ToList();

            // If we have no keywords from API, use fallback
            if (!prohibitedKeywords.Any() && _fallbackConfiguration != null && _fallbackConfiguration.Keywords != null)
            {
                _logger.LogInformation("Using {Count} fallback prohibited keywords", _fallbackConfiguration.Keywords.Count);
                return _fallbackConfiguration.Keywords.ToList();
            }

            return prohibitedKeywords;
        }

        private List<string> GetWhitelistedDomains()
        {
            // Use the WhitelistLink category for whitelisted domains
            return _cachedKeywords
                .Where(k => k.Category == KeywordCategory.WhitelistLink)
                .Select(k => k.Keyword)
                .ToList();
        }

        public async Task<(bool isProhibited, string matchedPattern)> ContainsProhibitedKeywordsAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return (false, string.Empty);

            await RefreshKeywordsIfNeededAsync();
            var prohibitedKeywords = GetProhibitedKeywords();

            string messageLower = message.ToLowerInvariant();

            foreach (var keyword in prohibitedKeywords)
            {
                if (messageLower.Contains(keyword.ToLowerInvariant()))
                {
                    _logger.LogInformation("Message contains prohibited keyword: {Keyword}", keyword);
                    return (true, keyword);
                }
            }

            return (false, string.Empty);
        }

        public async Task<(bool isNonWhitelisted, string domain, string url)> ContainsNonWhitelistedUrlAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return (false, string.Empty, string.Empty);

            await RefreshKeywordsIfNeededAsync();
            var whitelistedDomains = GetWhitelistedDomains();

            // Extract all URLs from the message
            var matches = UrlRegex.Matches(message);
            if (matches.Count == 0)
                return (false, string.Empty, string.Empty); // No URLs in message

            foreach (Match match in matches)
            {
                string url = match.Value;

                // Extract domain from URL
                var domainMatch = DomainRegex.Match(url);
                if (!domainMatch.Success)
                    continue;

                string fullDomain = domainMatch.Groups[2].Value.ToLowerInvariant();

                // Check if this domain or any parent domain is in the whitelist
                bool isWhitelisted = IsWhitelisted(fullDomain, whitelistedDomains);

                if (!isWhitelisted)
                {
                    _logger.LogInformation("Message contains non-whitelisted URL domain: {Domain}", fullDomain);
                    return (true, fullDomain, url);
                }
            }

            return (false, string.Empty, string.Empty);
        }

        private bool IsWhitelisted(string domain, List<string> whitelistedDomains)
        {
            if (whitelistedDomains.Count == 0)
                return false;

            foreach (var whitelistItem in whitelistedDomains)
            {
                // Extract the domain part from the whitelist item (which might be a full URL)
                string whitelistDomain = whitelistItem;

                // If whitelist item is a URL, extract the domain
                if (whitelistItem.StartsWith("http"))
                {
                    var match = DomainRegex.Match(whitelistItem);
                    if (match.Success)
                    {
                        whitelistDomain = match.Groups[2].Value.ToLowerInvariant();
                    }
                }

                // Check if the domain ends with the whitelisted domain
                // This allows subdomains to pass (e.g., tw.141-161.com should pass if 141-161.com is whitelisted)
                if (domain.EndsWith(whitelistDomain, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Domain {Domain} is whitelisted by {WhitelistDomain}", domain, whitelistDomain);
                    return true;
                }
            }

            return false;
        }

        public async Task<(bool isNonWhitelisted, string domain, string url)> ContainsNonWhitelistedFormattedUrlsAsync(Message message)
        {
            if (message?.Entities == null || !message.Entities.Any())
                return (false, string.Empty, string.Empty);

            await RefreshKeywordsIfNeededAsync();
            var whitelistedDomains = GetWhitelistedDomains();

            // Extract URLs from message entities
            var urlEntities = message.Entities.Where(e => e.Type == MessageEntityType.Url || e.Type == MessageEntityType.TextLink);

            foreach (var entity in urlEntities)
            {
                string url;

                if (entity.Type == MessageEntityType.TextLink)
                {
                    // For text links, the URL is in the Url property
                    url = entity.Url;
                }
                else
                {
                    // For regular URL entities, extract from the message text
                    url = message.Text.Substring(entity.Offset, entity.Length);
                }

                if (string.IsNullOrEmpty(url)) continue;

                // Extract domain from URL
                var domainMatch = DomainRegex.Match(url);
                if (!domainMatch.Success) continue;

                string fullDomain = domainMatch.Groups[2].Value.ToLowerInvariant();

                // Check if this domain or any parent domain is in the whitelist
                bool isWhitelisted = IsWhitelisted(fullDomain, whitelistedDomains);

                if (!isWhitelisted)
                {
                    _logger.LogInformation("Message contains non-whitelisted URL domain in formatted link: {Domain}", fullDomain);
                    return (true, fullDomain, url);
                }
            }

            return (false, string.Empty, string.Empty);
        }
    }

    // DTO classes for API response
    public class KeywordDto
    {
        public string Keyword { get; set; }
        public KeywordCategory Category { get; set; }
        public string Description { get; set; }
        public DateTime? LastModificationTime { get; set; }
        public string LastModifierId { get; set; }
        public DateTime CreationTime { get; set; }
        public string CreatorId { get; set; }
        public string Id { get; set; }
    }

    public enum KeywordCategory
    {
        Prohibited = 1,
        WhitelistLink = 2,
        Regex = 3
    }

    public class ApiResponse<T>
    {
        public List<T> Items { get; set; }
        public int TotalCount { get; set; }
    }
}
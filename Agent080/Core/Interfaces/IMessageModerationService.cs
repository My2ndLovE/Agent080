using Agent080.Core.Interfaces;
using Telegram.Bot.Types;

namespace Agent080.Core.Interfaces
{
    /// <summary>
    /// Combined service for content moderation including keywords and URLs
    /// </summary>
    public interface IMessageModerationService : IKeywordModerationService, IUrlModerationService
    {
        /// <summary>
        /// Checks for non-whitelisted URLs in formatted message links
        /// </summary>
        /// <param name="message">The Telegram message to check</param>
        /// <returns>
        /// A tuple containing:
        /// - bool isNonWhitelisted: Whether a non-whitelisted URL was found
        /// - string domain: The non-whitelisted domain (if found)
        /// - string url: The full URL (if found)
        /// </returns>
        Task<(bool isNonWhitelisted, string domain, string url)> ContainsNonWhitelistedFormattedUrlsAsync(Message message);
    }
}
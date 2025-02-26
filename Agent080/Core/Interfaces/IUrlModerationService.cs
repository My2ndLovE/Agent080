namespace Agent080.Core.Interfaces
{
    /// <summary>
    /// Service for detecting and validating URLs in content
    /// </summary>
    public interface IUrlModerationService
    {
        /// <summary>
        /// Checks if the provided text contains URLs with non-whitelisted domains
        /// </summary>
        /// <param name="content">The text to check</param>
        /// <returns>A tuple containing a flag indicating if non-whitelisted URLs were found, the domain, and the full URL</returns>
        Task<(bool isNonWhitelisted, string domain, string url)> ContainsNonWhitelistedUrlAsync(string content);
    }
}
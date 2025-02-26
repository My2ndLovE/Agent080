namespace Agent080.Core.Interfaces
{
    /// <summary>
    /// Service for detecting prohibited keywords in content
    /// </summary>
    public interface IKeywordModerationService
    {
        /// <summary>
        /// Checks if the provided text contains any prohibited keywords
        /// </summary>
        /// <param name="content">The text to check</param>
        /// <returns>A tuple containing a flag indicating if prohibited content was found and the matched pattern</returns>
        Task<(bool isProhibited, string matchedPattern)> ContainsProhibitedKeywordsAsync(string content);
    }
}
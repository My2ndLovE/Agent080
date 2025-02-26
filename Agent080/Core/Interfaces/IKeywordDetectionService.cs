namespace Agent080.Core.Interfaces
{
    /// <summary>
    /// Legacy service for simple keyword detection
    /// </summary>
    public interface IKeywordDetectionService
    {
        /// <summary>
        /// Checks content for prohibited keywords
        /// </summary>
        /// <param name="content">The content to check</param>
        /// <returns>A tuple with a flag indicating if prohibited content was found and the matched pattern</returns>
        (bool isProhibited, string matchedPattern) CheckContent(string content);
    }
}
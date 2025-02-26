// Add this to a new file: Utilities/Logging/LogEvents.cs
using Microsoft.Extensions.Logging;

namespace Agent080.Utilities.Logging
{
    public static class LogEvents
    {
        // Message moderation events
        public static readonly EventId KeywordDetected = new(1000, "KeywordDetected");
        public static readonly EventId UrlDetected = new(1001, "UrlDetected");

        // Message processing events
        public static readonly EventId MessageReceived = new(2000, "MessageReceived");
        public static readonly EventId MessageDeleted = new(2001, "MessageDeleted");

        // Image analysis events
        public static readonly EventId ImageAnalysisStarted = new(3000, "ImageAnalysisStarted");
        public static readonly EventId ImageAnalysisCompleted = new(3001, "ImageAnalysisCompleted");
        public static readonly EventId TextExtracted = new(3002, "TextExtracted");

        // User management events
        public static readonly EventId UserBanned = new(4000, "UserBanned");

        // Service events
        public static readonly EventId ServiceStarted = new(5000, "ServiceStarted");
        public static readonly EventId ServiceStopped = new(5001, "ServiceStopped");
    }
}
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Agent080.Core.Models.Entities;
using Agent080.Core.Exceptions;
using Agent080.Infrastructure.Repositories;
using Agent080.Services.Vision;
using Agent080.Core.Interfaces;
using Agent080.Utilities.Logging;
using Agent080.Utilities.ExceptionHandling;

namespace Agent080.Webhooks
{
    public class WebhookProcessor : IHostedService
    {
        private readonly ILogger<WebhookProcessor> _logger;
        private readonly Channel<Update> _updateChannel;
        private readonly ITelegramBotClient _telegramBotClient;
        private readonly ComputerVisionService _computerVisionService;
        private readonly BannedUsersRepository _bannedUsersRepository;
        private readonly MessageTrackingRepository _messageTrackingRepository;
        private readonly IMessageModerationService _messageModerationService;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ExceptionHandlingMiddleware _exceptionHandler;

        private readonly ConcurrentDictionary<(long ChatId, long UserId), (DateTime StartTime, DateTime LastCheck, int CurrentDelay, HashSet<int> ProcessedMessageIds)> _activeDeleteSessions = new();

        private const int InitialDelaySeconds = 10;
        private const int MaxTotalTimeSeconds = 60;
        private const int MaxRetryAttempts = 3;
        private const int BatchSize = 100;

        public WebhookProcessor(
            ILogger<WebhookProcessor> logger,
            Channel<Update> updateChannel,
            ITelegramBotClient telegramBotClient,
            ComputerVisionService computerVisionService,
            BannedUsersRepository bannedUsersRepository,
            MessageTrackingRepository messageTrackingRepository,
            IMessageModerationService messageModerationService,
            IHostApplicationLifetime applicationLifetime,
            ExceptionHandlingMiddleware exceptionHandler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
            _telegramBotClient = telegramBotClient ?? throw new ArgumentNullException(nameof(telegramBotClient));
            _computerVisionService = computerVisionService ?? throw new ArgumentNullException(nameof(computerVisionService));
            _bannedUsersRepository = bannedUsersRepository ?? throw new ArgumentNullException(nameof(bannedUsersRepository));
            _messageTrackingRepository = messageTrackingRepository ?? throw new ArgumentNullException(nameof(messageTrackingRepository));
            _messageModerationService = messageModerationService ?? throw new ArgumentNullException(nameof(messageModerationService));
            _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(LogEvents.ServiceStarted, "WebhookProcessor service starting");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(LogEvents.ServiceStopped, "WebhookProcessor service stopping");
            return Task.CompletedTask;
        }

        public async Task EnqueueUpdate(Update update)
        {
            ArgumentNullException.ThrowIfNull(update, nameof(update));

            await _exceptionHandler.ExecuteWithErrorHandlingAsync(async () =>
            {
                if (update.Message == null && update.EditedMessage == null)
                {
                    _logger.LogWarning("Update contains no message or edited message");
                    return false;
                }

                var message = update.Message ?? update.EditedMessage;
                var chatId = message.Chat.Id;
                var userId = message.From?.Id ?? 0;

                // Skip processing for anonymous admin messages
                if (message.From?.Username == "GroupAnonymousBot" || (message.SenderChat != null && message.SenderChat.Id == message.Chat.Id))
                {
                    _logger.LogInformation("Skipping anonymous admin message in chat {ChatId}", chatId);
                    return false;
                }

                // Skip processing for privileged users
                var chatMember = await _telegramBotClient.GetChatMember(chatId, userId);
                if (chatMember.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator)
                {
                    _logger.LogInformation("Skipping privileged user {UserId} in chat {ChatId}", userId, chatId);
                    return false;
                }

                // Track message immediately
                await _messageTrackingRepository.AddMessageAsync(chatId, userId, message.MessageId, message.Chat.Title ?? "Private Chat");

                // Process the message
                await ProcessMessageByType(message);
                await _updateChannel.Writer.WriteAsync(update, _applicationLifetime.ApplicationStopping);

                return true;
            }, "EnqueueUpdate");

            return;
        }

        private async Task ProcessMessageByType(Message message)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(message));

            switch (message.Type)
            {
                case MessageType.Photo:
                    await ProcessPhotoMessage(message);
                    break;
                case MessageType.Text:
                    await ProcessTextMessage(message);
                    break;
                case MessageType.Video:
                case MessageType.Animation:
                case MessageType.Document:
                    await CheckAndHandleCaptionAsync(message);
                    break;
                default:
                    _logger.LogDebug("Skipping unsupported message type: {MessageType}", message.Type);
                    break;
            }
        }

        private async Task ProcessPhotoMessage(Message message)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(message));
            var photos = message.Photo;

            if (photos == null || !photos.Any())
            {
                _logger.LogWarning(LogEvents.MessageReceived, "No photos found in message ID: {MessageId}", message.MessageId);
                return;
            }

            await _exceptionHandler.ExecuteWithErrorHandlingAsync(async () =>
            {
                // Get the highest quality photo
                var photo = photos.OrderByDescending(p => p.Width * p.Height).First();
                _logger.LogInformation(
                    LogEvents.ImageAnalysisStarted,
                    "Processing photo - MessageId: {MessageId}, FileId: {FileId}, Size: {Width}x{Height}",
                    message.MessageId,
                    photo.FileId,
                    photo.Width,
                    photo.Height);

                // Check caption
                var (captionHasProhibited, captionPattern, captionText) = await CheckMessageCaptionAsync(message);
                if (captionHasProhibited)
                {
                    _logger.LogInformation(
                        LogEvents.KeywordDetected,
                        "Prohibited content found in caption - MessageId: {MessageId}, Pattern: {Pattern}",
                        message.MessageId,
                        captionPattern);

                    // Check if it's a URL or keyword match
                    if (captionPattern.StartsWith("Non-whitelisted URL domain:"))
                    {
                        // For URL domain issues, just delete this message
                        await DeleteMessageSafely(message.Chat.Id, message.MessageId);

                        // Update the message status
                        await _messageTrackingRepository.UpdateMessageStatusAsync(
                            message.Chat.Id,
                            new[] { message.MessageId },
                            message.From?.Id ?? 0,
                            MessageStatus.Deleted
                        );
                    }
                    else
                    {
                        // For prohibited keywords, start deletion session
                        await StartDeletionSession(
                            message.Chat.Id,
                            message.From!.Id,
                            message.From.Username,
                            captionPattern!,
                            captionText!,
                            message.Chat.Title ?? "Private Chat"
                        );
                    }
                    return true;
                }

                // Process photo content
                _logger.LogInformation(
                    LogEvents.ImageAnalysisStarted,
                    "Starting photo content analysis - MessageId: {MessageId}, FileId: {FileId}",
                    message.MessageId,
                    photo.FileId);

                var (hasProhibitedText, extractedText, imagePattern) = await ProcessPhotoContent(photo);

                _logger.LogInformation(
                    LogEvents.ImageAnalysisCompleted,
                    "Photo analysis complete - MessageId: {MessageId}, HasProhibitedText: {HasProhibited}, Pattern: {Pattern}, ExtractedText: {Text}",
                    message.MessageId,
                    hasProhibitedText,
                    imagePattern ?? "none",
                    extractedText);

                if (hasProhibitedText)
                {
                    // Check if it's a URL or keyword match in the image text
                    if (imagePattern.StartsWith("Non-whitelisted URL domain:"))
                    {
                        // For URL domain issues, just delete this message
                        await DeleteMessageSafely(message.Chat.Id, message.MessageId);

                        // Update the message status
                        await _messageTrackingRepository.UpdateMessageStatusAsync(
                            message.Chat.Id,
                            new[] { message.MessageId },
                            message.From?.Id ?? 0,
                            MessageStatus.Deleted
                        );
                    }
                    else
                    {
                        // For prohibited keywords, start deletion session
                        await StartDeletionSession(
                            message.Chat.Id,
                            message.From!.Id,
                            message.From.Username,
                            imagePattern,
                            extractedText,
                            message.Chat.Title ?? "Private Chat"
                        );
                    }
                }

                return true;
            }, "ProcessPhotoMessage");
        }

        private async Task ProcessTextMessage(Message message)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(message));
            var text = message.Text ?? string.Empty;

            _logger.LogInformation(LogEvents.MessageReceived, "Processing text message: {TextLength} characters", text.Length);

            // Check for prohibited keywords
            var (isProhibited, matchedPattern) = await _messageModerationService.ContainsProhibitedKeywordsAsync(text);

            if (isProhibited)
            {
                _logger.LogInformation(
                    LogEvents.KeywordDetected,
                    "Prohibited keyword detected: {Pattern} in message from user {UserId}",
                    matchedPattern,
                    message.From?.Id ?? 0);

                await StartDeletionSession(
                    message.Chat.Id,
                    message.From!.Id,
                    message.From.Username,
                    matchedPattern,
                    text,
                    message.Chat.Title ?? "Private Chat"
                );
                return; // Exit early if prohibited content found
            }

            // Check for non-whitelisted URLs in plain text
            var (isNonWhitelisted, domain, url) = await _messageModerationService.ContainsNonWhitelistedUrlAsync(text);

            // Also check for formatted links
            if (!isNonWhitelisted && message.Entities != null && message.Entities.Any())
            {
                (isNonWhitelisted, domain, url) = await _messageModerationService.ContainsNonWhitelistedFormattedUrlsAsync(message);
            }

            if (isNonWhitelisted)
            {
                _logger.LogInformation(
                    LogEvents.UrlDetected,
                    "Non-whitelisted URL detected. Domain: {Domain}, URL: {Url}",
                    domain,
                    url
                );

                // Only delete the current message without starting a deletion session
                await DeleteMessageSafely(message.Chat.Id, message.MessageId);

                // Update the message status
                await _messageTrackingRepository.UpdateMessageStatusAsync(
                    message.Chat.Id,
                    new[] { message.MessageId },
                    message.From!.Id,
                    MessageStatus.Deleted
                );
            }
        }

        private async Task StartDeletionSession(long chatId, long userId, string? username, string pattern, string text, string chatTitle)
        {
            var now = DateTime.UtcNow;
            var sessionKey = (chatId, userId);

            // Add or update session
            _activeDeleteSessions.AddOrUpdate(
                sessionKey,
                // Add new session
                _ => (now, now, InitialDelaySeconds, new HashSet<int>()),
                // Update existing session
                (_, existing) => (existing.StartTime, now, InitialDelaySeconds, existing.ProcessedMessageIds)
            );

            // Start the deletion cycle
            _ = RunDeletionCycle(chatId, userId, username, pattern, text, chatTitle);
        }

        private async Task RunDeletionCycle(long chatId, long userId, string? username, string pattern, string text, string chatTitle)
        {
            var sessionKey = (chatId, userId);

            // Create a persistent logging scope for the entire deletion cycle
            using var deletionScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["DeletionSessionId"] = Guid.NewGuid().ToString(),
                ["ChatId"] = chatId,
                ["UserId"] = userId,
                ["Username"] = username ?? "Unknown",
                ["ChatTitle"] = chatTitle,
                ["Pattern"] = pattern
            });

            _logger.LogInformation(
                LogEvents.MessageDeleted,
                "Starting deletion cycle for user {UserId} in chat {ChatId} due to pattern: {Pattern}",
                userId, chatId, pattern
            );

            await _exceptionHandler.ExecuteWithErrorHandlingAsync(async () =>
            {
                while (_activeDeleteSessions.TryGetValue(sessionKey, out var session))
                {
                    var elapsedTime = (DateTime.UtcNow - session.StartTime).TotalSeconds;

                    // Check if we've exceeded the max total time
                    if (elapsedTime > MaxTotalTimeSeconds)
                    {
                        _activeDeleteSessions.TryRemove(sessionKey, out _);
                        _logger.LogInformation(
                            "Deletion session timed out after {ElapsedSeconds:F1} seconds for user {UserId} in chat {ChatId}",
                            elapsedTime, userId, chatId
                        );
                        break;
                    }

                    try
                    {
                        _logger.LogDebug(
                            "Fetching messages for deletion cycle. Elapsed time: {ElapsedSeconds:F1}s, Current delay: {CurrentDelay}s",
                            elapsedTime, session.CurrentDelay
                        );

                        // Get all messages that haven't been processed yet
                        var allMessages = await _messageTrackingRepository.GetUserMessagesAsync(
                            chatId,
                            userId,
                            TimeSpan.FromHours(36),
                            MessageStatus.Active
                        );

                        var newMessages = allMessages
                            .Where(msgId => !session.ProcessedMessageIds.Contains(msgId))
                            .ToList();

                        if (newMessages.Any())
                        {
                            _logger.LogInformation(
                                "Processing {Count} new messages for deletion. Total processed: {TotalProcessed}",
                                newMessages.Count,
                                session.ProcessedMessageIds.Count
                            );

                            // Check if this is the first batch of messages
                            bool isFirstBatch = !session.ProcessedMessageIds.Any();
                            if (isFirstBatch)
                            {
                                _logger.LogInformation(
                                    LogEvents.UserBanned,
                                    "First batch detected - initiating user ban process for user {UserId}",
                                    userId
                                );
                            }

                            // Reset delay to initial value when new messages are found
                            var previousDelay = session.CurrentDelay;
                            _activeDeleteSessions.AddOrUpdate(
                                sessionKey,
                                session,
                                (_, existing) => (
                                    existing.StartTime,
                                    DateTime.UtcNow,
                                    InitialDelaySeconds,
                                    existing.ProcessedMessageIds
                                )
                            );

                            _logger.LogDebug(
                                "Reset deletion delay from {PreviousDelay}s to {NewDelay}s",
                                previousDelay,
                                InitialDelaySeconds
                            );

                            // Ban user if this is the first batch
                            if (isFirstBatch)
                            {
                                await BanUser(chatId, userId, username, pattern, text, chatTitle);
                            }

                            // Delete messages in batches
                            for (int i = 0; i < newMessages.Count; i += BatchSize)
                            {
                                var batch = newMessages.Skip(i).Take(BatchSize).ToList();
                                _logger.LogInformation(
                                    "Processing deletion batch {BatchNumber} of {TotalBatches} ({BatchSize} messages)",
                                    (i / BatchSize) + 1,
                                    Math.Ceiling(newMessages.Count / (double)BatchSize),
                                    batch.Count
                                );

                                await DeleteMessageBatchSafely(chatId, userId, batch);

                                foreach (var messageId in batch)
                                {
                                    session.ProcessedMessageIds.Add(messageId);
                                }
                            }
                        }
                        else
                        {
                            // No new messages found, double the delay
                            var newDelay = session.CurrentDelay * 2;
                            var remainingTime = MaxTotalTimeSeconds - elapsedTime;

                            _logger.LogDebug(
                                "No new messages found. Current delay: {CurrentDelay}s, New delay: {NewDelay}s, Remaining time: {RemainingTime:F1}s",
                                session.CurrentDelay,
                                newDelay,
                                remainingTime
                            );

                            if (newDelay + elapsedTime > MaxTotalTimeSeconds)
                            {
                                _activeDeleteSessions.TryRemove(sessionKey, out _);
                                _logger.LogInformation(
                                    "Deletion session completed for user {UserId} in chat {ChatId}. Total messages processed: {TotalProcessed}",
                                    userId,
                                    chatId,
                                    session.ProcessedMessageIds.Count
                                );
                                break;
                            }

                            _activeDeleteSessions.AddOrUpdate(
                                sessionKey,
                                session,
                                (_, existing) => (
                                    existing.StartTime,
                                    DateTime.UtcNow,
                                    newDelay,
                                    existing.ProcessedMessageIds
                                )
                            );

                            _logger.LogDebug(
                                "Waiting {Delay} seconds before next check",
                                newDelay
                            );
                        }

                        await Task.Delay(TimeSpan.FromSeconds(session.CurrentDelay));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error in deletion cycle for user {UserId} in chat {ChatId}. Elapsed time: {ElapsedTime:F1}s",
                            userId, chatId, elapsedTime
                        );
                    }
                }

                _logger.LogInformation(
                    "Deletion cycle completed for user {UserId} in chat {ChatId}",
                    userId, chatId
                );

                return true;
            }, "RunDeletionCycle");
        }

        private async Task DeleteMessageSafely(long chatId, int messageId)
        {
            try
            {
                await _telegramBotClient.DeleteMessage(chatId, messageId);
                _logger.LogInformation(
                    LogEvents.MessageDeleted,
                    "Successfully deleted message {MessageId} from chat {ChatId}",
                    messageId, chatId
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to delete message {MessageId} from chat {ChatId}",
                    messageId, chatId
                );
            }
        }

        private async Task DeleteMessageBatchSafely(long chatId, long userId, List<int> messageIds, int attemptCount = 0)
        {
            try
            {
                await _telegramBotClient.DeleteMessages(chatId, messageIds);
                await _messageTrackingRepository.UpdateMessageStatusAsync(
                    chatId,
                    messageIds,
                    userId,
                    MessageStatus.Deleted
                );
                _logger.LogInformation(
                    LogEvents.MessageDeleted,
                    "Successfully deleted {Count} messages from chat {ChatId} for user {UserId}. Message IDs: {@MessageIds}",
                    messageIds.Count,
                    chatId,
                    userId,
                    messageIds
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to delete {Count} messages from chat {ChatId} for user {UserId}. Attempt {Attempt}. Message IDs: {@MessageIds}",
                    messageIds.Count,
                    chatId,
                    userId,
                    attemptCount + 1,
                    messageIds
                );

                if (attemptCount < MaxRetryAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attemptCount))); // Exponential backoff
                    await DeleteMessageBatchSafely(chatId, userId, messageIds, attemptCount + 1);
                }
            }
        }

        private async Task BanUser(long chatId, long userId, string? username, string pattern, string text, string chatTitle)
        {
            await _exceptionHandler.ExecuteWithErrorHandlingAsync(async () =>
            {
                var bannedUser = new BannedUser(chatId, userId, chatTitle)
                {
                    Username = username ?? $"User_{userId}",
                    Reason = $"Prohibited content detected: {pattern}",
                    DetectedText = text
                };

                await _telegramBotClient.BanChatMember(
                    chatId: chatId,
                    userId: userId,
                    revokeMessages: true
                );

                await _bannedUsersRepository.AddBannedUserAsync(bannedUser);

                _logger.LogInformation(
                    LogEvents.UserBanned,
                    "Successfully banned user {UserId} ({Username}) from chat {ChatId}",
                    userId, username, chatId
                );

                return true;
            }, "BanUser");
        }

        private async Task<(bool hasProhibitedText, string extractedText, string? pattern)> ProcessPhotoContent(Telegram.Bot.Types.PhotoSize photo)
        {
            ArgumentNullException.ThrowIfNull(photo, nameof(photo));

            return await _exceptionHandler.ExecuteWithErrorHandlingAsync(async () =>
            {
                _logger.LogInformation(LogEvents.ImageAnalysisStarted, "Getting file info for FileId: {FileId}", photo.FileId);

                var file = await _telegramBotClient.GetFile(photo.FileId);
                if (file?.FilePath == null)
                {
                    _logger.LogError("Could not get file info for FileId: {FileId}", photo.FileId);
                    return (false, string.Empty, null);
                }

                _logger.LogInformation(
                    "Downloading file - FileId: {FileId}, FilePath: {FilePath}",
                    photo.FileId,
                    file.FilePath);

                using var memoryStream = new MemoryStream();
                await _telegramBotClient.DownloadFile(file.FilePath, memoryStream);
                memoryStream.Position = 0;

                _logger.LogInformation(
                    LogEvents.ImageAnalysisStarted,
                    "Starting computer vision analysis for FileId: {FileId}, Stream size: {Size} bytes",
                    photo.FileId,
                    memoryStream.Length);

                var result = await _computerVisionService.AnalyzeImage(memoryStream, photo.FileId);

                _logger.LogInformation(
                    LogEvents.ImageAnalysisCompleted,
                    "Computer vision analysis complete - FileId: {FileId}, HasProhibitedText: {HasProhibited}, Pattern: {Pattern}, ExtractedText: {Text}",
                    photo.FileId,
                    result.hasProhibitedText,
                    result.matchedPattern ?? "none",
                    result.extractedText);

                return result;
            }, "ProcessPhotoContent");
        }

        private async Task<(bool hasProhibited, string? pattern, string? text)> CheckMessageCaptionAsync(Message message)
        {
            if (string.IsNullOrEmpty(message.Caption))
                return (false, null, null);

            // Check caption for prohibited keywords using the new message moderation service
            var (isProhibited, pattern) = await _messageModerationService.ContainsProhibitedKeywordsAsync(message.Caption);

            // Also check for non-whitelisted URLs in the caption
            if (!isProhibited)
            {
                var (isNonWhitelisted, domain, url) = await _messageModerationService.ContainsNonWhitelistedUrlAsync(message.Caption);
                if (isNonWhitelisted)
                {
                    return (true, $"Non-whitelisted URL domain: {domain}", message.Caption);
                }
            }

            return (isProhibited, pattern, message.Caption);
        }

        private async Task CheckAndHandleCaptionAsync(Message message)
        {
            var (hasProhibited, pattern, text) = await CheckMessageCaptionAsync(message);
            if (hasProhibited)
            {
                if (pattern!.StartsWith("Non-whitelisted URL domain:"))
                {
                    // For URL domain issues, just delete this message
                    await DeleteMessageSafely(message.Chat.Id, message.MessageId);

                    // Update the message status
                    await _messageTrackingRepository.UpdateMessageStatusAsync(
                        message.Chat.Id,
                        new[] { message.MessageId },
                        message.From?.Id ?? 0,
                        MessageStatus.Deleted
                    );
                }
                else
                {
                    // For prohibited keywords, start deletion session
                    await StartDeletionSession(
                        message.Chat.Id,
                        message.From?.Id ?? 0,
                        message.From?.Username,
                        pattern!,
                        text!,
                        message.Chat.Title ?? "Private Chat"
                    );
                }
            }
        }
    }
}
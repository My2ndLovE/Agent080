using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Threading.Channels;
using Agent080.Core.Interfaces;

namespace Agent080.Services.Vision
{
    public class ComputerVisionService
    {
        private readonly List<ComputerVisionClient> _clients;
        private readonly ILogger<ComputerVisionService> _logger;
        private readonly IMessageModerationService _messageModeration;
        private readonly IMemoryCache _cache;
        private readonly SemaphoreSlim _processingThrottler;
        private readonly Channel<ImageProcessingTask> _processingChannel;
        private int _currentClientIndex = 0;
        private readonly object _lockObject = new object();

        private const int MaxImageSize = 1024;
        private const int CacheExpirationHours = 24;
        private const string CacheKeyPrefix = "vision:";
        private const int MaxConcurrentProcessing = 3;

        private class ImageProcessingTask
        {
            public Stream ImageStream { get; init; }
            public string FileId { get; init; }
            public TaskCompletionSource<(bool, string, string)> CompletionSource { get; } = new();
        }

        public ComputerVisionService(
            ILoggerFactory loggerFactory,
            IMessageModerationService messageModeration,
            IMemoryCache cache)
        {
            _logger = loggerFactory.CreateLogger<ComputerVisionService>();
            _messageModeration = messageModeration;
            _cache = cache;
            _clients = InitializeClients();

            _processingThrottler = new SemaphoreSlim(MaxConcurrentProcessing);
            _processingChannel = Channel.CreateUnbounded<ImageProcessingTask>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Start background processing
            _ = ProcessImagesFromChannel();
        }

        private List<ComputerVisionClient> InitializeClients()
        {
            var clients = new List<ComputerVisionClient>();

            // Primary instance
            var primaryEndpoint = Environment.GetEnvironmentVariable("ComputerVisionEndpoint");
            var primaryKey = Environment.GetEnvironmentVariable("ComputerVisionKey");

            // Secondary instance
            var secondaryEndpoint = Environment.GetEnvironmentVariable("ComputerVisionEndpoint_2");
            var secondaryKey = Environment.GetEnvironmentVariable("ComputerVisionKey_2");

            if (string.IsNullOrEmpty(primaryEndpoint) || string.IsNullOrEmpty(primaryKey))
            {
                throw new InvalidOperationException("Primary Computer Vision configuration is missing");
            }

            // Add primary client
            clients.Add(new ComputerVisionClient(
                new ApiKeyServiceClientCredentials(primaryKey))
            { Endpoint = primaryEndpoint });

            // Add secondary client if configured
            if (!string.IsNullOrEmpty(secondaryEndpoint) && !string.IsNullOrEmpty(secondaryKey))
            {
                clients.Add(new ComputerVisionClient(
                    new ApiKeyServiceClientCredentials(secondaryKey))
                { Endpoint = secondaryEndpoint });
            }

            _logger.LogInformation("Initialized {Count} Computer Vision clients", clients.Count);
            return clients;
        }

        private ComputerVisionClient GetNextClient()
        {
            lock (_lockObject)
            {
                var client = _clients[_currentClientIndex];
                _currentClientIndex = (_currentClientIndex + 1) % _clients.Count;

                _logger.LogInformation("Using Computer Vision client {Index} of {Total}",
                    _currentClientIndex + 1, _clients.Count);

                return client;
            }
        }

        private async Task ProcessImagesFromChannel()
        {
            try
            {
                await foreach (var task in _processingChannel.Reader.ReadAllAsync())
                {
                    try
                    {
                        await _processingThrottler.WaitAsync();

                        _ = ProcessSingleImage(task).ContinueWith(t =>
                        {
                            _processingThrottler.Release();
                            if (t.IsFaulted && t.Exception != null)
                            {
                                task.CompletionSource.SetException(t.Exception);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _processingThrottler.Release();
                        task.CompletionSource.SetException(ex);
                        _logger.LogError(ex, "Error initiating image processing for file ID: {FileId}", task.FileId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in image processing channel");
            }
        }

        private async Task ProcessSingleImage(ImageProcessingTask task)
        {
            try
            {
                var result = await AnalyzeImageInternal(task.ImageStream, task.FileId);
                task.CompletionSource.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing image {FileId}", task.FileId);
                task.CompletionSource.SetException(ex);
            }
        }

        private async Task<string> CalculateImageHash(Stream imageStream)
        {
            try
            {
                using var image = await Image.LoadAsync(imageStream);
                using var memoryStream = new MemoryStream();
                await image.SaveAsPngAsync(memoryStream); // Convert to PNG for consistent hashing
                var imageBytes = memoryStream.ToArray();

                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(imageBytes);
                return Convert.ToBase64String(hashBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating image hash");
                return Guid.NewGuid().ToString(); // Fallback to ensure uniqueness
            }
            finally
            {
                imageStream.Position = 0; // Reset stream position
            }
        }

        public async Task<(bool hasProhibitedText, string extractedText, string matchedPattern)> AnalyzeImage(
    Stream imageStream,
    string fileId)
        {
            try
            {
                // Create a copy of the stream for hashing
                var imageStreamCopy = new MemoryStream();
                await imageStream.CopyToAsync(imageStreamCopy);
                imageStreamCopy.Position = 0;
                imageStream.Position = 0;

                // Calculate content-based hash
                var contentHash = await CalculateImageHash(imageStreamCopy);
                var cacheKey = $"{CacheKeyPrefix}{contentHash}";

                _logger.LogInformation(
                    "Image analysis - FileId: {FileId}, ContentHash: {ContentHash}",
                    fileId,
                    contentHash);

                string extractedText;

                // Try to get cached extracted text
                if (_cache.TryGetValue<ExtractedTextResult>(cacheKey, out var cachedResult))
                {
                    _logger.LogInformation(
                        "Retrieved text from cache - FileId: {FileId}, ContentHash: {ContentHash}",
                        fileId,
                        contentHash);

                    extractedText = cachedResult.ExtractedText;
                }
                else
                {
                    // Perform full OCR analysis if not cached
                    using var optimizedImageStream = await OptimizeImageForProcessing(imageStream);
                    var client = GetNextClient();

                    _logger.LogInformation(
                        "Starting new image analysis - FileId: {FileId}, ContentHash: {ContentHash}",
                        fileId,
                        contentHash);

                    var textHeaders = await client.ReadInStreamAsync(optimizedImageStream);
                    var operationId = textHeaders.OperationLocation.Split('/').Last();

                    var readResult = await WaitForOperationCompletion(client, operationId);

                    if (readResult.Status == OperationStatusCodes.Succeeded)
                    {
                        extractedText = ProcessReadResult(readResult);
                        _logger.LogInformation(
                            "Text extracted - FileId: {FileId}, ContentHash: {ContentHash}, Text: {Text}",
                            fileId,
                            contentHash,
                            extractedText);

                        // Cache only the extracted text
                        var textResult = new ExtractedTextResult(extractedText);
                        var cacheOptions = new MemoryCacheEntryOptions()
                            .SetSlidingExpiration(TimeSpan.FromHours(CacheExpirationHours))
                            .SetSize(1);

                        _cache.Set(cacheKey, textResult, cacheOptions);
                    }
                    else
                    {
                        throw new Exception($"Text extraction failed with status: {readResult.Status}");
                    }
                }

                // Always check against current keyword list
                var (isProhibited, matchedPattern) = await _messageModeration.ContainsProhibitedKeywordsAsync(extractedText);

                // Also check for non-whitelisted URLs in the extracted text
                if (!isProhibited)
                {
                    var (isNonWhitelisted, domain, url) = await _messageModeration.ContainsNonWhitelistedUrlAsync(extractedText);
                    if (isNonWhitelisted)
                    {
                        isProhibited = true;
                        matchedPattern = $"Non-whitelisted URL domain: {domain}";
                    }
                }

                return (isProhibited, extractedText, matchedPattern);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing image: {Message}", ex.Message);
                throw;
            }
        }

        private async Task<(bool hasProhibitedText, string extractedText, string matchedPattern)> AnalyzeImageInternal(
    Stream imageStream,
    string fileId)
        {
            try
            {
                using var optimizedImageStream = await OptimizeImageForProcessing(imageStream);
                var client = GetNextClient();

                _logger.LogInformation("Starting image analysis with Read API for file ID: {FileId}", fileId);

                var textHeaders = await client.ReadInStreamAsync(optimizedImageStream);
                var operationId = textHeaders.OperationLocation.Split('/').Last();

                var readResult = await WaitForOperationCompletion(client, operationId);

                if (readResult.Status == OperationStatusCodes.Succeeded)
                {
                    var extractedText = ProcessReadResult(readResult);
                    _logger.LogInformation("Extracted text: {Text}", extractedText);

                    // Calculate content-based hash for caching
                    var contentHash = Guid.NewGuid().ToString(); // Simplified for this internal method
                    var cacheKey = $"{CacheKeyPrefix}{contentHash}";

                    // Cache only the extracted text
                    var textResult = new ExtractedTextResult(extractedText);
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromHours(CacheExpirationHours))
                        .SetSize(1);

                    _cache.Set(cacheKey, textResult, cacheOptions);

                    // Always check against current keyword list
                    var (isProhibited, matchedPattern) = await _messageModeration.ContainsProhibitedKeywordsAsync(extractedText);

                    // Also check for non-whitelisted URLs in the extracted text
                    if (!isProhibited)
                    {
                        var (isNonWhitelisted, domain, url) = await _messageModeration.ContainsNonWhitelistedUrlAsync(extractedText);
                        if (isNonWhitelisted)
                        {
                            isProhibited = true;
                            matchedPattern = $"Non-whitelisted URL domain: {domain}";
                        }
                    }

                    return (isProhibited, extractedText, matchedPattern);
                }

                throw new Exception($"Text extraction failed with status: {readResult.Status}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing image: {Message}", ex.Message);
                throw;
            }
        }

        private async Task<Stream> OptimizeImageForProcessing(Stream imageStream)
        {
            using var image = await Image.LoadAsync(imageStream);

            if (image.Width > MaxImageSize || image.Height > MaxImageSize)
            {
                var resizeOptions = new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxImageSize, MaxImageSize)
                };

                image.Mutate(x => x.Resize(resizeOptions));
            }

            var optimizedStream = new MemoryStream();
            await image.SaveAsJpegAsync(optimizedStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
            {
                Quality = 85
            });

            optimizedStream.Position = 0;
            return optimizedStream;
        }

        private async Task<ReadOperationResult> WaitForOperationCompletion(ComputerVisionClient client, string operationId)
        {
            ReadOperationResult readResult;
            var attempts = 0;
            const int maxAttempts = 8;
            const int initialDelayMs = 300;
            const int maxDelaySeconds = 5;

            do
            {
                if (attempts > 0)
                {
                    var delayMs = Math.Min(
                        initialDelayMs * Math.Pow(2, attempts - 1),
                        maxDelaySeconds * 1000
                    );
                    await Task.Delay((int)delayMs);
                }

                readResult = await client.GetReadResultAsync(Guid.Parse(operationId));
                attempts++;

                _logger.LogDebug(
                    "Polling attempt {Attempt}/{MaxAttempts} for operation {OperationId}. Status: {Status}",
                    attempts, maxAttempts, operationId, readResult.Status
                );

            } while ((readResult.Status == OperationStatusCodes.Running ||
                      readResult.Status == OperationStatusCodes.NotStarted) &&
                     attempts < maxAttempts);

            if (attempts >= maxAttempts)
            {
                _logger.LogWarning(
                    "Max polling attempts ({MaxAttempts}) reached for operation {OperationId}",
                    maxAttempts, operationId
                );
            }

            return readResult;
        }

        private string ProcessReadResult(ReadOperationResult readResult)
        {
            var detectedText = readResult.AnalyzeResult.ReadResults
                .SelectMany(page => page.Lines)
                .Select(line => line.Text)
                .ToList();

            return string.Join(" ", detectedText);
        }
    }

    public record ExtractedTextResult(string ExtractedText);
}
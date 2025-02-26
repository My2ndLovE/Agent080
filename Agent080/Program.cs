using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Telegram.Bot;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Telegram.Bot.Types;
using System.Net.Http;
using Polly;
using Polly.Extensions.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Agent080.Services.Storage;
using Agent080.Services.Vision;
using Agent080.Services.Moderation;
using Agent080.Infrastructure.Configuration;
using Agent080.Infrastructure.Repositories;
using Agent080.Webhooks;
using Agent080.Core.Interfaces;
using Agent080.Utilities.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Register Options
        services.Configure<StorageOptions>(options =>
        {
            // Map from environment variables to the options model
            options.CosmosConnectionString = configuration["CosmosDBConnection"] ?? string.Empty;
            options.DatabaseName = configuration["DatabaseName"] ?? string.Empty;
            options.ContainerName = configuration["ContainerName"] ?? string.Empty;
            options.TableStorageConnectionString = configuration["AzureWebJobsStorage"] ?? string.Empty;
            options.MessagesTableName = configuration["MessagesTableName"] ?? string.Empty;
        });

        services.Configure<ComputerVisionOptions>(options =>
        {
            options.PrimaryEndpoint = configuration["ComputerVisionEndpoint"] ?? string.Empty;
            options.PrimaryKey = configuration["ComputerVisionKey"] ?? string.Empty;
            options.SecondaryEndpoint = configuration["ComputerVisionEndpoint_2"];
            options.SecondaryKey = configuration["ComputerVisionKey_2"];

            if (int.TryParse(configuration["ComputerVision:CacheExpirationHours"], out int cacheHours))
                options.CacheExpirationHours = cacheHours;

            if (int.TryParse(configuration["ComputerVision:MaxImageSize"], out int maxSize))
                options.MaxImageSize = maxSize;

            if (int.TryParse(configuration["ComputerVision:MaxConcurrentProcessing"], out int maxConcurrent))
                options.MaxConcurrentProcessing = maxConcurrent;
        });

        services.Configure<ModerationOptions>(options =>
        {
            options.ApiEndpoint = configuration["ModerationKeyword:Endpoint"] ?? string.Empty;
            options.SubscriptionKey = configuration["ModerationKeyword:SubscriptionKey"] ?? string.Empty;

            if (int.TryParse(configuration["ModerationKeyword:CacheMinutes"], out int cacheMinutes))
                options.CacheMinutes = cacheMinutes;
        });

        // Register Storage services
        services.AddSingleton<CosmosDbService>();
        services.AddSingleton<TableStorageService>();

        // Register repositories
        services.AddSingleton<BannedUsersRepository>();
        services.AddSingleton<MessageTrackingRepository>();

        // Register memory cache for Computer Vision Service
        services.AddMemoryCache();

        // Register MessageModerationService with HttpClient and retry policy
        services.AddHttpClient<IMessageModerationService, MessageModerationService>()
            .AddPolicyHandler(GetRetryPolicy());

        // Register interfaces for service implementations
        services.AddSingleton<IKeywordModerationService>(sp =>
            sp.GetRequiredService<IMessageModerationService>());
        services.AddSingleton<IUrlModerationService>(sp =>
            sp.GetRequiredService<IMessageModerationService>());

        // Keep the fallback configuration for backward compatibility
        services.AddSingleton<ProhibitedKeywordConfiguration>();
        services.AddSingleton<IKeywordDetectionService, KeywordDetectionService>();

        // Register Computer Vision service
        services.AddSingleton<ComputerVisionService>();

        // Register TelegramBotClient
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = context.Configuration["TelegramBotToken"]
                ?? throw new InvalidOperationException("TelegramBotToken is not configured");
            return new TelegramBotClient(token);
        });

        // WebhookProcessor services
        services.AddSingleton<WebhookProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookProcessor>());
        services.AddSingleton(Channel.CreateUnbounded<Update>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        }));

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
        });
    })
    .Build();

// Create a retry policy for HttpClient
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError() // HttpRequestException, 5XX, and 408 status codes
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // 429 status code
        .WaitAndRetryAsync(
            3, // Retry 3 times
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) // Exponential backoff: 2, 4, 8 seconds
        );
}

host.Run();
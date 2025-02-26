using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot.Types;
using Agent080.Utilities.Serialization;

namespace Agent080.Webhooks
{
    public class TelegramWebhookFunction
    {
        private readonly ILogger<TelegramWebhookFunction> _logger;
        private readonly WebhookProcessor _webhookProcessor;
        private readonly JsonSerializerOptions _jsonOptions;

        public TelegramWebhookFunction(
            ILogger<TelegramWebhookFunction> logger,
            WebhookProcessor webhookProcessor)
        {
            _logger = logger;
            _webhookProcessor = webhookProcessor;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };
        }

        [Function("TelegramWebhook")]
        public async Task<HttpResponseData> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("Webhook received. Processing request...");

            // Acknowledge Telegram immediately
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(string.Empty);

            try
            {
                // Read the request body
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                //_logger.LogInformation("Request body: {Body}", requestBody);

                // Deserialize the update using System.Text.Json with SnakeCaseNamingPolicy
                var update = JsonSerializer.Deserialize<Update>(requestBody, _jsonOptions);

                if (update == null)
                {
                    _logger.LogError("Failed to deserialize update");
                    return response;
                }

                _logger.LogInformation("Deserialized update {UpdateId}, Type: {UpdateType}", update.Id, update.Type);

                // Enqueue the update for processing
                await _webhookProcessor.EnqueueUpdate(update);
                _logger.LogInformation("Successfully processed webhook for update {UpdateId}", update.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing webhook request");
            }

            return response;
        }
    }
}
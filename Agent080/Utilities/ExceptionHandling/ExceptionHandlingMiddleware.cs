using Agent080.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agent080.Utilities.ExceptionHandling
{
    public class ExceptionHandlingMiddleware
    {
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger)
        {
            _logger = logger;
        }

        public async Task<TResult> ExecuteWithErrorHandlingAsync<TResult>(
            Func<Task<TResult>> operation,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await operation();
            }
            catch (ModerationException ex)
            {
                _logger.LogError(ex, "Moderation error during {OperationName}: {Message}", operationName, ex.Message);
                throw; // Rethrow as this is a known exception type
            }
            catch (ComputerVisionException ex)
            {
                _logger.LogError(ex, "Computer Vision error during {OperationName}: {Message}", operationName, ex.Message);
                throw; // Rethrow as this is a known exception type
            }
            catch (StorageException ex)
            {
                _logger.LogError(ex, "Storage error during {OperationName}: {Message}", operationName, ex.Message);
                throw; // Rethrow as this is a known exception type
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during {OperationName}: {Message}", operationName, ex.Message);

                // Convert to appropriate domain exception or rethrow
                throw;
            }
        }
    }
}
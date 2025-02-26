# Agent080 - Telegram Content Moderation Bot

A .NET-based Azure Function application that provides content moderation for Telegram groups.

## Features

- Text moderation with keyword filtering
- Image moderation with OCR text extraction
- URL whitelisting
- User management with automatic banning
- Message tracking and cleanup

## Architecture

The project follows a clean architecture approach with:
- Core: Domain models and interfaces
- Services: Business logic implementation
- Infrastructure: Data access and external integrations
- Webhooks: Telegram bot webhook handlers

## Setup Instructions

### Prerequisites

- .NET 9.0 SDK
- Azure Functions Core Tools
- Azure subscription
- Telegram Bot Token (from BotFather)

### Configuration

Configure the following settings in `local.settings.json`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "YOUR_STORAGE_CONNECTION_STRING",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "TelegramBotToken": "YOUR_TELEGRAM_BOT_TOKEN",
    "CosmosDBConnection": "YOUR_COSMOS_DB_CONNECTION",
    "DatabaseName": "TelegramBotDB",
    "ContainerName": "BannedUsers",
    "MessagesTableName": "TelegramMessages",
    "ModerationKeyword:Endpoint": "YOUR_MODERATION_API_ENDPOINT",
    "ModerationKeyword:SubscriptionKey": "YOUR_MODERATION_API_KEY",
    "ModerationKeyword:CacheMinutes": "5",
    "ComputerVisionEndpoint": "YOUR_COMPUTER_VISION_ENDPOINT",
    "ComputerVisionKey": "YOUR_COMPUTER_VISION_KEY"
  }
}
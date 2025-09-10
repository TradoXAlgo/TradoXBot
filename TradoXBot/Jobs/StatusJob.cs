using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class StatusJob : IJob
{
    private readonly ILogger<StatusJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly MongoDbService _mongoDbService;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public StatusJob(IConfiguration configuration, ILogger<StatusJob> logger, StoxKartClient stoxKartClient, MongoDbService mongoDbService, HistoricalDataFetcher historicalFetcher)
    {
        _logger = logger;
        _stoxKartClient = stoxKartClient;
        _mongoDbService = mongoDbService;
        _historicalFetcher = historicalFetcher;
        _telegramBot = new TelegramBotClient(configuration["Telegram:ApiKey"]);
        _chatId = configuration["Telegram:ChatId"];
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            _logger.LogInformation("Executing Status Job at {Time}", DateTime.Now);

            var status = await _stoxKartClient.AuthenticateAsync();
            if (!status)
            {
                _logger.LogError("Authentication failed. Aborting swing buy.");
                _ = await _telegramBot.SendMessage(_chatId, "Swing Buy: Authentication failed.");
                return;
            }

            var swingTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            var scalpingTransactions = await _mongoDbService.GetOpenScalpingTransactionsAsync();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");

            var quoteRequests = swingTransactions.Concat(scalpingTransactions)
                .Select(t => tokens.GetValueOrDefault(t.Symbol))
                .Where(t => t != null)
                .Distinct()
                .ToList();
            var quotes = await _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

            var symbolQuotes = new System.Collections.Generic.Dictionary<string, Quote>();
            foreach (var kv in quotes)
            {
                var symbol = swingTransactions.Concat(scalpingTransactions)
                    .FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol) == kv.Key)?.Symbol;
                if (symbol != null)
                    symbolQuotes[symbol] = kv.Value;
            }

            var statusBuilder = new StringBuilder("Portfolio Status:\n");
            bool hasPositions = false;

            foreach (var transaction in swingTransactions)
            {
                hasPositions = true;
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning($"No quote data for swing {transaction.Symbol}. Skipping status.");
                    continue;
                }

                decimal profitLoss = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                decimal profitLossPct = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                statusBuilder.AppendLine($"Swing - {transaction.StockName} ({transaction.Symbol}):\n" +
                                        $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                        $"Current Price: ₹{quote.LastPrice:F2}\n" +
                                        $"Profit/Loss: ₹{profitLoss:F2} ({profitLossPct:F2}%)\n" +
                                        $"Quantity: {transaction.Quantity}\n" +
                                        $"Expiry: {transaction.ExpiryDate:yyyy-MM-dd}\n");
            }

            foreach (var transaction in scalpingTransactions)
            {
                hasPositions = true;
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning($"No quote data for scalping {transaction.Symbol}. Skipping status.");
                    continue;
                }

                decimal profitLoss = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                decimal profitLossPct = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                statusBuilder.AppendLine($"Scalping - {transaction.StockName} ({transaction.Symbol}):\n" +
                                        $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                        $"Current Price: ₹{quote.LastPrice:F2}\n" +
                                        $"Profit/Loss: ₹{profitLoss:F2} ({profitLossPct:F2}%)\n" +
                                        $"Quantity: {transaction.Quantity}\n");
            }

            if (!hasPositions)
            {
                statusBuilder.AppendLine("No open positions.");
            }

            await _telegramBot.SendMessage(_chatId, statusBuilder.ToString());
            _logger.LogInformation("Status report sent to Telegram.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in Status Job: {ex.Message}");
            await _telegramBot.SendMessage(_chatId, $"Status Job Error: {ex.Message}");
        }
    }
}

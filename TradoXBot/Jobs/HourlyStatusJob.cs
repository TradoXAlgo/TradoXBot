using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs
{
    public class HourlyStatusJob : IJob
    {
        private readonly ILogger<HourlyStatusJob> _logger;
        private readonly StoxKartClient _stoxKartClient;
        private readonly HistoricalDataFetcher _historicalFetcher;
        private readonly MongoDbService _mongoDbService;
        private readonly TelegramBotClient _telegramBot;
        private readonly string? _chatId;

        public HourlyStatusJob(IConfiguration configuration, ILogger<HourlyStatusJob> logger,
            StoxKartClient stoxKartClient, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
        {
            _logger = logger;
            _stoxKartClient = stoxKartClient;
            _historicalFetcher = historicalFetcher;
            _mongoDbService = mongoDbService;
            _telegramBot = new TelegramBotClient(configuration["Telegram:ApiKey"]);
            _chatId = configuration["Telegram:ChatId"];
        }

        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                var marketOpen = new TimeSpan(9, 15, 0);
                var marketClose = new TimeSpan(15, 30, 0);
                if (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose || !IsTradingDay(now))
                {
                    _logger.LogInformation("Hourly Status Job skipped: Outside market hours (9:15 AM - 3:30 PM IST) or not a trading day.");
                    return;
                }

                _logger.LogInformation("Executing Hourly Status Job at {Time} IST", now);
                var status = _stoxKartClient.AuthenticateAsync();
                if (!status)
                {
                    _logger.LogError("Authentication failed. Aborting swing buy.");
                    _ = await _telegramBot.SendMessage(_chatId, "Swing Buy: Authentication failed.");
                    return;
                }

                var openSwingTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
                var openScalpingTransactions = await _mongoDbService.GetOpenScalpingTransactionsAsync();
                var openTransactions = openSwingTransactions.Concat(openScalpingTransactions).ToList();
                var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");

                var quoteRequests = openTransactions
                    .Select(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()))
                    .Where(t => t != null)
                    .Distinct()
                    .ToList();
                var quotes = _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

                var symbolQuotes = new Dictionary<string, Quote>();
                foreach (var kv in quotes)
                {
                    var symbol = openTransactions.FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()) == kv.Key)?.Symbol;
                    if (symbol != null)
                        symbolQuotes[symbol] = kv.Value;
                }

                foreach (var transaction in openTransactions)
                {
                    if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                    {
                        _logger.LogWarning("No quote data for {Symbol}. Skipping status update.", transaction.Symbol);
                        continue;
                    }

                    decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14, transaction.ExpiryDate.TimeOfDay == new TimeSpan(14, 30, 0) ? "5m" : "1d");
                    decimal riskPerShare = atr.HasValue ? 2 * atr.Value : transaction.BuyPrice * 0.025m;
                    decimal stopLoss = transaction.BuyPrice - riskPerShare;
                    decimal targetPrice = transaction.BuyPrice + (3 * riskPerShare);
                    decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;

                    string message = $"Hourly Status Update:\n" +
                                    $"Name: {transaction.StockName}\n" +
                                    $"Symbol: {transaction.Symbol}\n" +
                                    $"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST\n" +
                                    $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                    $"Current Price: ₹{quote.LastPrice:F2}\n" +
                                    $"Target: ₹{targetPrice:F2}\n" +
                                    $"Stop Loss: ₹{stopLoss:F2}\n" +
                                    $"Profit/Loss: {profitPercent:F2}%\n" +
                                    $"R:R: 3:1";
                    await _telegramBot.SendMessage(_chatId, message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error in Hourly Status Job: {Message}", ex.Message);
                await _telegramBot.SendMessage(_chatId, $"Hourly Status Job Error: {ex.Message}");
            }
        }

        private static bool IsTradingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date.Date);
        }

        private static readonly List<DateTime> Holidays =
        [
            new DateTime(2025, 2, 26), // Mahashivratri
            new DateTime(2025, 3, 14), // Holi
            new DateTime(2025, 3, 31), // Eid-Ul-Fitr
            new DateTime(2025, 4, 10), // Shri Mahavir Jayanti
            new DateTime(2025, 4, 14), // Dr. Baba Saheb Ambedkar Jayanti
            new DateTime(2025, 4, 18), // Good Friday
            new DateTime(2025, 5, 1), // Maharashtra Day
            new DateTime(2025, 8, 15), // Independence Day
            new DateTime(2025, 8, 27), // Ganesh Chaturthi
            new DateTime(2025, 10, 2), // Mahatma Gandhi Jayanti/Dussehra
            new DateTime(2025, 10, 21), // Diwali Laxmi Pujan
            new DateTime(2025, 10, 22), // Diwali-Balipratipada
            new DateTime(2025, 11, 5), // Prakash Gurpurb Sri Guru Nanak Dev
            new DateTime(2025, 12, 25) // Christmas
        ];
    }
}
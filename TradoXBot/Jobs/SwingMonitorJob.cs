using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class SwingMonitorJob : IJob
{
    private readonly ILogger _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public SwingMonitorJob(IConfiguration configuration, ILogger<SwingMonitorJob> logger,
        StoxKartClient stoxKartClient, HistoricalDataFetcher historicalFetcher,
        MongoDbService mongoDbService)
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

            var status = await _stoxKartClient.AccessTokenKey();
            await _telegramBot.SendMessage(_chatId, status);
            if (status == null) return;

            var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
            var marketOpen = new TimeSpan(9, 15, 0);
            var marketClose = new TimeSpan(15, 30, 0);

            if (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose || !IsTradingDay(now))
            {
                _logger.LogInformation("Swing Monitor skipped: Outside market hours (9:15 AM - 3:30 PM IST) or not a trading day.");
                return;
            }

            _logger.LogInformation("Executing Swing Monitor Job at {Time} IST", now);

            var openTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            if (!openTransactions.Any())
            {
                _logger.LogInformation("No open swing transactions to monitor.");
                return;
            }

            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");
            var quoteRequests = openTransactions
                .Select(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()))
                .Where(t => t != null)
                .Distinct()
                .ToList();
            var quotes = await _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

            var symbolQuotes = new Dictionary<string, Quote>();
            foreach (var kv in quotes)
            {
                var symbol = openTransactions.FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()) == kv.Key)?.Symbol;
                if (symbol != null)
                    symbolQuotes[symbol] = kv.Value;
            }

            foreach (Transaction transaction in openTransactions)
            {
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning("No quote data for {Symbol}. Skipping sell check.", transaction.Symbol);
                    continue;
                }

                decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                bool sell = false;
                string sellReason = "";
                decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14);
                decimal stopLossPrice = transaction.BuyPrice - (atr ?? transaction.BuyPrice * 0.005m);

                // Check if stock was bought today and price is up 2% or more tomorrow
                var today = now.Date;
                var yesterday = today.AddDays(-1);
                bool isBoughtYesterday = transaction.BuyDate.Date == yesterday;
                bool isTomorrowCheck = now.Date > transaction.BuyDate.Date;

                if (isBoughtYesterday && isTomorrowCheck && profitPercent >= 2)
                {
                    sell = true;
                    sellReason = ">2% profit on day after purchase";
                }
                else if (profitPercent >= 2)
                {
                    sell = true;
                    sellReason = ">2% profit";
                }
                else if (profitPercent <= -1)
                {
                    sell = true;
                    sellReason = "<1% stop-loss";
                }
                else
                {
                    var ema7 = await _historicalFetcher.GetEmaAsync(transaction.Symbol, 7);
                    if (ema7.HasValue && quote.LastPrice < ema7.Value)
                    {
                        sell = true;
                        sellReason = "close below EMA7";
                    }
                }

                if (sell)
                {
                    var token = tokens.GetValueOrDefault(transaction.Symbol.ToUpper());
                    if (token == null)
                    {
                        _logger.LogWarning("No token found for {Symbol}. Skipping sell.", transaction.Symbol);
                        continue;
                    }

                    var orderId = _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "CNC", transaction.Quantity, 0);
                    _logger.LogInformation("Swing sold {Symbol}. Order ID: {OrderId}", transaction.Symbol, orderId);

                    decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                    decimal profitLossPct = profitPercent;
                    await _mongoDbService.UpdateTransactionOnSellAsync("SwingTransactions", transaction.Symbol,
                        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone),
                        quote.LastPrice, profitLossAmount, profitLossPct);

                    string sellMessage = $"Swing Stock Sold:\n" +
                                        $"Name: {transaction.StockName}\n" +
                                        $"Symbol: {transaction.Symbol}\n" +
                                        $"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST\n" +
                                        $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                        $"Sell Price: ₹{quote.LastPrice:F2}\n" +
                                        $"Profit/Loss: ₹{profitLossAmount:F2} ({profitLossPct:F2}%)\n" +
                                        $"Reason: {sellReason}";
                    await _telegramBot.SendMessage(_chatId, sellMessage);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error in Swing Monitor Job: {Message}", ex.Message);
            await _telegramBot.SendMessage(_chatId, $"Swing Monitor Error: {ex.Message}");
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
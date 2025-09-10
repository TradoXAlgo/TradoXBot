using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Services;
using TradoXBot.Models;

namespace TradoXBot.Jobs;


public class ScalpingSelJob : IJob
{
    private readonly ILogger<ScalpingSelJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public ScalpingSelJob(IConfiguration configuration, ILogger<ScalpingSelJob> logger, StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
    {
        _logger = logger;
        _stoxKartClient = stoxKartClient;
        _chartinkScraper = chartinkScraper;
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
                _logger.LogInformation("Sell Job skipped: Outside market hours (9:15 AM - 3:30 PM IST) or not a trading day.");
                return;
            }

            _logger.LogInformation("Executing Swing Sell Job at {Time}", DateTime.Now);

            var status = await _stoxKartClient.AuthenticateAsync();
            if (!status)
            {
                _logger.LogError("Authentication failed. Aborting swing buy.");
                _ = await _telegramBot.SendMessage(_chatId, "Swing Buy: Authentication failed.");
                return;
            }
            var openTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");
            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            var scannerSymbols = scannerStocks.Select(s => s.Symbol.ToUpper()).ToList();

            var positions = await _stoxKartClient.GetPositionAsync();
            var positionsSymbols = positions.Select(h => h.Symbol.ToUpper()).ToHashSet();
            if (positions.Count == 0) return;

            var quoteRequests = openTransactions
                .Select(t => tokens.GetValueOrDefault(t.Symbol))
                .Where(t => t != null)
                .Distinct()
                .ToList();

            var quotes = await _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

            var symbolQuotes = new Dictionary<string, Quote>();
            foreach (var kv in quotes)
            {
                var symbol = openTransactions.FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol) == kv.Key)?.Symbol;
                if (symbol != null)
                    symbolQuotes[symbol] = kv.Value;
            }

            // Get transactions bought yesterday to check for 2%+ gain today
            var yesterday = now.Date.AddDays(-1);
            var transactionsBoughtYesterday = openTransactions
                .Where(t => t.BuyDate.Date == yesterday)
                .ToList();

            foreach (var transaction in openTransactions)
            {
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning("Scalping Sell: No quote data for {Symbol}. Skipping.", transaction.Symbol);
                    Console.WriteLine($"Scalping Sell: No quote data for {transaction.Symbol}.");
                    continue;
                }

                decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                bool sell = false;
                string sellReason = "";
                decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14, "5m");
                decimal stopLossPrice = transaction.BuyPrice - (atr.HasValue ? 2 * atr.Value : transaction.BuyPrice * 0.025m);
                decimal trailingStopLoss = profitPercent > 5 ? transaction.BuyPrice + (transaction.BuyPrice * 0.02m) : stopLossPrice;

                if (profitPercent >= 1)
                {
                    sell = true;
                    sellReason = ">1% profit";
                }
                else if (quote.LastPrice <= trailingStopLoss)
                {
                    sell = true;
                    sellReason = profitPercent > 5 ? "Trailing stop-loss (entry + 2%)" : "<0.5% stop-loss";
                }
                else
                {
                    var ema7 = await _historicalFetcher.GetEmaAsync(transaction.Symbol, 7, "5m");
                    if (ema7.HasValue && quote.Close < ema7.Value)
                    {
                        sell = true;
                        sellReason = "Close below EMA7 (5m)";
                    }
                }

                if (sell)
                {
                    var token = tokens.GetValueOrDefault(transaction.Symbol.ToUpper());
                    if (token == null)
                    {
                        _logger.LogWarning("Scalping Sell: No token found for {Symbol}. Skipping.", transaction.Symbol);
                        Console.WriteLine($"Scalping Sell: No token found for {transaction.Symbol}.");
                        continue;
                    }

                    var orderId = _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "INTRADAY", transaction.Quantity, 0);
                    _logger.LogInformation("Scalping Sell: Sold {Symbol}. Order ID: {OrderId}", transaction.Symbol, orderId);
                    Console.WriteLine($"Scalping Sell: Sold {transaction.Symbol} (Qty: {transaction.Quantity}, Price: ₹{quote.LastPrice:F2}).");

                    decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                    decimal profitLossPct = profitPercent;
                    await _mongoDbService.UpdateTransactionOnSellAsync("ScalpingTransactions", transaction.Symbol,
                        now, quote.LastPrice, profitLossAmount, profitLossPct);

                    string sellMessage = $"Scalping Stock Sold:\n" +
                                        $"Name: {transaction.StockName}\n" +
                                        $"Symbol: {transaction.Symbol}\n" +
                                        $"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST\n" +
                                        $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                        $"Sell Price: ₹{quote.LastPrice:F2}\n" +
                                        $"Profit/Loss: ₹{profitLossAmount:F2} ({profitLossPct:F2}%)\n" +
                                        $"Reason: {sellReason}";
                    await _telegramBot.SendMessage(_chatId, sellMessage);
                    Console.WriteLine("Scalping Sell: Telegram notification sent.");
                }
            }
            // Status report for all open positions after sell checks
            //var statusJob = new StatusJob(_stoxKartClient, _mongoDbService, _telegramBot, _logger, _chatId);
            //await statusJob.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in Swing Sell Job: {ex.Message}");
            await _telegramBot.SendMessage(_chatId, $"Swing Sell Error: {ex.Message}");
        }
    }

    private DateTime AddTradingDays(DateTime startDate, int tradingDays)
    {
        int daysToAdd = tradingDays;
        DateTime resultDate = startDate;
        int direction = tradingDays >= 0 ? 1 : -1;

        while (daysToAdd != 0)
        {
            resultDate = resultDate.AddDays(direction);
            if (IsTradingDay(resultDate))
            {
                daysToAdd -= direction;
            }
        }

        return resultDate;
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

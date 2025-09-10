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
using System.Diagnostics.Eventing.Reader;

namespace TradoXBot.Jobs;


public class SellJob : IJob
{
    private readonly ILogger<SellJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public SellJob(IConfiguration configuration, ILogger<SellJob> logger, StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
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
            var marketClose = new TimeSpan(15, 20, 0);
            if (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose || !IsTradingDay(now))
            {
                _logger.LogInformation("Sell Job skipped: Outside market hours (9:15 AM - 3:30 PM IST) or not a trading day.");
                return;
            }

            _logger.LogInformation("Executing Swing Sell Job at {Time}", DateTime.Now);
            var status = await _stoxKartClient.AuthenticateAsync();
            if (!status) return;

            var openTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");
            var holdins = await _stoxKartClient.GetPortfolioHoldingsAsync();

            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            var scannerSymbols = scannerStocks.Select(s => s.Symbol.ToUpper()).ToList();
            var holdingsSymbols = holdins.Select(h => h.Symbol.ToUpper()).ToHashSet();

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

            var after2Day = now.Date.AddDays(-2);
            var transactionsBoughtAfter2Day = openTransactions
                .Where(t => t.BuyDate.Date == after2Day)
                .ToList();

            foreach (var transaction in openTransactions)
            {
                // Check if stock is available in portfolio holdings
                if (!holdingsSymbols.Contains(transaction.Symbol.ToUpper()))
                {
                    _logger.LogInformation("Skipping {Symbol}: Not available in portfolio holdings.", transaction.Symbol);
                    continue;
                }
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning("No quote data for {Symbol}. Skipping sell check.", transaction.Symbol);
                    continue;
                }

                if (scannerSymbols.Contains(transaction.Symbol.ToUpper()))
                {
                    transaction.ExpiryDate = AddTradingDays(transaction.ExpiryDate, 1);
                    await _mongoDbService.UpdateTransactionOnSellAsync("SwingTransactions", transaction.Symbol,
                        transaction.ExpiryDate, transaction.SellPrice, transaction.ProfitLoss, transaction.ProfitLossPct);
                    _logger.LogInformation("Extended expiry for {Symbol} by 1 trading day (in Chartink scanner).", transaction.Symbol);
                    continue;
                }
                decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                bool sell = false;
                string sellReason = "";

                decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14);
                decimal stopLossPrice = transaction.BuyPrice - (atr.HasValue ? 2 * atr.Value : transaction.BuyPrice * 0.025m);
                decimal trailingStopLoss = profitPercent > 5 ? transaction.BuyPrice + (transaction.BuyPrice * 0.02m) : stopLossPrice;

                bool isEma5Confirmed = quote.Open < await _historicalFetcher.GetEmaAsync(transaction.Symbol, 5) &&
                                       quote.Close < await _historicalFetcher.GetEmaAsync(transaction.Symbol, 5) &&
                                       quote.Close < quote.Open;

                bool isEma9Confirmed = quote.Open < await _historicalFetcher.GetEmaAsync(transaction.Symbol, 9) &&
                                       quote.Close < await _historicalFetcher.GetEmaAsync(transaction.Symbol, 9) &&
                                       quote.Close < quote.Open;

                bool isVolumeDrop = await _historicalFetcher.IsVolumeBelowSmaAsync(transaction.Symbol, 20) &&
                                    quote.LastPrice < transaction.BuyPrice &&
                                    now.Date > transaction.BuyDate.Date;

                if (transactionsBoughtYesterday.Any(t => t.Symbol == transaction.Symbol) && profitPercent >= 2)
                {
                    sell = true;
                    sellReason = ">2% profit since yesterday";
                }
                else if (transactionsBoughtYesterday.Any(t => t.Symbol == transaction.Symbol) && profitPercent >= 5)
                {
                    sell = true;
                    sellReason = ">5% profit since yesterday";
                }
                else if (transaction.ExpiryDate <= now)
                {
                    sell = true;
                    sellReason = "Position expired";
                }
                else if (profitPercent >= 10)
                {
                    sell = true;
                    sellReason = ">10% profit";
                }
                else if (quote.LastPrice < await _historicalFetcher.GetEmaAsync(transaction.Symbol, 9) && now.TimeOfDay >= marketClose)
                {
                    sell = true;
                    sellReason = "Price below EMA9";
                }
                else if (quote.LastPrice <= trailingStopLoss && isEma5Confirmed)
                {
                    sell = true;
                    sellReason = profitPercent > 5 ? "Trailing stop-loss (entry + 2%)" : "Stop-loss (2x ATR or 2.5%)";
                }
                else if (isVolumeDrop)
                {
                    sell = true;
                    sellReason = "Volume drop below 20-day SMA and price below entry";
                }
                else if (isEma9Confirmed)
                {
                    sell = true;
                    sellReason = "Close below EMA9";
                }
                else if (quote.LastPrice <= trailingStopLoss)
                {
                    sell = true;
                    sellReason = profitPercent > 5 ? "trailing stop-loss (5% profit)" : "hit stop-loss (2x ATR or 2.5%)";
                }

                else if (isEma5Confirmed)
                {
                    sell = true;
                    sellReason = "below EMA5 (confirmed candle)";
                }
                else if (isEma9Confirmed)
                {
                    sell = true;
                    sellReason = "below EMA9 (confirmed candle)";
                }
                else if (isVolumeDrop)
                {
                    sell = true;
                    sellReason = "volume drop below SMA20 with price decline";
                }

                // Check for 2%+ gain for stocks bought yesterday
                if (profitPercent >= 5)
                {
                    sell = true;
                    sellReason = ">5% profit since yesterday";
                }

                // Check for 2%+ gain for stocks bought yesterday
                if (profitPercent >= 2)
                {
                    sell = true;
                    sellReason = ">2% profit since yesterday";
                }
                // Add other existing sell conditions here (e.g., stop-loss, expiry)
                else if (transaction.ExpiryDate <= now)
                {
                    sell = true;
                    sellReason = "Position expired";
                }
                else
                {
                    if (quote.LastPrice <= stopLossPrice)
                    {
                        sell = true;
                        sellReason = "Hit stop-loss (ATR-based)";
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

                    var orderId = _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "LIMIT", "CNC", transaction.Quantity, quote.LastPrice);
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

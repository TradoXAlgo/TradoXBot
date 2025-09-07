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
            _logger.LogInformation("Executing Swing Sell Job at {Time}", DateTime.Now);
            await _stoxKartClient.AuthenticateAsync();

            var openTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");

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

            foreach (var transaction in openTransactions)
            {
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning($"No quote data for {transaction.Symbol}. Skipping sell check.");
                    continue;
                }

                decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                bool sell = false;
                string sellReason = "";
                decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol);
                decimal stopLossPrice = transaction.BuyPrice - (atr.HasValue ? atr.Value * 2 : transaction.BuyPrice * 0.025m);

                if (profitPercent >= 10)
                {
                    sell = true;
                    sellReason = ">10% profit";
                }
                else if (DateTime.Now.Date >= transaction.ExpiryDate.Date && !scannerStocks.Any(s => s.Symbol == transaction.Symbol))
                {
                    sell = true;
                    sellReason = "expired and not in scanner";
                }
                else if (quote.LastPrice < stopLossPrice)
                {
                    sell = true;
                    sellReason = "hit stop-loss (2x ATR or 2.5%)";
                }
                else
                {
                    var ema21 = await _historicalFetcher.GetEma21Async(transaction.Symbol);
                    var ema9 = await _historicalFetcher.GetEma9Async(transaction.Symbol);
                    var ema5 = await _historicalFetcher.GetEma5Async(transaction.Symbol);
                    var ohlc = await _historicalFetcher.GetTodaysOhlcAsync(transaction.Symbol);
                    var volumeSma = await _historicalFetcher.GetVolumeSmaAsync(transaction.Symbol);

                    if (ema21.HasValue && quote.LastPrice < ema21.Value)
                    {
                        sell = true;
                        sellReason = "below EMA21";
                    }
                    else if (ema9.HasValue && ema5.HasValue && ohlc != null &&
                             ohlc.Close < ema9.Value && ohlc.Close < ohlc.Open &&
                             ohlc.Close < ema5.Value && ohlc.Volume < volumeSma)
                    {
                        sell = true;
                        sellReason = "hybrid EMA9 stop-loss";
                    }
                }

                if (profitPercent > 5)
                {
                    decimal trailingStop = transaction.BuyPrice + (transaction.BuyPrice * 0.02m);
                    if (quote.LastPrice < trailingStop)
                    {
                        sell = true;
                        sellReason = "trailing stop-loss (5% profit)";
                    }
                }

                if (scannerStocks.Any(s => s.Symbol == transaction.Symbol))
                {
                    await _mongoDbService.UpdateTransactionExpiryAsync("SwingTransactions", transaction.Symbol, AddTradingDays(DateTime.Now, 10));
                    _logger.LogInformation($"Extended expiry for {transaction.Symbol} to {AddTradingDays(DateTime.Now, 10):yyyy-MM-dd}");
                    continue;
                }

                if (sell)
                {
                    var token = tokens.GetValueOrDefault(transaction.Symbol);
                    if (token == null)
                    {
                        _logger.LogWarning($"No token found for {transaction.Symbol}. Skipping sell.");
                        continue;
                    }

                    var orderId = await _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "DELIVERY", transaction.Quantity, 0);
                    _logger.LogInformation($"Swing sold {transaction.Symbol}. Order ID: {orderId}");

                    decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                    decimal profitLossPct = profitPercent;
                    await _mongoDbService.UpdateTransactionOnSellAsync("SwingTransactions", transaction.Symbol, DateTime.Now, quote.LastPrice, profitLossAmount, profitLossPct);

                    string sellMessage = $"Swing Stock Sold:\n" +
                                         $"Name: {transaction.StockName}\n" +
                                         $"Symbol: {transaction.Symbol}\n" +
                                         $"Buy Date: {transaction.BuyDate:yyyy-MM-dd HH:mm}\n" +
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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class ScalpingMonitorJob : IJob
{
    private readonly ILogger<ScalpingMonitorJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public ScalpingMonitorJob(IConfiguration configuration, ILogger<ScalpingMonitorJob> logger, StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
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
            var marketClose = new TimeSpan(14, 30, 0);
            if (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose || !IsTradingDay(now))
            {
                _logger.LogInformation("Scalping Monitor skipped: Outside market hours (9:15 AM - 2:30 PM IST) or not a trading day.");
                return;
            }
            _logger.LogInformation("Executing Scalping Monitor Job at {Time} IST", now);
            await _stoxKartClient.AuthenticateAsync();
            var openCount = await _mongoDbService.GetOpenPositionCountAsync();
            if (openCount >= 5)
            {
                _logger.LogWarning("Portfolio limit reached (5 positions). Checking sell conditions only.");
                await CheckScalpingSellConditionsAsync();
                return;
            }
            var dailyStockCount = await _mongoDbService.GetDailyUniqueStocksBoughtAsync();
            if (dailyStockCount >= 5)
            {
                _logger.LogWarning("Daily buy limit reached (5 unique stocks). Checking sell conditions only.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Daily buy limit reached (5 unique stocks).");
                await CheckScalpingSellConditionsAsync();
                return;
            }
            var availableFunds = await _stoxKartClient.GetFundsAsync();
            var maxOrders = Math.Min((int)(availableFunds / 20000), 5 - openCount);
            if (maxOrders <= 0)
            {
                _logger.LogWarning("Insufficient funds for scalping buy.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Insufficient funds for new buys.");
                await CheckScalpingSellConditionsAsync();
                return;
            }
            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            if (scannerStocks.Count == 0)
            {
                _logger.LogWarning("No stocks found in Chartink scanner.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: No stocks found in scanner.");
                await CheckScalpingSellConditionsAsync();
                return;
            }
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");
            foreach (var stock in scannerStocks.Take(maxOrders))
            {
                // Check for open position
                if (await _mongoDbService.HasOpenPositionAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol}: Already has an open position.", stock.Symbol);
                    continue;
                }
                // Check if stock was bought and sold today
                if (await _mongoDbService.WasStockBoughtAndSoldSameDayAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol}: Already bought and sold today.", stock.Symbol);
                    continue;
                }
                // Check if stock had a profitable trade today
                if (await _mongoDbService.ShouldSkipStockForScalpingAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol} due to recent profitable scalping trade today.", stock.Symbol);
                    continue;
                }
                bool isBreakout = await _historicalFetcher.IsScalpingBreakoutAsync(stock.Symbol);
                if (!isBreakout)
                {
                    _logger.LogInformation("Skipping {Symbol}: No scalping breakout.", stock.Symbol);
                    continue;
                }
                var token = tokens.GetValueOrDefault(stock.Symbol.ToUpper());
                if (token == null)
                {
                    _logger.LogWarning("No token found for {Symbol}. Skipping.", stock.Symbol);
                    continue;
                }
                var quotes = await _stoxKartClient.GetQuotesAsync("NSE", new[] { token }.ToList());
                if (!quotes.TryGetValue(token, out var quote))
                {
                    _logger.LogWarning("No quote data for {Symbol}. Skipping.", stock.Symbol);
                    continue;
                }
                decimal? atr = await _historicalFetcher.GetAtrAsync(stock.Symbol, 14, "5m");
                decimal riskPerShare = atr.HasValue ? atr.Value : quote.LastPrice * 0.005m;
                decimal riskPerTrade = availableFunds * 0.01m;
                int quantity = (int)(riskPerTrade / riskPerShare);
                if (quantity <= 0)
                {
                    _logger.LogWarning("Calculated quantity for {Symbol} is {Quantity}. Skipping.", stock.Symbol, quantity);
                    continue;
                }
                var orderId = await _stoxKartClient.PlaceOrderAsync("BUY", "NSE", token, "MARKET", "INTRADAY", quantity, 0);
                _logger.LogInformation("Scalping bought {Symbol} (Qty: {Quantity}, Price: {Price:F2}). Order ID: {OrderId}",
                stock.Symbol, quantity, quote.LastPrice, orderId);
                var ohlc = await _historicalFetcher.GetTodaysOhlcAsync(stock.Symbol, "5m");
                var transaction = new Transaction
                {
                    StockName = stock.Name,
                    Symbol = stock.Symbol,
                    BuyDate = now,
                    BuyPrice = quote.LastPrice,
                    Quantity = quantity,
                    ExpiryDate = now.Date.Add(new TimeSpan(14, 30, 0)),
                    OpenPrice = ohlc?.Open ?? 0,
                    HighPrice = ohlc?.High ?? 0,
                    LowPrice = ohlc?.Low ?? 0,
                    ClosePrice = ohlc?.Close ?? quote.LastPrice,
                    IsOpen = true
                };
                await _mongoDbService.InsertScalpingTransactionAsync(transaction);
                await _mongoDbService.InsertScannerStockAsync(stock);
                decimal stopLoss = quote.LastPrice - riskPerShare;
                decimal targetPrice = quote.LastPrice + (2 * riskPerShare);
                string message = $"Scalping Trade Alert:\n" +
                $"Name: {stock.Name}\n" +
                $"Symbol: {stock.Symbol}\n" +
                $"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST\n" +
                $"Buy Price: ₹{quote.LastPrice:F2}\n" +
                $"Target: ₹{targetPrice:F2}\n" +
                $"Stop Loss: ₹{stopLoss:F2}\n" +
                $"Quantity: {quantity}\n" +
                $"Risk-Reward Ratio: 2:1";
                await _telegramBot.SendMessage(_chatId, message);
            }
            await CheckScalpingSellConditionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error in Scalping Monitor Job: {Message}", ex.Message);
            await _telegramBot.SendMessage(_chatId, $"Scalping Monitor Error: {ex.Message}");
        }
    }
    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }

    private async Task CheckScalpingSellConditionsAsync()
    {
        var openTransactions = await _mongoDbService.GetOpenScalpingTransactionsAsync();
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
        foreach (var transaction in openTransactions)
        {
            if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
            {
                _logger.LogWarning("No quote data for {Symbol}. Skipping sell check.", transaction.Symbol);
                continue;
            }
            decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
            bool sell = false;
            string sellReason = "";
            decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14, "5m");
            decimal stopLossPrice = transaction.BuyPrice - (atr.HasValue ? atr.Value : transaction.BuyPrice * 0.005m);
            if (profitPercent >= 1)
            {
                sell = true;
                sellReason = ">1% profit";
            }
            else if (profitPercent <= (decimal)-0.5)
            {
                sell = true;
                sellReason = "<0.5% stop-loss";
            }
            else
            {
                var ema7 = await _historicalFetcher.GetEma7Async(transaction.Symbol);
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
                var orderId = await _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "INTRADAY", transaction.Quantity, 0);
                _logger.LogInformation("Scalping sold {Symbol}. Order ID: {OrderId}", transaction.Symbol, orderId);
                decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                decimal profitLossPct = profitPercent;
                await _mongoDbService.UpdateTransactionOnSellAsync("ScalpingTransactions", transaction.Symbol,
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")),
                quote.LastPrice, profitLossAmount, profitLossPct);
                string sellMessage = $"Scalping Stock Sold:\n" +
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
}

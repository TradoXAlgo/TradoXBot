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

public class ScalpingBuyJob : IJob
{
    private readonly ILogger<ScalpingBuyJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public ScalpingBuyJob(IConfiguration configuration, ILogger<ScalpingBuyJob> logger,
        StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper,
        HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
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
            var status = await _stoxKartClient.AccessTokenKey();
            await _telegramBot.SendMessage(_chatId, status);
            if (status == null) return;
            var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
            var marketOpen = new TimeSpan(9, 10, 0);
            var marketClose = new TimeSpan(14, 30, 0);

            if (IsMonthEnd(now))
            {
                _logger.LogInformation("Scalping Monitor skipped: Month-end (28th-31st).");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Skipped due to month-end.");
                await CheckScalpingSellConditionsAsync();
                return;
            }

            _logger.LogInformation("Executing Scalping Monitor Job at {Time} IST", now);

            Console.WriteLine("Scalping Buy Jobs");
            var availableFunds = await _stoxKartClient.GetFundsAsync();
            var dailyStockCount = await _mongoDbService.GetDailyUniqueStocksBoughtAsync();
            if (dailyStockCount >= 5)
            {
                _logger.LogWarning("Daily buy limit reached (5 unique stocks). Checking sell conditions only.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Daily buy limit reached (5 unique stocks).");
                await CheckScalpingSellConditionsAsync();
                return;
            }

            if (availableFunds <= 10000)
            {
                _logger.LogWarning("Insufficient funds for scalping buy (≤ ₹20,000). Checking sell conditions.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Insufficient funds (≤ ₹20,000).");
                await CheckScalpingSellConditionsAsync();
                return;
            }

            //var maxOrders = Math.Min(GetMaxOrders(availableFunds), 5 - dailyStockCount);

            var maxOrders = Math.Min((int)(availableFunds / 10000), 5 - dailyStockCount);
            if (maxOrders < 1)
            {
                _logger.LogWarning("No orders possible with available funds and daily limit.");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: No orders possible.");
                await CheckScalpingSellConditionsAsync();
                return;
            }

            var scannerStocks = await _chartinkScraper.GetScalpingStocksAsync();
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
                if (await _mongoDbService.HasOpenPositionAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol}: Already has an open position.", stock.Symbol);
                    continue;
                }

                if (await _mongoDbService.WasStockSoldAtProfitRecentlyAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol}: Sold at profit within 20 trading days.", stock.Symbol);
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

                bool isUptrend = await _historicalFetcher.IsPriceAboveEmaAsync(stock.Symbol, 50);
                bool isBreakout = await _historicalFetcher.IsBreakoutAsync(stock.Symbol);
                bool isRsiValid = await _historicalFetcher.GetRsiAsync(stock.Symbol) > 60;
                bool isVolumeValid = await _historicalFetcher.IsVolumeAboveSmaAsync(stock.Symbol, 20);

                if (!isUptrend || !isBreakout || !isRsiValid || !isVolumeValid)
                {
                    _logger.LogInformation("Skipping {Symbol}: Does not meet uptrend/breakout/RSI/volume criteria.", stock.Symbol);
                    continue;
                }

                decimal? atr = await _historicalFetcher.GetAtrAsync(stock.Symbol, 14, "5m");
                decimal riskPerShare = atr.HasValue ? 2 * atr.Value : quote.LastPrice * 0.025m;
                decimal riskPerTrade = availableFunds * 0.01m;
                int quantity = (int)(riskPerTrade / riskPerShare);
                if (quantity <= 0)
                {
                    _logger.LogWarning("Calculated quantity for {Symbol} is {Quantity}. Skipping.", stock.Symbol, quantity);
                    continue;
                }

                var orderId = _stoxKartClient.PlaceOrderAsync("BUY", "NSE", token, "MARKET", "INTRADAY", quantity, 0);
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
                decimal targetPrice = quote.LastPrice + (3 * riskPerShare);

                string message = $"Scalping Trade Alert:\n" +
                                $"Name: {stock.Name}\n" +
                                $"Symbol: {stock.Symbol}\n" +
                                $"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST\n" +
                                $"Buy Price: ₹{quote.LastPrice:F2}\n" +
                                $"Target: ₹{targetPrice:F2}\n" +
                                $"Stop Loss: ₹{stopLoss:F2}\n" +
                                $"Quantity: {quantity}\n" +
                                $"R:R: 3:1";
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

    private async Task CheckScalpingSellConditionsAsync()
    {
        var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
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
                    _logger.LogWarning("No token found for {Symbol}. Skipping sell.", transaction.Symbol);
                    continue;
                }

                var orderId = _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "INTRADAY", transaction.Quantity, 0);
                _logger.LogInformation("Scalping sold {Symbol}. Order ID: {OrderId}", transaction.Symbol, orderId);

                decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                decimal profitLossPct = profitPercent;
                await _mongoDbService.UpdateTransactionOnSellAsync("ScalpingTransactions", transaction.Symbol,
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone),
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

    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }

    private bool IsMonthEnd(DateTime date)
    {
        return date.Day >= 28 && date.Day <= 31;
    }

    private int GetMaxOrders(decimal funds)
    {
        if (funds <= 40000) return 1;
        if (funds <= 60000) return 2;
        if (funds <= 80000) return 3;
        if (funds <= 100000) return 4;
        return 5;
    }
}
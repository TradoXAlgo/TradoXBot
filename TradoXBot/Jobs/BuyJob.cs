using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class BuyJob : IJob
{
    private readonly ILogger<BuyJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public BuyJob(IConfiguration configuration, ILogger<BuyJob> logger, StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
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

            if (IsMonthEnd())
            {
                _logger.LogInformation("Buy Job skipped: Month-end (28th-31st).");
                await _telegramBot.SendMessage(_chatId, "Buy Job: Skipped due to month-end.");
                return;
            }
            var availableFunds = await _stoxKartClient.GetFundsAsync();
            if (availableFunds <= 20000)
            {
                _logger.LogWarning($"Buy Job: Insufficient funds (≤ ₹20,000), Current Fund{availableFunds}");
                await _telegramBot.SendMessage(_chatId, $"Buy Job: Insufficient funds (≤ ₹20,000), Current Fund{availableFunds}");
                return;
            }

            // var openCount = await _mongoDbService.GetOpenPositionCountAsync();
            // if (openCount >= 5)
            // {
            //     _logger.LogWarning("Portfolio limit reached (5 positions).");
            //     _ = await _telegramBot.SendMessage(_chatId, "Swing Buy: Portfolio limit reached (5 positions).");
            //     return;
            // }

            var dailyStockCount = await _mongoDbService.GetDailyUniqueStocksBoughtAsync();
            if (dailyStockCount >= 5)
            {
                _logger.LogWarning("Daily buy limit reached (5 unique stocks). Skipping buy.");
                await _telegramBot.SendMessage(_chatId, "Buy Job: Daily buy limit reached (5 unique stocks).");
                return;
            }
            // Calculate max orders based on available funds and daily limit    

            var maxOrders = Math.Min((int)(availableFunds / 20000), 5 - dailyStockCount);
            if (maxOrders <= 0)
            {
                _logger.LogWarning("Insufficient funds for swing buy.");
                _ = await _telegramBot.SendMessage(_chatId, "Swing Buy: Insufficient funds.");
                return;
            }

            // Fetch and sort stocks by percent change
            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            if (scannerStocks.Count == 0)
            {
                _logger.LogWarning("No stocks found in Chartink scanner.");
                _ = await _telegramBot.SendMessage(_chatId, "Buy Jobs: No stocks found in scanner.");
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

                if (await _mongoDbService.WasStockSoldAtProfitRecentlyAsync(stock.Symbol))
                {
                    _logger.LogInformation("Skipping {Symbol}: Sold at profit within 20 trading days.", stock.Symbol);
                    continue;
                }

                // Get token and quote

                var token = tokens.TryGetValue(stock.Symbol, out string? value) ? value : null;
                if (token == null)
                {
                    _logger.LogWarning($"No token found for {stock.Symbol}. Skipping.");
                    continue;
                }

                var quotes = await _stoxKartClient.GetQuotesAsync("NSE", new[] { token }.ToList());
                if (!quotes.TryGetValue(token, out var quote))
                {
                    _logger.LogWarning("No quote data for {Symbol}. Skipping.", stock.Symbol);
                    continue;
                }

                // Check uptrend and breakout conditions
                //bool isUptrend = await _historicalFetcher.IsPriceAboveEmaAsync(stock.Symbol, 50);
                //bool isBreakout = await _historicalFetcher.IsBreakoutAsync(stock.Symbol);
                //bool isRsiValid = await _historicalFetcher.GetRsiAsync(stock.Symbol) > 50;
                //bool isVolumeValid = await _historicalFetcher.IsVolumeAboveSmaAsync(stock.Symbol, 20);

                // if (!isUptrend || !isRsiValid || !isVolumeValid)
                // {
                //     _logger.LogInformation("Skipping {Symbol}: Does not meet uptrend/breakout/RSI/volume criteria.", stock.Symbol);
                //     continue;
                // }

                // Calculate quantity based on 1% risk
                decimal? atr = await _historicalFetcher.GetAtrAsync(stock.Symbol, 14);
                decimal riskPerShare = atr.HasValue ? atr.Value * 2 : quote.LastPrice * 0.025m;
                decimal riskPerTrade = availableFunds * 0.01m;
                int quantity = (int)(riskPerTrade / riskPerShare);
                if (quantity <= 0)
                {
                    _logger.LogWarning($"Calculated quantity for {stock.Symbol} is {quantity}. Skipping.");
                    continue;
                }

                // Place buy order
                var orderId = await _stoxKartClient.PlaceOrderAsync(
                    "BUY",
                    "NSE",
                    token,
                    quote.LastPrice == 0 ? "MARKET" : "LIMIT",
                    "DELIVERY",
                     quantity,
                     quote.LastPrice
                     );
                if (string.IsNullOrEmpty(orderId))
                {
                    _logger.LogInformation("Swing bought {Symbol} (Qty: {Quantity}, Price: {Price:F2}). Order ID: {OrderId}",
                        stock.Symbol, quantity, quote.LastPrice, orderId);
                    continue;
                }
                _logger.LogInformation($"Swing bought {stock.Symbol} (Qty: {quantity}, Price: {quote.LastPrice:F2}). Order ID: {orderId}");

                // Store transaction
                var ohlc = await _historicalFetcher.GetTodaysOhlcAsync(stock.Symbol);
                var transaction = new Transaction
                {
                    StockName = stock.Name,
                    OrderId = orderId,
                    Symbol = stock.Symbol,
                    BuyDate = DateTime.Now,
                    BuyPrice = quote.LastPrice,
                    Quantity = quantity,
                    ExpiryDate = AddTradingDays(DateTime.Now, 10),
                    OpenPrice = ohlc?.Open ?? 0,
                    HighPrice = ohlc?.High ?? 0,
                    LowPrice = ohlc?.Low ?? 0,
                    ClosePrice = ohlc?.Close ?? quote.LastPrice,
                    IsOpen = true,
                    TransactionType = "Swing"
                };
                await _mongoDbService.InsertSwingTransactionAsync(transaction);
                await _mongoDbService.InsertScannerStockAsync(stock);

                // Calculate target and stop-loss
                decimal stopLoss = quote.LastPrice - riskPerShare;
                decimal targetPrice = quote.LastPrice + (3 * riskPerShare); // 3:1 R:R

                // Send Telegram notification
                string message = $"Swing Trade Alert:\n" +
                                $"Name: {stock.Name}\n" +
                                $"Symbol: {stock.Symbol}\n" +
                                $"Buy Date: {transaction.BuyDate:yyyy-MM-dd HH:mm}\n" +
                                $"Buy Price: ₹{quote.LastPrice:F2}\n" +
                                $"Target: ₹{targetPrice:F2}\n" +
                                $"Stop Loss: ₹{stopLoss:F2}\n" +
                                $"Quantity: {quantity}\n" +
                                $"Risk-Reward Ratio: 3:1";
                _ = await _telegramBot.SendMessage(_chatId, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in Swing Buy Job: {ex.Message}");
            _ = await _telegramBot.SendMessage(_chatId, $"Swing Buy Error: {ex.Message}");
        }
    }

    private bool IsMonthEnd()
    {
        var today = DateTime.Now;
        var lastDayOfMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        return today.Date >= lastDayOfMonth.AddDays(-3);
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
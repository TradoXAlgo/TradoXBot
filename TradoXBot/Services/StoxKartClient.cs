using Flurl.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.SuperrApiConnect;

namespace TradoXBot.Services;

public class StoxKartClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StoxKartClient> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _clientId;
    private readonly string _password;
    private string? _accessToken;
    private string? _telegramAPI;
    private const string BaseUrl = "https://openapi.stoxkart.com/";
    private SuperrApi? _superrApi;
    private Ticker? ticker;
    private readonly IAsyncPolicy _retryPolicy;
    private DateTime _tokenExpiry;
    private readonly TelegramBotClient _telegramBot;

    public StoxKartClient(IConfiguration configuration, ILogger<StoxKartClient> logger)
    {
        _apiKey = configuration["Stoxkart:ApiKey"] ?? throw new ArgumentNullException(nameof(configuration));
        _apiSecret = configuration["Stoxkart:ApiSecret"] ?? throw new ArgumentNullException(nameof(configuration));
        _clientId = configuration["Stoxkart:ClientId"] ?? throw new ArgumentNullException(nameof(configuration));
        _password = configuration["Stoxkart:Password"] ?? throw new ArgumentNullException(nameof(configuration));
        _telegramAPI = configuration["Telegram:ApiKey"] ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _logger = logger;
        _configuration = configuration;
        _superrApi = new SuperrApi(_clientId, _password, _apiKey, _apiSecret);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _telegramBot = new TelegramBotClient(_telegramAPI);
        // Initialize Polly retry policy
        _retryPolicy = Policy.Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            (exception, timeSpan, retryCount, context) =>
            {
                _logger.LogWarning($"Retry {retryCount} encountered an error: {exception.Message}. Waiting {timeSpan} before next retry.");
            });
    }

    public async Task<bool> AuthenticateAsync()
    {
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (!string.IsNullOrEmpty(_accessToken))
        {
            _logger.LogInformation("Already authenticated with valid access token.");
            return true;
        }
        try
        {
            Task<bool> status = Task.Run(() => _superrApi.LoginAndSetAccessToken(), cts.Token);
            if (!await status)
            {
                return await status;
            }
            _accessToken = _superrApi.GetAccessToken()?.ToString();
            if (string.IsNullOrEmpty(_accessToken))
            {
                _logger.LogError("No access token received in login response.");
                throw new InvalidOperationException("Failed to retrieve access token.");
            }
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            _logger.LogInformation("Successfully authenticated with StoxKart API.");
            return await status;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Authentication failed: {ex.Message}");
            throw;
        }
    }

    public async Task<decimal> GetFundsAsync()
    {

        if (string.IsNullOrEmpty(_accessToken))
        {
            _logger.LogError("Not authenticated. Call AuthenticateAsync first.");
            throw new Exception("Not authenticated.");
        }
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Dictionary<string, dynamic>> FundDetailsResponse = Task.Run(() => _superrApi.FundDetails(), cts.Token);

        var fundDerails = await FundDetailsResponse;

        if (fundDerails["status"] == "success")
        {
            var size = fundDerails["data"].Count;
            //await size;
        }
        else
        {
            Console.WriteLine("Fund Details Transaction Failed ::" + fundDerails["message"]);
        }

        return fundDerails["data"].Count > 0 ? Convert.ToDecimal(fundDerails["data"]["available_limit"]) : 0;
    }

    public async Task<string> PlaceOrderAsync(string action, string exchange, string token, string orderType, string productType, int quantity, decimal price)
    {
        await AuthenticateAsync();
        if (string.IsNullOrEmpty(_accessToken))
        {
            _logger.LogError("Not authenticated. Call AuthenticateAsync first.");
            throw new Exception("Not authenticated.");
        }
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Dictionary<string, dynamic>> PlaceOrderResponse = Task.Run(() => _superrApi.PlaceOrder(
                        variety: "AMO",
                        action: action,
                        exchange: exchange,
                        token: token,
                        order_type: orderType,
                        product_type: productType,
                        quantity: quantity.ToString(),
                        disclose_quantity: "0",
                        price: price.ToString(),
                        trigger_price: "0",
                        stop_loss_price: "0",
                        trailing_stop_loss: "0",
                        validity: "DAY",
                        tag: ""
                    ), cts.Token);
        var PlaceOrderRes = await PlaceOrderResponse;

        if (PlaceOrderRes["status"] == "success")
        {
            Console.WriteLine("Order_ID ::" + PlaceOrderRes["data"]["order_id"]);
        }
        else
        {
            Console.WriteLine("Place Order Transaction Failed ::" + PlaceOrderRes["message"]);
        }
        return PlaceOrderRes["data"]["order_id"];
    }

    public async Task<Dictionary<string, Quote>> GetQuotesAsync(string exchange, List<string> tokens)
    {
        try
        {
            if (string.IsNullOrEmpty(_accessToken)) throw new Exception("Authenticate first.");

            if (tokens == null || tokens.Count == 0)
            {
                _logger.LogWarning("No tokens provided for quotes request.");
                return new Dictionary<string, Quote>();
            }

            var quotes = new Dictionary<string, Quote>();

            CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<Dictionary<string, dynamic>> FundDetailsResponse = Task.Run(() => _superrApi.GetQuotes(exchange, tokens), cts.Token);

            var fundDerails = await FundDetailsResponse;

            if (fundDerails["status"] == "success")
            {
                var size = fundDerails["data"].Count;
                var data = fundDerails["data"];
                foreach (var dataItem in data)
                {
                    foreach (var token in tokens)
                    {
                        if (dataItem["token"] == token)
                        {
                            var quoteData = dataItem;
                            quotes[token] = new Quote
                            {
                                LastPrice = Convert.ToDecimal(quoteData["last_trade_price"]),
                                PrevClose = Convert.ToDecimal(quoteData["ohlc"]["close"]),
                                Open = Convert.ToDecimal(quoteData["ohlc"]["open"]),
                                High = Convert.ToDecimal(quoteData["ohlc"]["high"]),
                                Low = Convert.ToDecimal(quoteData["ohlc"]["low"]),
                                Close = Convert.ToDecimal(quoteData["ohlc"]["close"]),
                                Volume = Convert.ToInt64(quoteData["volume"])
                            };
                        }
                    }
                }
                //await size;
            }
            else
            {
                Console.WriteLine("Fund Details Transaction Failed ::" + fundDerails["message"]);
            }

            return quotes;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching quotes: {Message}", ex.Message);
            return new Dictionary<string, Quote>();
        }

    }

    public async Task<Dictionary<string, string>> GetInstrumentTokensAsync(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null or empty.");

        try
        {

            // CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // var jsonContentList = Task.Run(() => _superrApi.GetInstrumentTokens(
            //                 exchange: exchange), cts.Token);

            // var jsonContents = await jsonContentList;

            var response = await _httpClient.GetAsync($"scrip-master/csv/{exchange}");
            response.EnsureSuccessStatusCode();
            var csvContent = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(csvContent))
                throw new InvalidOperationException("CSV content is empty.");

            var tokens = new Dictionary<string, string>();
            var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1);

            foreach (var line in lines)
            {
                var columns = line.Split(',', StringSplitOptions.TrimEntries);
                if (columns.Length >= 4)
                {
                    var symbol = columns[3];
                    var token = columns[2];
                    if (tokens.ContainsKey(symbol)) continue;
                    tokens[symbol] = token;
                }
            }

            return tokens;
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Failed to fetch instrument tokens for exchange {exchange}: {ex.Message}", ex);
        }
    }

    public async Task<Dictionary<string, string>> GetHoldingAsync(string exchange)
    {
        var tokens = new Dictionary<string, string>();
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (string.IsNullOrEmpty(_accessToken))
        {
            _logger.LogError("Not authenticated. Call AuthenticateAsync first.");
            throw new Exception("Not authenticated.");
        }
        Task<Dictionary<string, dynamic>> holdingDetailsResponse = Task.Run(() => _superrApi.HoldingDetails(), cts.Token);

        var holdings = await holdingDetailsResponse;

        if (holdings["status"] == "success")
        {
            var size = holdings["data"].Count;
            if (size > 0)
            {
                foreach (var record in holdings["data"])
                {
                    var recordCount = record.Count;
                    if (recordCount > 0)
                    {

                        var key = record["nse_symbol"];
                        var value = record["nse_token"];
                        tokens[key] = value;
                    }
                }
            }
            else
            {
                Console.WriteLine("Fund Details Transaction Failed ::" + holdings["message"]);
            }
        }
        return tokens;
    }
}


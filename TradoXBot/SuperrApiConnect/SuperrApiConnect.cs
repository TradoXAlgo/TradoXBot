using System.Text;

namespace TradoXBot.SuperrApiConnect;

public class SuperrApi
{
    private string _apiKey;
    private string _apiSecret;
    private string _root = "https://openapi.stoxkart.com";
    private string _accessToken;

    static User _user;
    static Ticker _ticker;

    public string GetAccessToken()
    {
        return _accessToken;
    }

    public SuperrApi(string UserID, string Password, string API_Key, string API_Secret)
    {
        _user = new User(UserID, Password);
        _apiKey = API_Key;
        _apiSecret = API_Secret;
        _accessToken = "";
    }

    private Dictionary<string, string> GetAdditionalHeaders()
    {
        var additionalHeaders = new Dictionary<string, string>();
        additionalHeaders.Add("X-Access-Token", _accessToken);
        additionalHeaders.Add("X-Client-Id", _user.GetUserId());
        additionalHeaders.Add("X-Platform", "api");
        additionalHeaders.Add("X-Api-Key", _apiKey);
        return additionalHeaders;
    }

    private readonly Dictionary<string, string> _routes = new Dictionary<string, string>
    {
        ["login"] = "/auth/login",
        ["2FAverify"] = "/auth/twofa/verify",
        ["accessToken"] = "/auth/token",

        ["place_order"] = "/orders/{variety}",
        ["modify_order"] = "/orders/{variety}/{order_id}",
        ["cancel_order"] = "/orders/{variety}/{order_id}",
        ["fund_details"] = "/funds",
        ["quotes"] = "/quotes",
        ["holding_details"] = "/portfolio/holdings",
        ["position_details"] = "/portfolio/positions",

        ["order_book"] = "/reports/order-book",
        ["trade_book"] = "/reports/trade-book",
        ["scrip_master"] = "/scrip-master/{exch}",

        ["script_master_details"] = "/reports/script-master"
    };

    private string GetRequestUrlFromEndpoint(string endpoint)
    {
        return String.Format("{0}{1}", _root, endpoint);
    }

    private string GetLoginWithAPIKeyUrl()
    {
        return string.Format("{0}{1}?api-key={2}", _root, _routes["login"], _apiKey);
    }

    private string Get2FA_VerifyUrl()
    {
        return string.Format("{0}{1}?api-key={2}", _root, _routes["2FAverify"], _apiKey);
    }

    private string GetAccessTokenUrl()
    {
        return string.Format("{0}{1}", _root, _routes["accessToken"]);
    }

    private string LoginWithAPIKey(string Url)
    {
        Dictionary<string, dynamic> result = _user.Login(Url);
        if (result["status"] == "success")
            return result["data"]["token"];
        else
            return "failure:" + result["message"];
    }

    private string Verify2FA(string Url, string request_token)
    {
        Dictionary<string, dynamic> result = _user.Verify2FA(Url, request_token);
        if (result["status"] == "success")
            return result["data"]["request_token"];
        else
            return "failure:" + result["message"];
    }

    private string GenerateSignature(string auth_token)
    {
        string key = _apiKey + auth_token;
        byte[] keyInBytes = Encoding.UTF8.GetBytes(key);
        byte[] secretInBytes = Encoding.UTF8.GetBytes(_apiSecret);
        return Utils.GenerateHMACSignature(keyInBytes, secretInBytes);
    }

    private string GetAccessToken(string Url, string auth_token)
    {
        string signature = GenerateSignature(auth_token);
        var RequestBody = new Dictionary<string, string> {
               {"api_key", _apiKey},
               {"signature", signature},
               {"req_token", auth_token}
            };
        string Response = Utils.SendHttpRequest("POST", Url, RequestBody);
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        if (parsedResponse["status"] == "success")
            return parsedResponse["data"]["access_token"];
        else
            return "failure:" + parsedResponse["message"];
    }

    public bool LoginAndSetAccessToken()
    {
        string request_token = LoginWithAPIKey(GetLoginWithAPIKeyUrl());
        if (request_token.Split(":")[0] == "failure")
        {
            Console.WriteLine(request_token.Split(":")[1]);
            return false;
        }
        string auth_token = Verify2FA(Get2FA_VerifyUrl(), request_token);
        if (request_token.Split(":")[0] == "failure")
        {
            Console.WriteLine(request_token.Split(":")[1]);
            return false;
        }
        _accessToken = GetAccessToken(GetAccessTokenUrl(), auth_token);
        if (request_token.Split(":")[0] == "failure")
        {
            Console.WriteLine(request_token.Split(":")[1]);
            return false;
        }
        Console.WriteLine("access Token ::" + _accessToken);
        return true;
    }

    public Dictionary<string, dynamic> PlaceOrder(
       string variety,
       string action,
       string exchange,
       string token,
       string price,
       string order_type = null,
       string? product_type = "DELIVERY",
       string quantity = null,
       string? disclose_quantity = null,
       string? trigger_price = null,
       string? stop_loss_price = null,
       string? trailing_stop_loss = null,
       string? validity = "DAY",
       string? tag = null
   )
    {
        var param = new Dictionary<string, dynamic>();
        param.Add("variety", variety);
        param.Add("action", action);
        param.Add("exchange", exchange);
        param.Add("token", token);
        param.Add("order_type", order_type);
        param.Add("product_type", product_type);
        param.Add("quantity", quantity);
        param.Add("disclose_quantity", disclose_quantity);
        param.Add("price", price);
        param.Add("trigger_price", trigger_price);
        param.Add("stop_loss_price", stop_loss_price);
        param.Add("trailing_stop_loss", trailing_stop_loss);
        param.Add("validity", validity);
        param.Add("tag", tag);

        string Response = Utils.SendHttpRequest("POST", GetRequestUrlFromEndpoint(_routes["place_order"]), param, GetAdditionalHeaders());

        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);

        return parsedResponse;
    }

    public Dictionary<string, dynamic> ModifyOrder(
          string order_id,
          string variety,
          string exchange,
          string token,
          string order_type,
          string quantity,
          string price,
          string? disclose_quantity = "0",
          string? trigger_price = "0",
          string? stop_loss_price = "0",
          string? validity = ""
    )
    {
        var param = new Dictionary<string, dynamic>();
        param.Add("variety", variety);
        param.Add("order_id", order_id);
        param.Add("exchange", exchange);
        param.Add("token", token);
        param.Add("order_type", order_type);
        param.Add("quantity", quantity);
        param.Add("disclose_quantity", disclose_quantity);
        param.Add("price", price);
        param.Add("trigger_price", trigger_price);
        param.Add("stop_loss_price", stop_loss_price);
        param.Add("validity", validity);

        string Response = Utils.SendHttpRequest("PUT", GetRequestUrlFromEndpoint(_routes["modify_order"]), param, GetAdditionalHeaders());

        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);

        return parsedResponse;
    }

    public Dictionary<string, dynamic> CancelOrder(
       string variety,
       string order_id
    )
    {
        var param = new Dictionary<string, dynamic>();
        param.Add("variety", variety);
        param.Add("order_id", order_id);

        string Response = Utils.SendHttpRequest("DELETE", GetRequestUrlFromEndpoint(_routes["cancel_order"]), param, GetAdditionalHeaders());

        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);

        return parsedResponse;
    }

    public Dictionary<string, dynamic> FundDetails()
    {
        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["fund_details"]), additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> GetInstrumentTokens(string exchange)
    {
        var param = new Dictionary<string, dynamic>
        {
            { "exch", exchange }
        };

        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["scrip_master"]), param, additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> HoldingDetails()
    {
        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["holding_details"]), additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> PositionDetails()
    {
        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["position_details"]), additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> OrderBook()
    {
        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["order_book"]), additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> TradeBook()
    {
        string Response = Utils.SendHttpRequest("GET", GetRequestUrlFromEndpoint(_routes["trade_book"]), additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> ScripMaster(
       string exchange
    )
    {
        var param = new Dictionary<string, dynamic>();
        param.Add("exch", exchange);

        string Response = Utils.SendHttpRequest("POST", GetRequestUrlFromEndpoint(_routes["script_master_details"]), param, additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> GetQuotes(string exchange, List<string> tokens)
    {
        var RequestBody = new Dictionary<string, dynamic> {
                {"exchange", exchange},
                {"tokens", tokens}
            };

        string Response = Utils.SendHttpRequest("POST", GetRequestUrlFromEndpoint(_routes["quotes"]), RequestBody, additionalHeaders: GetAdditionalHeaders());
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }
}
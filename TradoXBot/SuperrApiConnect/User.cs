namespace TradoXBot.SuperrApiConnect;

public class User
{
    private string _userID;
    private string _password;
    public User(string UserID, string Password)
    {
        _userID = UserID;
        _password = Password;
    }

    public string GetUserId()
    {
        return _userID;
    }

    public Dictionary<string, dynamic> Login(string LoginUrl)
    {
        var DataBody = new Dictionary<string, dynamic> {
                {"client_id", _userID},
                {"password", _password}
            };
        var RequestBody = new Dictionary<string, dynamic> {
                {"platform", "api"},
                {"data", DataBody}
            };
        string Response = Utils.SendHttpRequest("POST", LoginUrl, RequestBody);
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }

    public Dictionary<string, dynamic> Verify2FA(string Verify2FA_Url, string request_token)
    {
        Console.WriteLine("Please, enter the OTP ::");
        string otp = Console.ReadLine();

        var DataBody = new Dictionary<string, dynamic> {
                {"client_id", _userID},
                {"token", request_token},
                {"action", "api-key-validation"},
                {"otp", otp}
            };
        var RequestBody = new Dictionary<string, dynamic> {
                {"platform", "api"},
                {"data", DataBody}
            };
        string Response = Utils.SendHttpRequest("POST", Verify2FA_Url, RequestBody);
        Dictionary<string, dynamic> parsedResponse = Utils.JsonDeserialize(Response);
        return parsedResponse;
    }
}
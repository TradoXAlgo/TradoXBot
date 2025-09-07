namespace TradoXBot.SuperrApiConnect;

public class Ticker
{
    public WebSocket _ws;
    int _timerTick = 5;
    private int _interval = 5;
    private byte _subscriptionMode;
    public Ticker(string APIKey, string access_token, string UserId, int bufferLen = 2000, int cancelTimeInSeconds = 86400)
    {
        _ws = new WebSocket(APIKey, access_token, UserId, bufferLen, cancelTimeInSeconds);
    }

    public void SetCancellationTimeInSeconds(int timeInSeconds = Constants.EOD)
    {
        _ws.SetCancellationTimeInSeconds(timeInSeconds);
    }

    public void Subscribe(String[] Tokens, UInt32 Mode = Constants.MODE_LTP)
    {
        if (Tokens.Length == 0) return;

        if (IsConnected())
        {
            _ws.Subscribe(Tokens, Mode);
        }
    }

    private bool IsConnected()
    {
        return _ws.IsConnected();
    }

    public async Task Connect()
    {
        if (!IsConnected())
        {
            await _ws.Connect();
        }
    }
}

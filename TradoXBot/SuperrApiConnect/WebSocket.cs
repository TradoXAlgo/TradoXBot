using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;


namespace TradoXBot.SuperrApiConnect;

public delegate void OnErrorHandler(string Message);
public delegate void OnCloseHandler();
public delegate void OnDataHandler(byte[] Data);
public class WebSocket
{
    private string WSRootUrl = "ws://inmob.stoxkart.com:7763";
    private string? WSUrl;
    private int _buffer;
    private string? _userID;
    private string? _authToken;

    private ClientWebSocket client = new();
    private int _cancelAfter;
    CancellationTokenSource cTs = new CancellationTokenSource();

    public WebSocket(string ApiKey, string access_token, string UserId, int bufferLength, int cancelTimeInSeconds)
    {
        WSUrl = string.Format("{0}?api_key={1}&request_token={2}", WSRootUrl, ApiKey, access_token);
        _userID = UserId;
        _authToken = access_token;
        _buffer = bufferLength;
        SetCancellationTimeInSeconds(cancelTimeInSeconds);
        Connect().Wait();
        Thread t = new Thread(() => ReceiveTicks().Wait());
        t.Start();
    }

    public void SetCancellationTimeInSeconds(int duration)
    {
        string eod = "23:59:59.9999999";
        DateTime time = DateTime.Parse(eod);
        TimeSpan timeSpan = (time - DateTime.Now);
        int timeDiff = (timeSpan.Hours * 3600) + (timeSpan.Minutes * 60) + timeSpan.Seconds;
        if (duration > timeDiff)
        {
            Console.WriteLine("The duration provided is greater than today EOD, hence setting it todays EOD.");
            _cancelAfter = timeDiff;
        }
        else
        {
            _cancelAfter = duration;
        }
    }
    public bool IsConnected()
    {
        if (client is null)
            return false;

        return client.State == WebSocketState.Open;
    }

    public async Task Connect()
    {
        client = new ClientWebSocket();
        Uri WSUriUrl = new Uri(WSUrl);
        cTs.CancelAfter(TimeSpan.FromSeconds(_cancelAfter));
        try
        {
            await client.ConnectAsync(WSUriUrl, cTs.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine("Exception caugnt in connecting WebSocket. Message ::" + e.Message);
        }

        Console.WriteLine("WebSocket Connected!!");
    }

    private WSSubscribeRequest GetBasicRequestStructure(UInt32 Mode)
    {
        WSRequestHeader requestHeader = new WSRequestHeader();
        requestHeader.sClientId = _userID;
        requestHeader.sAuthToken = _authToken;
        requestHeader.iRequestCode = (byte)Mode;

        WSSubscribeRequest subscribeRequest = new WSSubscribeRequest();
        requestHeader.iMsgLength = (ushort)Marshal.SizeOf(subscribeRequest);
        subscribeRequest.bHeader = requestHeader;

        subscribeRequest.WName = _userID;
        subscribeRequest.secIdxCode = -1;
        subscribeRequest.ScripCount = (byte)1;
        return subscribeRequest;
    }

    private byte GetExchangeCode(string Exchange)
    {
        Exchange = Exchange.ToUpper();
        switch (Exchange)
        {
            case "NSE_CASH":
                return Constants.NSE_CASH;
            case "NSE_FNO":
                return Constants.NSE_FNO;
            case "NSE_CURRENCY":
                return Constants.NSE_CURRENCY;
            case "BSE_CASH":
                return Constants.BSE_CASH;
            case "MCX_COMMODITIES":
                return Constants.MCX_COMMODITIES;
            case "NCEDEXCX_COMMODITIES":
                return Constants.NCEDEXCX_COMMODITIES;
            default:
                return (byte)255;
        }
    }

    public void Subscribe(string[] Tokens, UInt32 Mode)
    {
        WSSubscribeRequest subscribeRequest = GetBasicRequestStructure(Mode);
        subscribeRequest.scripId = new SCRIPID[Tokens.Length];
        for (int i = 0; i < Tokens.Length; i++)
        {
            WSSubscribeRequest currentSubscribeRequest = subscribeRequest;
            try
            {
                currentSubscribeRequest.ExchSeg = GetExchangeCode(Tokens[i].Split(':')[0].Trim());
                currentSubscribeRequest.scripId[0] = new SCRIPID(Tokens[i].Split(':')[1].Trim());
                ArraySegment<byte> byteToSend = new ArraySegment<byte>(Utils.StructToBytes(currentSubscribeRequest, currentSubscribeRequest.bHeader.iMsgLength));
                if (IsConnected())
                    client.SendAsync(byteToSend, WebSocketMessageType.Text, true, cTs.Token).Wait();
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception in struct formation. Message ::" + e.Message);
            }
        }
    }

    private async Task ReceiveTicks()
    {
        byte[] buffer = new byte[_buffer];
        while (client.State == WebSocketState.Open)
        {
            ArraySegment<byte> byteReceived = new ArraySegment<byte>(buffer, 0, _buffer);
            WebSocketReceiveResult response = await client.ReceiveAsync(byteReceived, cTs.Token);
            if (response.MessageType == WebSocketMessageType.Close)
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            else
            {
                string receivedMessage = Encoding.UTF8.GetString(buffer, 0, response.Count);
            }
            ProcessDataReceived(buffer);
        }
    }

    private void ProcessDataReceived(byte[] buffer)
    {
        WSResponseHeader wsResponseHeader = Utils.ByteArrayToStructure<WSResponseHeader>(buffer, Constants.RESPONSE_HEADER_SIZE);
        switch (wsResponseHeader.MsgCode.ToString())
        {
            case Constants.RESP_MKT_STATUS:
                ReadMarketStatusData(buffer, wsResponseHeader);
                break;
            case Constants.RESP_LTP:
                ReadLTPMode(buffer, wsResponseHeader);
                break;
            case Constants.RESP_QUOTE:
                ReadQUOTEMode(buffer, wsResponseHeader);
                break;
            case Constants.RESP_FULL:
                ReadFULLMode(buffer, wsResponseHeader);
                break;
            case Constants.RESP_IDX_QUOTE:
                ReadIDXQUOTEMode(buffer, wsResponseHeader);
                break;
            case Constants.RESP_IDX_FULL:
                ReadIDXFULLMode(buffer, wsResponseHeader);
                break;
            default:
                Console.WriteLine("incorrect Message Code (" + wsResponseHeader.MsgCode.ToString() + ") Received");
                break;
        }
    }

    private void ReadMarketStatusData(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        MarketStatusStruct response = Utils.ByteArrayToStructure<MarketStatusStruct>(buffer, wsResponseHeader.MsgLength);
        Console.WriteLine("MKT_STATUS ::\n" +
                          "{\"bHeader\" : " +
                                "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
                                "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
                                "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
                                "\"ScripId\" : " + wsResponseHeader.ScripId +
                                "}, " +
                            "\"Mkt_Type\" : \"" + response.Mkt_Type + "\", " +
                            "\"Mkt_Status\" : " + response.Mkt_Type + "}");
        return;
    }

    private void ReadLTPMode(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        WSLTPResponse response = Utils.ByteArrayToStructure<WSLTPResponse>(buffer, wsResponseHeader.MsgLength);
        Console.WriteLine("LTP_MODE ::\n" +
                            "{\"bHeader\" : " +
                                "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
                                "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
                                "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
                                "\"ScripId\" : " + wsResponseHeader.ScripId +
                                "}, " +
                            "\"LTP\" : " + response.LTP + ", " +
                            "\"LTT\" : " + response.LTT + ", " +
                            "\"iSecId\" : " + response.iSecId + ", " +
                            "\"traded\" : \"" + response.traded.ToString() + "\", " +
                            "\"mode\" : \"" + response.mode.ToString() + "\", " +
                            "\"fchange\" : " + response.fchange + ", " +
                            "\"fperChange\" : " + response.fperChange + "}");
        return;
    }

    private void ReadQUOTEMode(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        WSQuoteResponse response = Utils.ByteArrayToStructure<WSQuoteResponse>(buffer, wsResponseHeader.MsgLength);
        Console.WriteLine("QUOTE_MODE ::\n" +
                            "{\"bHeader\" : " +
                                "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
                                "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
                                "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
                                "\"ScripId\" : " + wsResponseHeader.ScripId +
                                "}, " +
                            "\"LTP\" : " + response.LTP + ", " +
                            "\"LTT\" : " + response.LTT + ", " +
                            "\"iSecId\" : " + response.iSecId + ", " +
                            "\"traded\" : \"" + response.traded.ToString() + "\", " +
                            "\"mode\" : \"" + response.mode.ToString() + "\", " +
                            "\"LTQ\" : " + response.LTQ + ", " +
                            "\"APT\" : " + response.APT + ", " +
                            "\"Vtraded\" : " + response.Vtraded + ", " +
                            "\"TotalBuyQ\" : " + response.TotalBuyQ + ", " +
                            "\"TotalSellQ\" : " + response.TotalSellQ + ", " +
                            "\"fOpen\" : " + response.fOpen + ", " +
                            "\"fClose\" : " + response.fClose + ", " +
                            "\"fHigh\" : " + response.fHigh + ", " +
                            "\"fLow\" : " + response.fLow + ", " +
                            "\"fperChange\" : " + response.fperChange + ", " +
                            "\"fchange\" : " + response.fchange + ", " +
                            "\"f52WKHigh\" : " + response.f52WKHigh + ", " +
                            "\"f52WKLow\" : " + response.f52WKLow + "}");
        return;
    }

    private void ReadFULLMode(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        WSFullModeResponse response = Utils.ByteArrayToStructure<WSFullModeResponse>(buffer, wsResponseHeader.MsgLength);
        string MBPRow = "[";
        for (int i = 0; i < response.submbp.Length; i++)
        {
            MBPRow += "{" +
                            "\"iBuyqty\" : " + response.submbp[i].iBuyqty + ", " +
                            "\"iSellqty\" : " + response.submbp[i].iSellqty + ", " +
                            "\"iBuyordno\" : " + response.submbp[i].iBuyordno + ", " +
                            "\"iSellordno\" : " + response.submbp[i].iSellqty + ", " +
                            "\"fBuyprice\" : " + response.submbp[i].fBuyprice + ", " +
                            "\"fSellprice\" : " + response.submbp[i].fSellprice + "}";
            if (i != (response.submbp.Length - 1))
            {
                MBPRow += ", ";
            }
        }
        MBPRow += "]";
        Console.WriteLine("FULL_MODE ::\n" +
        "{\"bHeader\" : " +
            "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
            "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
            "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
            "\"ScripId\" : " + wsResponseHeader.ScripId +
            "}, " +
        "\"submbp\" : " + MBPRow + ", " +
        "\"LTP\" : " + response.LTP + ", " +
        "\"LTT\" : " + response.LTT + ", " +
        "\"iSecId\" : " + response.iSecId + ", " +
        "\"traded\" : \"" + response.traded.ToString() + "\", " +
        "\"mode\" : \"" + response.mode.ToString() + "\", " +
        "\"LTQ\" : " + response.LTQ + ", " +
        "\"APT\" : " + response.APT + ", " +
        "\"Vtraded\" : " + response.Vtraded + ", " +
        "\"TotalBuyQ\" : " + response.TotalBuyQ + ", " +
        "\"TotalSellQ\" : " + response.TotalSellQ + ", " +
        "\"fOpen\" : " + response.fOpen + ", " +
        "\"fClose\" : " + response.fClose + ", " +
        "\"fHigh\" : " + response.fHigh + ", " +
        "\"fLow\" : " + response.fLow + ", " +
        "\"fperChange\" : " + response.fperChange + ", " +
        "\"fchange\" : " + response.fchange + ", " +
        "\"f52WKHigh\" : " + response.f52WKHigh + ", " +
        "\"f52WKLow\" : " + response.f52WKLow + ", " +
        "\"OI\" : " + response.OI + ", " +
        "\"OIChange\" : " + response.OIChange + "}");
        return;
    }

    private void ReadIDXQUOTEMode(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        WSIndexQuoteResponse response = Utils.ByteArrayToStructure<WSIndexQuoteResponse>(buffer, wsResponseHeader.MsgLength);
        Console.WriteLine("IDX_QUOTE_MODE ::\n" +
                            "{\"bHeader\" : " +
                                "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
                                "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
                                "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
                                "\"ScripId\" : " + wsResponseHeader.ScripId +
                                "}, " +
                            "\"LTP\" : " + response.LTP + ", " +
                            "\"iSecId\" : " + response.iSecId + ", " +
                            "\"traded\" : \"" + response.traded.ToString() + "\", " +
                            "\"mode\" : \"" + response.mode.ToString() + "\", " +
                            "\"fOpen\" : " + response.fOpen + ", " +
                            "\"fClose\" : " + response.fClose + ", " +
                            "\"fHigh\" : " + response.fHigh + ", " +
                            "\"fLow\" : " + response.fLow + ", " +
                            "\"fperChange\" : " + response.fperChange + ", " +
                            "\"fchange\" : " + response.fchange + ", " +
                            "\"f52WKHigh\" : " + response.f52WKHigh + ", " +
                            "\"f52WKLow\" : " + response.f52WKLow + "}");
        return;
    }

    private void ReadIDXFULLMode(byte[] buffer, WSResponseHeader wsResponseHeader)
    {
        WSIndexFullModeResponse response = Utils.ByteArrayToStructure<WSIndexFullModeResponse>(buffer, wsResponseHeader.MsgLength);
        Console.WriteLine("IDX_FULL_MODE ::\n" +
                            "{\"bHeader\" : " +
                                "{\"ExchSeg\" : \"" + wsResponseHeader.ExchSeg.ToString() + "\", " +
                                "\"MsgLength\" : " + wsResponseHeader.MsgLength + ", " +
                                "\"MsgCode\" : \"" + wsResponseHeader.MsgCode.ToString() + "\", " +
                                "\"ScripId\" : " + wsResponseHeader.ScripId +
                                "}, " +
                             "\"LTP\" : " + response.LTP + ", " +
                              "\"iSecId\" : " + response.iSecId + ", " +
                            "\"traded\" : \"" + response.traded.ToString() + "\", " +
                            "\"mode\" : \"" + response.mode.ToString() + "\", " +
                            "\"fOpen\" : " + response.fOpen + ", " +
                            "\"fClose\" : " + response.fClose + ", " +
                            "\"fHigh\" : " + response.fHigh + ", " +
                            "\"fLow\" : " + response.fLow + ", " +
                            "\"fperChange\" : " + response.fperChange + ", " +
                            "\"fchange\" : " + response.fchange + ", " +
                            "\"LTT\" : " + response.LTT + "}");
        return;
    }
}



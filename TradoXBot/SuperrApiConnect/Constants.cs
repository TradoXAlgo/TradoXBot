namespace TradoXBot.SuperrApiConnect;

public class Constants
{
    // Exchange Codes For Ticks
    public const byte NSE_CASH = (byte)1;
    public const byte NSE_FNO = (byte)2;
    public const byte NSE_CURRENCY = (byte)3;
    public const byte BSE_CASH = (byte)4;
    public const byte MCX_COMMODITIES = (byte)5;
    public const byte NCEDEXCX_COMMODITIES = (byte)6;

    // Subscription Modes
    public const UInt32 MODE_LTP = 71;
    public const UInt32 MODE_QUOTE = 72;
    public const UInt32 MODE_FULL = 73;
    public const UInt32 MODE_INDEX_LTP = 74;
    public const UInt32 MODE_INDEX_QUOTE = 75;
    public const UInt32 MODE_INDEX_FULL = 76;

    // Response Message Code
    public const string RESP_LTP = "61";
    public const string RESP_QUOTE = "62";
    public const string RESP_FULL = "63";
    public const string RESP_IDX_LTP = "64";
    public const string RESP_IDX_QUOTE = "65";
    public const string RESP_IDX_FULL = "66";
    public const string RESP_MKT_STATUS = "29";

    // Response Structure Sizes
    public const int RESPONSE_HEADER_SIZE = 10;

    // 24 hrs time in Seconds (24*60*60)
    public const int EOD = 86400;
}
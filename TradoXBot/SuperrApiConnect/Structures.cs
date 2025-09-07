using System.Runtime.InteropServices;

namespace TradoXBot.SuperrApiConnect;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSRequestHeader
{
    public byte iRequestCode;
    public ushort iMsgLength;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 30)]
    public string sClientId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)]
    public string sAuthToken;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SCRIPID
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
    public string ScripCode;
    public SCRIPID(string _ScripID)
    {
        ScripCode = _ScripID;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSSubscribeRequest
{
    public WSRequestHeader bHeader;
    public byte ExchSeg;
    public int secIdxCode;
    public byte ScripCount;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 20)]
    public string WName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public SCRIPID[] scripId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSResponseHeader
{
    public byte ExchSeg;
    public int MsgLength;
    public byte MsgCode;
    public uint ScripId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSLTPResponse
{
    public WSResponseHeader respHeader;
    public float LTP;
    public int LTT;
    public uint iSecId;
    public byte traded;
    public byte mode;
    public float fchange;
    public float fperChange;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSQuoteResponse
{
    public WSResponseHeader respHeader;
    public float LTP;
    public int LTT;
    public uint iSecId;
    public byte traded;
    public byte mode;
    public int LTQ;
    public float APT;
    public float Vtraded;
    public uint TotalBuyQ;
    public uint TotalSellQ;
    public float fOpen;
    public float fClose;
    public float fHigh;
    public float fLow;
    public float fperChange;
    public float fchange;
    public float f52WKHigh;
    public float f52WKLow;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSIndexQuoteResponse
{
    public WSResponseHeader respHeader;
    public float LTP;
    public uint iSecId;
    public byte traded;
    public byte mode;
    public float fOpen;
    public float fClose;
    public float fHigh;
    public float fLow;
    public float fperChange;
    public float fchange;
    public float f52WKHigh;
    public float f52WKLow;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MBPRowStruct
{
    public uint iBuyqty;
    public uint iSellqty;
    public ushort iBuyordno;
    public ushort iSellordno;
    public float fBuyprice;
    public float fSellprice;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSFullModeResponse
{
    public WSResponseHeader respHeader;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
    public MBPRowStruct[] submbp;
    public float LTP;
    public int LTT;
    public uint iSecId;
    public byte traded;
    public byte mode;
    public int LTQ;
    public float APT;
    public float Vtraded;
    public uint TotalBuyQ;
    public uint TotalSellQ;
    public float fOpen;
    public float fClose;
    public float fHigh;
    public float fLow;
    public float fperChange;
    public float fchange;
    public float f52WKHigh;
    public float f52WKLow;
    public int OI;
    public int OIChange;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WSIndexFullModeResponse
{
    public WSResponseHeader respHeader;
    public float LTP;
    public uint iSecId;
    public byte traded;
    public byte mode;
    public float fOpen;
    public float fClose;
    public float fHigh;
    public float fLow;
    public float fperChange;
    public float fchange;
    public int LTT;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MarketStatusStruct
{
    public WSResponseHeader respHeader;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 3)]
    public string Mkt_Type;
    public float Mkt_Status;
}
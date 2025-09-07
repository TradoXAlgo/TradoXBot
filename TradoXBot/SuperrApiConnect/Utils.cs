using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Collections;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System;

namespace TradoXBot.SuperrApiConnect;

public class Utils
{
    public static string SendHttpRequest(string requestType, string url, dynamic RequestParams = null, Dictionary<string, string> additionalHeaders = null, dynamic UrlParams = null)
    {
        if (url.Contains("{") && RequestParams != null)
        {
            var routeParams = (RequestParams as Dictionary<string, dynamic>).ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (KeyValuePair<string, dynamic> item in routeParams)
            {
                if (url.Contains("{" + item.Key + "}"))
                {
                    if (item.Key == "variety")
                    {
                        url = url.Replace("{" + item.Key + "}", (string)item.Value.ToLower().Trim());
                    }
                    else
                    {
                        url = url.Replace("{" + item.Key + "}", (string)item.Value);
                    }
                    RequestParams.Remove(item.Key);
                }
            }
        }
        requestType = requestType.ToUpper().Trim();
        string response = "";
        using var client = new HttpClient();
        if (additionalHeaders != null)
        {
            foreach (KeyValuePair<string, string> entry in additionalHeaders)
            {
                client.DefaultRequestHeaders.Add(entry.Key, entry.Value);
            }
        }
        string JsonData = "";
        StringContent RequestData;
        switch (requestType)
        {
            case "GET":
                response = GetRequest(client, url).Result;
                break;
            case "POST":
                JsonData = JsonSerializer.Serialize(RequestParams);
                RequestData = new StringContent(JsonData, Encoding.UTF8, "application/json");
                response = PostRequest(client, url, RequestData).Result;
                break;
            case "PUT":
                JsonData = JsonSerializer.Serialize(RequestParams);
                RequestData = new StringContent(JsonData, Encoding.UTF8, "application/json");
                response = PutRequest(client, url, RequestData).Result;
                break;
            case "DELETE":
                response = DeleteRequest(client, url).Result;
                break;
            default:
                Console.WriteLine("The RequestType \"" + requestType + "\" made on url " + url + " is inappropriate");
                break;
        }
        return response;
    }

    private static async Task<string> PostRequest(HttpClient client, string url, StringContent data)
    {
        try
        {
            var httpResponseMessage = await client.PostAsync(url, data);
            //httpResponseMessage.EnsureSuccessStatusCode();
            return httpResponseMessage.Content.ReadAsStringAsync().Result;
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine("Request error: " + e.Message);
            throw;
        }
    }

    private static async Task<string> PutRequest(HttpClient client, string url, StringContent data)
    {
        var httpResponseMessage = await client.PutAsync(url, data);
        return httpResponseMessage.Content.ReadAsStringAsync().Result;
    }

    private static async Task<string> GetRequest(HttpClient client, string url)
    {
        return await client.GetStringAsync(url);
    }

    private static async Task<string> DeleteRequest(HttpClient client, string url)
    {
        var httpResponseMessage = await client.DeleteAsync(url);
        return httpResponseMessage.Content.ReadAsStringAsync().Result;
    }

    public static Dictionary<string, dynamic> JsonDeserialize(string JsonData)
    {
        try
        {
            JsonElement jsonElement = JsonSerializer.Deserialize<JsonElement>(JsonData);
            Dictionary<string, dynamic> json2Dictionary = JsonElementToDictionary(jsonElement);
            return json2Dictionary;
        }
        catch (Exception e)
        {
            Console.WriteLine("Caught in exception with message ::" + e.Message + " Json Data received is ::" + JsonData);
        }
        return null;
    }

    private static decimal StringToDecimal(String value)
    {
        return decimal.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
    }

    private static dynamic JsonElementToDictionary(JsonElement jsonElement)
    {
        if (jsonElement.ValueKind == JsonValueKind.Number)
        {
            return StringToDecimal(jsonElement.GetRawText());
        }
        else if (jsonElement.ValueKind == JsonValueKind.String)
        {
            return jsonElement.GetString();
        }
        else if (jsonElement.ValueKind == JsonValueKind.True || jsonElement.ValueKind == JsonValueKind.False)
        {
            return jsonElement.GetBoolean();
        }
        else if (jsonElement.ValueKind == JsonValueKind.Object)
        {
            var map = jsonElement.EnumerateObject().ToList();
            var newMap = new Dictionary<String, dynamic>();
            for (int i = 0; i < map.Count; i++)
            {
                newMap.Add(map[i].Name, JsonElementToDictionary(map[i].Value));
            }
            return newMap;
        }
        else if (jsonElement.ValueKind == JsonValueKind.Array)
        {
            var items = jsonElement.EnumerateArray().ToList();
            var newItems = new ArrayList();
            for (int i = 0; i < items.Count; i++)
            {
                newItems.Add(JsonElementToDictionary(jsonElement[i]));
            }
            return newItems;
        }
        else
        {
            return null;
        }
    }

    public static string GenerateHMACSignature(byte[] keyInBytes, byte[] secretInBytes)
    {
        using HMACSHA256 hmac = new HMACSHA256(keyInBytes);
        byte[] signatureInBytes = hmac.ComputeHash(secretInBytes);
        return BitConverter.ToString(signatureInBytes).Replace("-", "").ToLower();
    }

    public static byte[] StructToBytes(object _struct, int structSize)
    {
        try
        {
            IntPtr intPtr = Marshal.AllocHGlobal(structSize);
            Marshal.StructureToPtr(_struct, intPtr, fDeleteOld: true);
            byte[] array = new byte[structSize];
            Marshal.Copy(intPtr, array, 0, structSize);
            Marshal.FreeHGlobal(intPtr);
            return array;
        }
        catch (Exception innerException)
        {
            ArgumentException ex = new ArgumentException("Error in conversion from struct to bytearray.", innerException);
            throw ex;
        }
    }

    public static T ByteArrayToStructure<T>(byte[] bytes, int structSize) where T : struct
    {
        try
        {
            IntPtr intPtr = Marshal.AllocHGlobal(structSize);
            Marshal.Copy(bytes, 0, intPtr, structSize);
            T result = (T)Marshal.PtrToStructure(intPtr, typeof(T));
            Marshal.FreeHGlobal(intPtr);
            return result;
        }
        catch (Exception innerException)
        {
            ArgumentException ex = new ArgumentException("Error in conversion from ByteArray to Struct.", innerException);
            throw ex;
        }
    }

    public static Tuple<string, Dictionary<string, dynamic>> GetRequestUrl(string url, dynamic RequestParams)
    {
        if (url.Contains("{") && RequestParams != null)
        {
            var routeParams = (RequestParams as Dictionary<string, dynamic>).ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (KeyValuePair<string, dynamic> item in routeParams)
            {
                if (url.Contains("{" + item.Key + "}"))
                {
                    url = url.Replace("{" + item.Key + "}", (string)item.Value);
                    RequestParams.Remove(item.Key);
                }
            }

        }
        return Tuple.Create(url, RequestParams);
    }
}

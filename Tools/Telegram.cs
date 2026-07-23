using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Tools;

// Telegram secrets — kept OUT of source control. Loaded once at startup from a machine-local
// file (see Telegram.ConfigPath: S:\Telegram.json on Windows, /mnt/s/Telegram.json on Linux).
// A template lives at Tools/Telegram.example.json.
[RegisterJson]
public struct TelegramConfig
{
    public string AlertChatId;
    public string NotificationChatId;
    public string ApiToken;
}

// https://core.telegram.org/bots/api#available-methods
public sealed class Telegram : IDisposable
{
    // Cross-platform via FileSystemPath: S:\Telegram.json (Windows) <-> /mnt/s/Telegram.json (Linux).
    private static readonly FileSystemPath ConfigPath = @"S:\Telegram.json";
    private static readonly TelegramConfig s_config = LoadConfig();

    public static string AlertChatId => s_config.AlertChatId;
    public static string NotificationChatId => s_config.NotificationChatId;

    private const string ApiBase = "https://api.telegram.org/";
    private const int MaxMessageLength = 4000;
    private static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(3);
    private static readonly string Separator = Environment.NewLine + Environment.NewLine;

    public Logger? Logger { get; set; }

    private readonly string _chatId;
    private readonly HttpClient _http;
    private readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
    private readonly Thread _thread;
    private volatile bool _disposed;

    private static TelegramConfig LoadConfig()
    {
        try
        {
            return Json.Deserialize<TelegramConfig>(System.IO.File.ReadAllText(ConfigPath));
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Telegram: could not load config from {ConfigPath.Path} ({exception.Message}); alerts disabled.");
            return default;
        }
    }

    public Telegram(string chatId)
    {
        _chatId = chatId;
        _http = new HttpClient { BaseAddress = new Uri($"{ApiBase}bot{s_config.ApiToken}/") };
        _thread = LowLatency.StartBackgroundThread("Telegram", ConsumeLoop);
    }

    public void Send(params object[] objects)
    {
        if (_disposed) return;
        try
        {
            _queue.Add(string.Join(Environment.NewLine, objects));
        }
        catch
        {
        }
    }

    private void ConsumeLoop()
    {
        StringBuilder buffer = new(MaxMessageLength);
        try
        {
            foreach (string first in _queue.GetConsumingEnumerable())
            {
                AppendOrChunk(buffer, first);
                while (_queue.TryTake(out string? next))
                {
                    AppendOrChunk(buffer, next);
                }
                Flush(buffer);
            }
        }
        catch (Exception exception)
        {
            try { Logger?.Log("Telegram", exception); } catch { Console.WriteLine(exception); }
        }
    }

    private void AppendOrChunk(StringBuilder builder, string item)
    {
        // Fits in one message: flush pending if it won't co-pack, then append whole — never split an item that didn't need it.
        if (item.Length <= MaxMessageLength)
        {
            int sepLen = builder.Length == 0 ? 0 : Separator.Length;
            if (builder.Length + sepLen + item.Length > MaxMessageLength)
            {
                Flush(builder);
            }
            if (builder.Length > 0) builder.Append(Separator);
            builder.Append(item);
            return;
        }

        // Item exceeds a single Telegram message: flush pending, then split across N full-sized messages.
        Flush(builder);
        int idx = 0;
        while (idx < item.Length)
        {
            int take = Math.Min(item.Length - idx, MaxMessageLength);
            builder.Append(item, idx, take);
            idx += take;
            if (idx < item.Length)
            {
                Flush(builder);
            }
        }
    }

    private void Flush(StringBuilder builder)
    {
        if (builder.Length == 0) return;

        string text = builder.ToString();
        builder.Clear();

        try
        {
            string endpoint = $"sendMessage?chat_id={_chatId}&text={WebUtility.UrlEncode(text)}";
            using HttpResponseMessage response = _http.Send(new HttpRequestMessage(HttpMethod.Get, endpoint));
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception)
        {
            try { Logger?.Log("Telegram", text, exception); } catch { Console.WriteLine(exception); }
        }

        Thread.Sleep(SendInterval);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        try { _thread.Join(TimeSpan.FromSeconds(10)); } catch { }

        _http.Dispose();
        _queue.Dispose();
    }
}

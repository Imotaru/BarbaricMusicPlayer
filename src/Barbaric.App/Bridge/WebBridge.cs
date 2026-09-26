using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Barbaric.App.Bridge;

/// <summary>
/// JSON channel between the web UI and the host.
/// UI to host: <c>{ id, method, params }</c>.
/// Host to UI: <c>{ id, ok, result }</c> / <c>{ id, ok: false, error }</c> replies, and <c>{ event, data }</c> pushes.
/// </summary>
public sealed class WebBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly CoreWebView2 _web;
    private readonly Dispatcher _dispatcher;
    private readonly Uri _trustedOrigin;
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new(StringComparer.Ordinal);

    public WebBridge(CoreWebView2 web, Dispatcher dispatcher, Uri trustedOrigin)
    {
        _web = web;
        _dispatcher = dispatcher;
        _trustedOrigin = trustedOrigin;
        _web.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>Registers a method with no result.</summary>
    public void Command(string method, Action<JsonElement> handler) =>
        _handlers[method] = p =>
        {
            handler(p);
            return Task.FromResult<object?>(null);
        };

    /// <summary>Registers an async method with no result.</summary>
    public void CommandAsync(string method, Func<JsonElement, Task> handler) =>
        _handlers[method] = async p =>
        {
            await handler(p);
            return null;
        };

    /// <summary>Registers a method that returns a result.</summary>
    public void Query(string method, Func<JsonElement, object?> handler) =>
        _handlers[method] = p => Task.FromResult(handler(p));

    /// <summary>Registers an async method that returns a result.</summary>
    public void QueryAsync(string method, Func<JsonElement, Task<object?>> handler) =>
        _handlers[method] = handler;

    /// <summary>Pushes an event to the UI. Safe to call from any thread.</summary>
    public void Emit(string eventName, object? data = null)
    {
        var json = JsonSerializer.Serialize(new { @event = eventName, data }, JsonOptions);
        if (_dispatcher.CheckAccess())
        {
            _web.PostWebMessageAsJson(json);
        }
        else
        {
            _dispatcher.BeginInvoke(() => _web.PostWebMessageAsJson(json));
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedSource(e.Source))
        {
            return;
        }

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(e.WebMessageAsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (request is null)
        {
            return;
        }

        object reply;
        try
        {
            if (!_handlers.TryGetValue(request.Method, out var handler))
            {
                throw new InvalidOperationException($"Unknown method '{request.Method}'.");
            }

            reply = new { id = request.Id, ok = true, result = await handler(request.Params) };
        }
        catch (Exception ex)
        {
            reply = new { id = request.Id, ok = false, error = ex.Message };
        }

        _web.PostWebMessageAsJson(JsonSerializer.Serialize(reply, JsonOptions));
    }

    private bool IsTrustedSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && Uri.Compare(uri, _trustedOrigin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private sealed record Request(int Id, string Method, JsonElement Params);
}

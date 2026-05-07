using System;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCode.UI
{
    /// <summary>
    /// Handles C#/JS communication via WebView2 message routing.
    /// JS -> C#: Uses window.chrome.webview.postMessage() received via WebMessageReceived.
    /// C# -> JS: Uses ExecuteScriptAsync() to call window.receiveFromJava().
    /// Port of com.anthropic.claude.intellij.ui.WebviewBridge.
    /// </summary>
    public class WebviewBridge : IDisposable
    {
        private readonly WebView2 _webView;

        /// <summary>
        /// Handler receiving (type, jsonData) messages from the webview.
        /// </summary>
        public Action<string, string>? MessageHandler { get; set; }

        public WebviewBridge(WebView2 webView)
        {
            _webView = webView;
            _webView.WebMessageReceived += OnWebMessageReceived;
        }

        // Serializing semaphore — async void ExecuteScriptAsync calls can complete out of order,
        // which caused state transitions like Stopped→Starting→Running to be applied in JS as
        // [Running, Stopped] (last write wins on disorder). Funnel everything through one queue.
        private readonly System.Threading.SemaphoreSlim _sendGate = new(1, 1);

        /// <summary>
        /// Sends a message from C# to the webview JavaScript.
        /// Calls window.receiveFromJava(type, data) in the browser.
        /// </summary>
        public async void SendToWebview(string type, string jsonData)
        {
            if (_webView.CoreWebView2 == null) return;

            var escapedType = JsonConvert.SerializeObject(type);
            // jsonData is already a JSON string, embed it directly
            var script = $"if (window.receiveFromJava) {{ window.receiveFromJava({escapedType}, {jsonData}); }}";

            // Serialize sends so events arrive in the order they were fired (event order matters
            // for state transitions like Stopping→Stopped→Starting→Running).
            await _sendGate.WaitAsync();
            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebviewBridge] Error executing JS: {ex.Message}");
            }
            finally
            {
                _sendGate.Release();
            }
        }

        /// <summary>
        /// Injects the bridge function into the webview.
        /// Creates window.__sendToJava() which routes to postMessage.
        /// </summary>
        public async void InjectBridgeFunction()
        {
            LogDebug("InjectBridgeFunction called, CoreWebView2=" + (_webView.CoreWebView2 != null));
            if (_webView.CoreWebView2 == null) return;

            var script = @"
(function() {
    window.__sendToJava = function(request) {
        window.chrome.webview.postMessage(request);
    };
    if (window.__onBridgeReady) { window.__onBridgeReady(); }
})();";

            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebviewBridge] Error injecting bridge: {ex.Message}");
            }
        }

        private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var request = e.TryGetWebMessageAsString();
                LogDebug($"OnWebMessageReceived: raw='{(request?.Length > 100 ? request.Substring(0, 100) : request)}'");
                if (string.IsNullOrEmpty(request)) return;

                var message = JObject.Parse(request);
                var type = message.Value<string>("type");
                if (type == null) return;

                var dataObj = message["data"];
                var dataJson = dataObj?.ToString(Formatting.None) ?? "{}";

                LogDebug($"Dispatching type='{type}' handler={MessageHandler != null}");
                MessageHandler?.Invoke(type, dataJson);
            }
            catch (Exception ex)
            {
                LogDebug($"Error: {ex.Message}");
            }
        }

        private static void LogDebug(string msg)
        {
            try
            {
                var logPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCode", "debug.log");
                System.IO.File.AppendAllText(logPath, $"[Bridge {DateTime.Now:HH:mm:ss}] {msg}\n");
            }
            catch { }
        }

        public void Dispose()
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
        }
    }
}

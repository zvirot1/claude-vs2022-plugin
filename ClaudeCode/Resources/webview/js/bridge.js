/**
 * Bridge module for C# <-> JavaScript communication in the WebView2 webview.
 *
 * JS -> C#: sendToJava(type, data) serializes a JSON message and calls
 *           window.__sendToJava() which is injected by WebviewBridge.cs
 *           and routes via chrome.webview.postMessage.
 *
 * C# -> JS: C# calls window.receiveFromJava(type, data) via ExecuteScriptAsync().
 *           The bridge dispatches this to registered handlers via the event system.
 *
 * Adapted from the IntelliJ JCEF bridge to use WebView2's postMessage API.
 */
(function () {
    'use strict';

    var listeners = {};
    var pendingMessages = [];
    var bridgeReady = false;

    function on(type, callback) {
        if (!listeners[type]) {
            listeners[type] = [];
        }
        // Dedup: don't add the same callback twice
        if (listeners[type].indexOf(callback) === -1) {
            listeners[type].push(callback);
        }
    }

    function off(type, callback) {
        if (!listeners[type]) return;
        listeners[type] = listeners[type].filter(function (cb) {
            return cb !== callback;
        });
    }

    function emit(type, data) {
        var handlers = listeners[type];
        if (!handlers) return;
        for (var i = 0; i < handlers.length; i++) {
            try {
                handlers[i](data);
            } catch (e) {
                console.error('[Bridge] Error in handler for "' + type + '":', e);
            }
        }
    }

    function sendToJava(type, data) {
        var message = JSON.stringify({
            type: type,
            data: data || {}
        });

        if (window.__sendToJava) {
            try {
                window.__sendToJava(message);
            } catch (e) {
                console.error('[Bridge] Error sending to C#:', e);
            }
        } else if (window.chrome && window.chrome.webview) {
            // Direct WebView2 fallback before bridge injection
            try {
                window.chrome.webview.postMessage(message);
            } catch (e) {
                console.error('[Bridge] Error posting message:', e);
            }
        } else {
            pendingMessages.push(message);
        }
    }

    function receiveFromJava(type, data) {
        emit(type, data);
    }

    function onBridgeReady() {
        bridgeReady = true;
        if (pendingMessages.length > 0 && window.__sendToJava) {
            for (var i = 0; i < pendingMessages.length; i++) {
                try {
                    window.__sendToJava(pendingMessages[i]);
                } catch (e) {
                    console.error('[Bridge] Error flushing pending message:', e);
                }
            }
            pendingMessages = [];
        }
    }

    window.bridge = {
        on: on,
        off: off,
        emit: emit,
        sendToJava: sendToJava
    };

    window.receiveFromJava = receiveFromJava;
    window.__onBridgeReady = onBridgeReady;

})();

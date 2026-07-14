using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// HTTP server that runs inside the Unity Editor, enabling external MCP tools
    /// to control the editor via REST API calls.
    ///
    /// Supports two modes:
    ///   1. Queue mode (async):  POST /api/queue/submit → poll GET /api/queue/status
    ///   2. Legacy mode (sync):  POST /api/{command}    → blocks until done
    ///
    /// Both modes go through MCPRequestQueue for fair round-robin scheduling.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPBridgeServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static bool _isRunning;

        /// <summary>
        /// The actual port this server is running on.
        /// Resolved at startup via auto-selection or manual override.
        /// </summary>
        private static int _activePort;

        /// <summary>The port this server is currently bound to (0 if not running).</summary>
        public static int ActivePort => _isRunning ? _activePort : 0;

        // Routes whose Unity APIs use async callbacks (fire on next editor frame).
        // Register here instead of adding per-route if-conditions in HandleRequest/HandleQueueSubmit.
        private delegate void DeferredRouteHandler(Dictionary<string, object> args, Action<object> resolve,
            Action<object> progress);

        private static readonly Dictionary<string, DeferredRouteHandler>
            _deferredRoutes = new Dictionary<string, DeferredRouteHandler>
        {
            { "testing/list-tests", (args, resolve, _) => MCPTestRunnerCommands.ListTests(args, resolve) },
            { "advanced/execute", (args, resolve, _) => ExecuteAdvancedRouteDeferred(args, resolve) },
            { "wait/editor-idle", (args, resolve, _) => MCPEditorCommands.WaitForIdle(args, resolve) },
            { "uitoolkit/wait-refresh", (args, resolve, _) => MCPUICommands.WaitForUIToolkitRefresh(args, resolve) },
            { "uitoolkit/builder-preview", (args, resolve, _) => MCPUICommands.OpenUIBuilderPreview(args, resolve) },
            { "screenshot/game", (args, resolve, _) => MCPScreenshotCommands.CaptureGameView(args, resolve) },
            { "packages/update-git", (args, resolve, _) => MCPPackageManagerCommands.UpdateGitPackageDeferred(args, resolve) },
            { "packages/list", (args, resolve, _) => MCPPackageManagerCommands.ListPackagesDeferred(args, resolve) },
            { "packages/add", (args, resolve, _) => MCPPackageManagerCommands.AddPackageDeferred(args, resolve) },
            { "packages/remove", (args, resolve, _) => MCPPackageManagerCommands.RemovePackageDeferred(args, resolve) },
            { "packages/search", (args, resolve, _) => MCPPackageManagerCommands.SearchPackageDeferred(args, resolve) },
            { "prefab-asset/add-component", (args, resolve, _) => MCPPrefabAssetCommands.AddComponentDeferred(args, resolve) },
            { "prefab-asset/transaction-edit", MCPPrefabAssetCommands.TransactionEditDeferred },
            { "asset/import", MCPAssetCommands.ImportDeferred },
            { "asset/move", MCPAssetCommands.MoveDeferred },
            { "component/set-reference", MCPComponentCommands.SetReferencesDeferred },
            { "localization/upsert-entry", (args, resolve, progress) =>
                MCPLocalizationBridge.ExecuteDeferred("localization/upsert-entry", args, resolve, progress) },
        };

        internal static IEnumerable<string> DeferredRouteNames => _deferredRoutes.Keys;

        // SessionState key to persist running state across domain reloads (Play Mode, recompile)
        private const string WasRunningKey = "UnityMCP_WasRunningBeforeReload";

        // Keep MCP work from monopolizing the first Editor update after a compile/domain reload.
        // Individual Unity API calls are not preemptible, so execute at most one queued request
        // per update and wait for the asset pipeline to remain idle briefly before resuming.
        internal const int MaxRequestsPerEditorUpdate = 1;
        internal const double PostReloadProcessingDelaySeconds = 0.5;
        private static double _requestProcessingNotBefore;

        // ─── Manual-port restart retry (unity-mcp-server issue #10) ───
        // Right after a domain reload the configured manual port can be briefly
        // unbindable while the previous listener's socket is released. Auto-port
        // mode survives this (it probes and falls back); manual mode had neither
        // probe nor retry and failed permanently. Retry the SAME port instead.
        private const int MaxManualPortRetries = 10;
        private const double ManualPortRetryDelaySeconds = 0.5;
        private static int _manualPortRetryCount;
        private static double _manualPortRetryAt;
        private static bool _manualPortRetryPending;

        /// <summary>
        /// Whether the MCP bridge may auto-start in this Editor. False on MPPM
        /// Virtual Players when StartOnVirtualPlayers is disabled (issue #21) —
        /// manual start is unaffected.
        /// </summary>
        private static bool AutoStartAllowed =>
            MCPSettingsManager.AutoStart &&
            (MCPSettingsManager.StartOnVirtualPlayers || !MCPScenarioCommands.IsVirtualPlayer());

        static MCPBridgeServer()
        {
            // Skip batch-mode Unity subprocesses (AssetImportWorker, CLI builds, etc.).
            // These are short-lived, don't need MCP access, and would otherwise claim
            // ports in the 7890-7899 range and exhaust availability for real editors.
            if (Application.isBatchMode) return;

            _requestProcessingNotBefore = EditorApplication.timeSinceStartup + PostReloadProcessingDelaySeconds;

            // Restart if: auto-start is allowed (respects the Virtual Player setting)
            // OR the server was running before a domain reload.
            bool wasRunning = SessionState.GetBool(WasRunningKey, false);
            if (AutoStartAllowed || wasRunning)
            {
                Start();
                SessionState.SetBool(WasRunningKey, false);
            }
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// Handle Play Mode transitions to ensure the server stays alive.
        /// Unity triggers a domain reload when entering/exiting Play Mode,
        /// which is handled by the assembly reload callbacks and the SessionState flag.
        /// This callback provides additional resilience for edge cases.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                if (!_isRunning && (AutoStartAllowed || SessionState.GetBool(WasRunningKey, false)))
                {
                    Debug.Log("[MCP Bridge] Restarting server after Play Mode transition...");
                    Start();
                    SessionState.SetBool(WasRunningKey, false);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_isRunning)
            {
                // Persist that we were running, so we restart after reload
                SessionState.SetBool(WasRunningKey, true);
                Stop();
            }
        }

        private static void OnQuitting()
        {
            Stop();
            // Final cleanup of registry on quit
            MCPInstanceRegistry.Unregister();
        }

        /// <summary>Whether the server is currently running.</summary>
        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            if (_isRunning) return;

            // Batch-mode subprocesses (AssetImportWorker, etc.) must never start the server.
            if (Application.isBatchMode) return;

            // Ensure console log capture is active before anything else
            MCPConsoleCommands.EnsureListening();

            // Clean up stale entries before selecting a port
            MCPInstanceRegistry.CleanupStaleEntries();

            // Resolve port: use manual override if set, otherwise auto-select
            int port;
            if (MCPSettingsManager.UseManualPort)
            {
                port = MCPSettingsManager.Port;
            }
            else
            {
                port = MCPInstanceRegistry.FindAvailablePort();
                if (port < 0)
                {
                    // No port available in the auto-select range -> give up cleanly.
                    // Without this guard the old retry logic would spin forever.
                    Debug.LogError(
                        $"[AB-UMCP] No available port in range {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd}. " +
                        "Close other Unity instances or set a manual port in MCP settings.");
                    return;
                }
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _isRunning = true;
                _activePort = port;

                // Update the settings so the UI reflects the actual port
                MCPSettingsManager.Port = port;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AB Unity MCP Server"
                };
                _listenerThread.Start();

                // Register in the shared instance registry
                MCPInstanceRegistry.Register(port);

                // Successful bind — clear any pending manual-port retry state.
                _manualPortRetryCount = 0;
                _manualPortRetryPending = false;

                Debug.Log($"[AB-UMCP] Server started on port {port}");
            }
            catch (Exception ex)
            {
                if (MCPSettingsManager.UseManualPort)
                {
                    // Manual port: do NOT fall back to another port — the user
                    // explicitly chose this one. The port is usually only briefly
                    // unavailable (socket release after a domain reload), so retry
                    // the SAME port a few times before giving up (issue #10).
                    if (_manualPortRetryCount < MaxManualPortRetries)
                    {
                        _manualPortRetryCount++;
                        Debug.LogWarning(
                            $"[AB-UMCP] Port {port} not yet available ({ex.Message}). " +
                            $"Retry {_manualPortRetryCount}/{MaxManualPortRetries} in {ManualPortRetryDelaySeconds:0.0}s...");
                        _manualPortRetryAt = EditorApplication.timeSinceStartup + ManualPortRetryDelaySeconds;
                        _manualPortRetryPending = true;
                    }
                    else
                    {
                        _manualPortRetryCount = 0;
                        Debug.LogError(
                            $"[AB-UMCP] Failed to start on port {port} after {MaxManualPortRetries} retries: {ex.Message}. " +
                            "Choose a different manual port in MCP settings, or switch to automatic port selection.");
                    }
                }
                else
                {
                    Debug.LogError($"[AB-UMCP] Failed to start on port {port}: {ex.Message}");

                    // Auto-port mode: fall back to another free port.
                    // Retry only if another port is actually free — the previous
                    // implementation retried whenever port < PortRangeEnd which
                    // caused an infinite loop when FindAvailablePort kept returning
                    // the same unavailable default port.
                    int nextPort = MCPInstanceRegistry.FindAvailablePort();
                    if (nextPort < 0 || nextPort == port)
                    {
                        Debug.LogError(
                            "[AB-UMCP] No alternative port available. Giving up to avoid a retry loop.");
                        return;
                    }

                    Debug.Log($"[AB-UMCP] Trying next available port {nextPort}...");
                    EditorApplication.delayCall += Start;
                }
            }
        }

        public static void Stop()
        {
            _isRunning = false;

            // Cancel any pending manual-port restart retry.
            _manualPortRetryPending = false;
            _manualPortRetryCount = 0;

            // Unregister from shared instance registry
            MCPInstanceRegistry.Unregister();

            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
            }
            catch { }
            _activePort = 0;
            Debug.Log("[AB-UMCP] Server stopped");
        }

        // ─── EditorApplication.update — processes the ticket queue on the main thread ───

        private static void OnEditorUpdate()
        {
            // 0. Manual-port restart retry (issue #10): the manual port can be
            //    briefly unbindable after a domain reload — retry on a short delay.
            if (_manualPortRetryPending && !_isRunning &&
                EditorApplication.timeSinceStartup >= _manualPortRetryAt)
            {
                _manualPortRetryPending = false;
                Start();
            }

            double now = EditorApplication.timeSinceStartup;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _requestProcessingNotBefore = now + PostReloadProcessingDelaySeconds;
                return;
            }

            if (now < _requestProcessingNotBefore)
                return;

            // Limit main-thread MCP work to one request per Editor update. Backlogged
            // clients are still served fairly by MCPRequestQueue across later frames.
            MCPRequestQueue.ProcessNextRequests(MaxRequestsPerEditorUpdate);
        }

        // ─── HTTP Listener ───

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_isRunning) { break; }
                catch (ThreadAbortException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError($"[AB-UMCP] Listener error: {ex.Message}");
                }
            }
        }

        // ─── Request Handler ───

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = request.Url.AbsolutePath.TrimStart('/');
                if (!path.StartsWith("api/"))
                {
                    SendJson(response, 404, new { error = "Not found" });
                    return;
                }

                string apiPath = path.Substring(4); // Remove "api/"
                string body = "";
                if (request.HasEntityBody)
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                        body = reader.ReadToEnd();
                }

                string agentId = request.Headers["X-Agent-Id"] ?? "anonymous";
                var requestArgs = ParseJson(body);
                AddExpectedProjectHeaders(request, requestArgs);
                requestArgs["_agentId"] = agentId;
                string requestId = request.Headers["Idempotency-Key"] ?? request.Headers["X-Request-Id"];
                if (string.IsNullOrEmpty(requestId))
                    requestId = GetArgumentString(requestArgs, "requestId");
                if (!string.IsNullOrEmpty(requestId))
                    requestArgs["_requestId"] = requestId;
                body = MiniJson.Serialize(requestArgs);
                if (TryBuildProjectMismatchResponse(apiPath, requestArgs, out var projectMismatch))
                {
                    SendJson(response, 409, projectMismatch);
                    return;
                }

                // ═══ Queue endpoints (async, non-blocking) ═══
                if (apiPath == "queue/submit")
                {
                    HandleQueueSubmit(response, agentId, body);
                    return;
                }
                if (apiPath == "queue/status")
                {
                    HandleQueueStatus(response, request, requestArgs, agentId);
                    return;
                }
                if (apiPath == "queue/cancel")
                {
                    HandleQueueCancel(response, agentId, requestArgs);
                    return;
                }
                if (apiPath == "queue/info")
                {
                    SendJson(response, 200, MCPRequestQueue.GetQueueInfo());
                    return;
                }

                // ═══ Project Context endpoints (read-only, no queue needed) ═══
                if (apiPath == "context")
                {
                    SendJson(response, 200, MCPContextManager.GetContextResponse());
                    return;
                }
                if (apiPath.StartsWith("context/"))
                {
                    string category = apiPath.Substring("context/".Length);
                    SendJson(response, 200, MCPContextManager.GetContextResponse(category));
                    return;
                }

                // ═══ Deferred paths (Unity APIs with async callbacks) ═══
                if (apiPath == "wait/editor-idle")
                {
                    var result = MCPRequestQueue.ExecuteResumableEditorIdleWait(agentId, ParseJson(body));
                    SendJson(response, 200, result);
                    return;
                }
                if (_deferredRoutes.TryGetValue(apiPath, out var deferredHandler))
                {
                    var result = MCPRequestQueue.ExecuteDeferredWithTracking(agentId, apiPath,
                        (resolve, progress) => deferredHandler(ParseJson(body), resolve, progress));
                    SendJson(response, 200, result);
                    return;
                }

                // ═══ Legacy synchronous path (blocks until main thread processes) ═══
                {
                    string requestKey = BuildRequestKey(agentId, apiPath, request, requestArgs);
                    var result = MCPRequestQueue.ExecutePersistentWithTracking(agentId, apiPath,
                        request.HttpMethod, body, requestKey);
                    SendJson(response, 200, result);
                }
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        // ─── Queue Submit (async) ───

        private static void HandleQueueSubmit(HttpListenerResponse response, string agentId, string body)
        {
            try
            {
                var args = ParseJson(body);
                string apiPath = args.ContainsKey("apiPath") ? args["apiPath"].ToString() : "";
                string innerBody = args.ContainsKey("body") ? args["body"].ToString() : "";

                if (string.IsNullOrEmpty(apiPath))
                {
                    SendJson(response, 400, new { error = "Missing 'apiPath' in request body" });
                    return;
                }

                var innerArgs = ParseJson(innerBody);
                innerArgs["_agentId"] = agentId;
                CopyArgumentIfMissing(args, innerArgs, "expectedProjectPath");
                CopyArgumentIfMissing(args, innerArgs, "expectedProjectName");
                innerBody = MiniJson.Serialize(innerArgs);
                if (TryBuildProjectMismatchResponse(apiPath, innerArgs, out var projectMismatch))
                {
                    SendJson(response, 409, projectMismatch);
                    return;
                }

                // Override agentId if provided in the body
                if (args.ContainsKey("agentId") && !string.IsNullOrEmpty(args["agentId"]?.ToString()))
                    agentId = args["agentId"].ToString();
                innerArgs["_agentId"] = agentId;
                innerBody = MiniJson.Serialize(innerArgs);
                string requestId = GetArgumentString(args, "requestId");
                if (string.IsNullOrEmpty(requestId))
                    requestId = GetArgumentString(innerArgs, "requestId");
                if (!string.IsNullOrEmpty(requestId))
                {
                    innerArgs["_requestId"] = requestId;
                    innerBody = MiniJson.Serialize(innerArgs);
                }
                string requestKey = string.IsNullOrEmpty(requestId)
                    ? null
                    : agentId + "|" + apiPath + "|" + requestId;

                MCPRequestQueue.RequestTicket ticket;
                bool reused = false;
                if (apiPath == "wait/editor-idle")
                {
                    ticket = MCPRequestQueue.SubmitResumableEditorIdleWait(agentId, innerArgs, out reused);
                }
                else if (_deferredRoutes.TryGetValue(apiPath, out var deferredHandler))
                {
                    ticket = MCPRequestQueue.SubmitPersistentDeferredRequest(agentId, apiPath,
                        (resolve, progress) => deferredHandler(ParseJson(innerBody), resolve, progress),
                        innerBody, requestKey, out reused);
                }
                else
                {
                    ticket = MCPRequestQueue.SubmitPersistentRequest(agentId, apiPath, "POST", innerBody,
                        requestKey, out reused);
                }

                // Return immediately with ticket info
                SendJson(response, 202, new Dictionary<string, object>
                {
                    { "ticketId",      ticket.TicketId },
                    { "status",        ticket.Status.ToString() },
                    { "queuePosition", ticket.QueuePosition },
                    { "agentId",       agentId },
                    { "reused",        reused },
                });
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = $"Queue submit failed: {ex.Message}" });
            }
        }

        // ─── Queue Status (polling) ───

        private static void HandleQueueStatus(HttpListenerResponse response, HttpListenerRequest request,
            Dictionary<string, object> args, string agentId)
        {
            string ticketIdStr = request.QueryString["ticketId"];
            if (string.IsNullOrEmpty(ticketIdStr))
                ticketIdStr = GetArgumentString(args, "ticketId");
            if (string.IsNullOrEmpty(ticketIdStr) || !long.TryParse(ticketIdStr, out long ticketId))
            {
                SendJson(response, 400, new { error = "Missing or invalid 'ticketId' query parameter" });
                return;
            }

            var status = MCPRequestQueue.GetTicketStatus(ticketId, agentId, true);
            if (status == null)
            {
                SendJson(response, 404, new { error = $"Ticket {ticketId} not found or expired" });
                return;
            }

            SendJson(response, 200, status);
        }

        private static void HandleQueueCancel(HttpListenerResponse response, string agentId,
            Dictionary<string, object> args)
        {
            if (args == null || !args.TryGetValue("ticketId", out object value) || value == null ||
                !long.TryParse(value.ToString(), out long ticketId))
            {
                SendJson(response, 400, MCPResponse.Error("ticketId is required.", "invalid_arguments"));
                return;
            }
            object result = MCPRequestQueue.CancelTicket(ticketId, agentId);
            SendJson(response, MCPResponse.TryGetError(result, out _, out _, out _) ? 409 : 200, result);
        }

        private static string BuildRequestKey(string agentId, string apiPath, HttpListenerRequest request,
            Dictionary<string, object> args)
        {
            string requestId = request?.Headers["Idempotency-Key"] ?? request?.Headers["X-Request-Id"];
            if (string.IsNullOrEmpty(requestId))
                requestId = GetArgumentString(args, "requestId");
            return string.IsNullOrEmpty(requestId) ? null : agentId + "|" + apiPath + "|" + requestId;
        }

        // ─── Route Request (runs on main thread) ───

        private static string ExtractCategory(string path)
        {
            int slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        private static object ExecuteAdvancedRoute(Dictionary<string, object> args, string outerMethod)
        {
            string route = GetArgumentString(args, "route");
            if (string.IsNullOrEmpty(route))
                route = GetArgumentString(args, "path");
            if (string.IsNullOrEmpty(route))
                return new { error = "route is required" };

            route = route.Trim('/');
            if (route == "advanced/execute")
                return new { error = "advanced/execute cannot call itself" };

            string nestedMethod = GetArgumentString(args, "method");
            if (string.IsNullOrEmpty(nestedMethod))
                nestedMethod = string.IsNullOrEmpty(outerMethod) ? "POST" : outerMethod;

            string nestedBody = GetArgumentString(args, "body");
            if (string.IsNullOrEmpty(nestedBody))
            {
                var nestedArgs = GetArgumentDictionary(args, "args")
                                 ?? GetArgumentDictionary(args, "arguments")
                                 ?? GetArgumentDictionary(args, "parameters")
                                 ?? new Dictionary<string, object>();
                nestedBody = MiniJson.Serialize(nestedArgs);
            }

            return RouteRequest(route, nestedMethod, nestedBody);
        }

        private static void ExecuteAdvancedRouteDeferred(Dictionary<string, object> args, Action<object> resolve)
        {
            string route = GetArgumentString(args, "route");
            if (string.IsNullOrEmpty(route))
                route = GetArgumentString(args, "path");
            if (string.IsNullOrEmpty(route))
            {
                resolve(new { error = "route is required" });
                return;
            }

            route = route.Trim('/');
            if (route == "advanced/execute")
            {
                resolve(new { error = "advanced/execute cannot call itself" });
                return;
            }

            string nestedBody = GetArgumentString(args, "body");
            var nestedArgs = GetArgumentDictionary(args, "args")
                             ?? GetArgumentDictionary(args, "arguments")
                             ?? GetArgumentDictionary(args, "parameters")
                             ?? new Dictionary<string, object>();
            if (string.IsNullOrEmpty(nestedBody))
                nestedBody = MiniJson.Serialize(nestedArgs);

            if (TryBuildProjectMismatchResponse(route, ParseJson(nestedBody), out var projectMismatch))
            {
                resolve(projectMismatch);
                return;
            }

            if (_deferredRoutes.TryGetValue(route, out var deferredHandler))
            {
                deferredHandler(ParseJson(nestedBody), resolve, _ => { });
                return;
            }

            resolve(ExecuteAdvancedRoute(args, "POST"));
        }

        private static string GetArgumentString(Dictionary<string, object> args, string key)
        {
            if (args == null || args.TryGetValue(key, out var value) == false || value == null)
                return "";
            return value.ToString();
        }

        private static Dictionary<string, object> GetArgumentDictionary(Dictionary<string, object> args, string key)
        {
            if (args == null || args.TryGetValue(key, out var value) == false || value == null)
                return null;
            return value as Dictionary<string, object>;
        }

        private static void CopyArgumentIfMissing(Dictionary<string, object> source,
            Dictionary<string, object> destination, string key)
        {
            if (source == null || destination == null || destination.ContainsKey(key) ||
                !source.TryGetValue(key, out object value) || value == null)
                return;
            destination[key] = value;
        }

        private static void AddExpectedProjectHeaders(HttpListenerRequest request, Dictionary<string, object> args)
        {
            if (request == null || args == null)
                return;

            if (args.ContainsKey("expectedProjectPath") == false)
            {
                string expectedProjectPath =
                    request.Headers["X-UnityMCP-Expected-Project-Path"] ??
                    request.Headers["X-Unity-Project-Path"] ??
                    request.Headers["X-Unity-Project-Root"];

                if (!string.IsNullOrEmpty(expectedProjectPath))
                    args["expectedProjectPath"] = expectedProjectPath;
            }

            if (args.ContainsKey("expectedProjectName") == false)
            {
                string expectedProjectName =
                    request.Headers["X-UnityMCP-Expected-Project-Name"] ??
                    request.Headers["X-Unity-Project-Name"];

                if (!string.IsNullOrEmpty(expectedProjectName))
                    args["expectedProjectName"] = expectedProjectName;
            }
        }

        private static bool TryBuildProjectMismatchResponse(string route, Dictionary<string, object> args,
            out object response)
        {
            response = null;
            if (ShouldSkipProjectValidation(route))
                return false;

            string expectedProjectPath = MCPInstanceCommands.GetExpectedProjectPath(args);
            string expectedProjectName = GetArgumentString(args, "expectedProjectName");
            if (string.IsNullOrEmpty(expectedProjectName))
                expectedProjectName = GetArgumentString(args, "targetProjectName");
            if (string.IsNullOrEmpty(expectedProjectName))
                expectedProjectName = GetArgumentString(args, "unityProjectName");

            if (string.IsNullOrEmpty(expectedProjectPath) && string.IsNullOrEmpty(expectedProjectName))
            {
                if (!MCPToolMetadata.RouteRequiresTargetBinding(route))
                    return false;

                response = new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "target_project_required" },
                    { "message", "Mutating requests must bind to a Unity project by expectedProjectPath or expectedProjectName." },
                    { "route", route },
                    { "actualProjectPath", MCPInstanceRegistry.CurrentProjectPath },
                    { "actualProjectName", MCPInstanceRegistry.CurrentProjectName },
                    { "actualPort", ActivePort },
                    { "currentInstance", MCPInstanceRegistry.GetCurrentInstanceInfo() }
                };
                return true;
            }

            response = MCPInstanceCommands.BuildProjectMismatch(expectedProjectPath, expectedProjectName, route);
            return response != null;
        }

        private static bool ShouldSkipProjectValidation(string route)
        {
            if (string.IsNullOrEmpty(route))
                return true;

            route = route.Trim('/');
            if (route.StartsWith("_meta/", StringComparison.Ordinal))
                return true;

            switch (route)
            {
                case "ping":
                case "queue/status":
                case "queue/info":
                case "queue/cancel":
                case "instance/current":
                case "instance/list":
                case "instance/resolve":
                case "instance/assert-project":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Route API requests to the appropriate handler.
        /// NOTE: This entire method runs on the main thread (dispatched by HandleRequest
        /// or by MCPRequestQueue.ProcessNextRequests), so all Unity APIs work correctly.
        /// </summary>
        private static object RouteRequest(string path, string method, string body)
        {
            // ─── Meta endpoints (no category check) ───
            if (path == "_meta/routes")
            {
                return MCPToolMetadata.GetRegisteredRoutes();
            }
            if (path == "_meta/tools")
            {
                var args = ParseJson(body);
                bool firstClassOnly = !args.TryGetValue("firstClassOnly", out var value) ||
                                      value == null || Convert.ToBoolean(value);
                bool compact = !args.TryGetValue("compact", out value) ||
                               value == null || Convert.ToBoolean(value);
                bool includeSchema = args.TryGetValue("includeSchema", out value) &&
                                     value != null && Convert.ToBoolean(value);
                bool includeCollections = args.TryGetValue("includeCollections", out value) &&
                                          value != null && Convert.ToBoolean(value);
                int offset = args.TryGetValue("offset", out value) && value != null
                    ? Convert.ToInt32(value)
                    : 0;
                int limit = args.TryGetValue("limit", out value) && value != null
                    ? Convert.ToInt32(value)
                    : 50;
                string metadataCategory = args.TryGetValue("category", out value) ? value?.ToString() : null;
                return MCPToolMetadata.GetRegisteredTools(firstClassOnly, compact, includeSchema,
                    offset, limit, metadataCategory, includeCollections);
            }
            if (path == "_meta/capabilities")
            {
                return MCPCapabilityRegistry.GetCapabilities();
            }

            if (TryBuildProjectMismatchResponse(path, ParseJson(body), out var projectMismatch))
            {
                return projectMismatch;
            }

            // Check if category is enabled
            string category = ExtractCategory(path);
            if (category != "ping" && category != "agents" && category != "queue"
                && !MCPSettingsManager.IsCategoryEnabled(category))
            {
                return new { error = $"Category '{category}' is currently disabled. Enable it in Window > AB Unity MCP." };
            }

            if (MCPProjectToolCommands.TryExecuteDirectRoute(path, ParseJson(body), out var projectToolResult))
            {
                return projectToolResult;
            }

            if (!MCPRouteRegistry.ContainsBuiltInRoute(path))
            {
                return MCPResponse.Error($"Unknown MCP route '{path}'.", "unknown_route");
            }

            switch (path)
            {
                // ─── Ping ───
                case "ping":
                    return new
                    {
                        status = "ok",
                        unityVersion = Application.unityVersion,
                        projectName = Application.productName,
                        projectPath = GetProjectPath(),
                        platform = Application.platform.ToString(),
                        isClone = MCPInstanceRegistry.IsParrelSyncClone(),
                        cloneIndex = MCPInstanceRegistry.GetParrelSyncCloneIndex(),
                        processId = System.Diagnostics.Process.GetCurrentProcess().Id
                    };

                // ─── Instance Routing ───
                case "instance/current":
                    return MCPInstanceCommands.Current(ParseJson(body));
                case "instance/list":
                    return MCPInstanceCommands.List(ParseJson(body));
                case "instance/resolve":
                    return MCPInstanceCommands.Resolve(ParseJson(body));
                case "instance/assert-project":
                    return MCPInstanceCommands.AssertProject(ParseJson(body));
                case "mcp/health":
                    return MCPHealthCommands.GetHealth(ParseJson(body));
                case "mcp/set-autostart":
                    return MCPHealthCommands.SetServerAutoStart(ParseJson(body));
                case "jobs/list":
                    return MCPJobHistory.List(ParseJson(body));
                case "jobs/get":
                    return MCPJobHistory.Get(ParseJson(body));

                // ─── Stable Generic Route ───
                case "advanced/execute":
                    return ExecuteAdvancedRoute(ParseJson(body), method);

                // ─── Editor State ───
                case "editor/state":
                    return MCPEditorCommands.GetEditorState();
                case "wait/editor-idle":
                    return new { error = "wait/editor-idle must be executed through the deferred route." };
                case "uitoolkit/wait-refresh":
                    return new { error = "uitoolkit/wait-refresh must be executed through the deferred route." };
                case "editor/play-mode":
                    return MCPEditorCommands.SetPlayMode(ParseJson(body));
                case "editor/execute-menu-item":
                    return MCPEditorCommands.ExecuteMenuItem(ParseJson(body));
                case "editor/execute-code":
                    return MCPEditorCommands.ExecuteCode(ParseJson(body));

                // ─── Scene ───
                case "scene/info":
                    return MCPSceneCommands.GetSceneInfo();
                case "scene/open":
                    return MCPSceneCommands.OpenScene(ParseJson(body));
                case "scene/save":
                    return MCPSceneCommands.SaveScene();
                case "scene/new":
                    return MCPSceneCommands.NewScene();
                case "scene/hierarchy":
                    return MCPSceneCommands.GetHierarchy(ParseJson(body));
                case "scene/instantiate-prefab":
                    return MCPAssetCommands.InstantiatePrefab(ParseJson(body));

                // ─── GameObject ───
                case "gameobject/create":
                    return MCPGameObjectCommands.Create(ParseJson(body));
                case "gameobject/delete":
                    return MCPGameObjectCommands.Delete(ParseJson(body));
                case "gameobject/info":
                    return MCPGameObjectCommands.GetInfo(ParseJson(body));
                case "gameobject/set-transform":
                    return MCPGameObjectCommands.SetTransform(ParseJson(body));

                // ─── Component ───
                case "component/add":
                    return MCPComponentCommands.Add(ParseJson(body));
                case "component/remove":
                    return MCPComponentCommands.Remove(ParseJson(body));
                case "component/get-properties":
                    return MCPComponentCommands.GetProperties(ParseJson(body));
                case "component/set-property":
                    return MCPComponentCommands.SetProperty(ParseJson(body));
                case "component/set-reference":
                    return MCPComponentCommands.SetReferences(ParseJson(body));
                case "component/get-referenceable":
                    return MCPComponentCommands.GetReferenceableObjects(ParseJson(body));

                // ─── SerializedObject ───
                case "serialized-object/get":
                    return MCPSerializedObjectCommands.Get(ParseJson(body));
                case "serialized-object/set":
                    return MCPSerializedObjectCommands.Set(ParseJson(body));

                // ─── Assets ───
                case "asset/list":
                    return MCPAssetCommands.List(ParseJson(body));
                case "asset/import":
                    return MCPAssetCommands.Import(ParseJson(body));
                case "asset/refresh":
                    return MCPAssetCommands.Refresh(ParseJson(body));
                case "asset/get-refresh-job":
                    return MCPAssetCommands.GetRefreshJob(ParseJson(body));
                case "asset/export-unitypackage":
                    return MCPAssetCommands.ExportUnityPackage(ParseJson(body));
                case "asset/delete":
                    return MCPAssetCommands.Delete(ParseJson(body));
                case "asset/rename":
                    return MCPAssetCommands.Rename(ParseJson(body));
                case "asset/move":
                    return MCPAssetCommands.Move(ParseJson(body));
                case "asset/create-prefab":
                    return MCPAssetCommands.CreatePrefab(ParseJson(body));
                case "asset/instantiate-prefab":
                    return MCPAssetCommands.InstantiatePrefab(ParseJson(body));
                case "asset/create-material":
                    return MCPAssetCommands.CreateMaterial(ParseJson(body));
                case "asset/create-folder":
                    return MCPAssetWorkspaceCommands.EnsureFolder(ParseJson(body));
                case "asset/copy":
                    return MCPAssetWorkspaceCommands.Copy(ParseJson(body));
                case "asset/dependencies":
                    return MCPAssetWorkspaceCommands.Dependencies(ParseJson(body));
                case "asset/transaction":
                    return MCPAssetWorkspaceCommands.Transaction(ParseJson(body));

                // ─── Scripts ───
                case "script/create":
                    return MCPScriptCommands.Create(ParseJson(body));
                case "script/read":
                    return MCPScriptCommands.Read(ParseJson(body));
                case "script/update":
                    return MCPScriptCommands.Update(ParseJson(body));

                // ─── Renderer ───
                case "renderer/set-material":
                    return MCPRendererCommands.SetMaterial(ParseJson(body));

                // ─── Build ───
                case "build/start":
                    return MCPBuildCommands.StartBuild(ParseJson(body));
                case "build/run-test":
                    return MCPBuildCommands.BuildAndRunTest(ParseJson(body));
                case "build/get-job":
                    return MCPBuildCommands.GetBuildJob(ParseJson(body));

                // ─── Console ───
                case "console/log":
                    return MCPConsoleCommands.GetLog(ParseJson(body));
                case "console/query":
                    return MCPConsoleCommands.Query(ParseJson(body));
                case "console/clear":
                    return MCPConsoleCommands.Clear();

                // ─── Script Debug Helpers ───
                case "debug/attach-unity":
                    return MCPDebugCommands.AttachUnity(ParseJson(body));
                case "debug/set-breakpoint":
                    return MCPDebugCommands.SetBreakpoint(ParseJson(body));
                case "debug/continue":
                    return MCPDebugCommands.Continue(ParseJson(body));
                case "debug/pause":
                    return MCPDebugCommands.Pause(ParseJson(body));
                case "debug/step-over":
                    return MCPDebugCommands.StepOver(ParseJson(body));
                case "debug/step-into":
                    return MCPDebugCommands.StepInto(ParseJson(body));
                case "debug/stack-trace":
                    return MCPDebugCommands.StackTrace(ParseJson(body));
                case "debug/variables":
                    return MCPDebugCommands.Variables(ParseJson(body));
                case "debug/evaluate":
                    return MCPDebugCommands.Evaluate(ParseJson(body));

                // ─── Compilation ───
                case "compilation/errors":
                    return MCPConsoleCommands.GetCompilationErrors(ParseJson(body));

                // ─── Project ───
                case "project/info":
                    return MCPProjectCommands.GetInfo();
                case "project-tools/list":
                    return MCPProjectToolCommands.List(ParseJson(body));
                case "project-tools/execute":
                    return MCPProjectToolCommands.Execute(ParseJson(body));

                // ─── Animation ───
                case "animation/create-controller":
                    return MCPAnimationCommands.CreateController(ParseJson(body));
                case "animation/controller-info":
                    return MCPAnimationCommands.GetControllerInfo(ParseJson(body));
                case "animation/add-parameter":
                    return MCPAnimationCommands.AddParameter(ParseJson(body));
                case "animation/remove-parameter":
                    return MCPAnimationCommands.RemoveParameter(ParseJson(body));
                case "animation/add-state":
                    return MCPAnimationCommands.AddState(ParseJson(body));
                case "animation/remove-state":
                    return MCPAnimationCommands.RemoveState(ParseJson(body));
                case "animation/add-transition":
                    return MCPAnimationCommands.AddTransition(ParseJson(body));
                case "animation/transition-info":
                    return MCPAnimationCommands.GetTransitionInfo(ParseJson(body));
                case "animation/update-state":
                    return MCPAnimationCommands.UpdateState(ParseJson(body));
                case "animation/update-transition":
                    return MCPAnimationCommands.UpdateTransition(ParseJson(body));
                case "animation/connect-states":
                    return MCPAnimationCommands.ConnectStates(ParseJson(body));
                case "animation/validate-controller":
                    return MCPAnimationCommands.ValidateController(ParseJson(body));
                case "animation/create-clip":
                    return MCPAnimationCommands.CreateClip(ParseJson(body));
                case "animation/clip-info":
                    return MCPAnimationCommands.GetClipInfo(ParseJson(body));
                case "animation/set-clip-curve":
                    return MCPAnimationCommands.SetClipCurve(ParseJson(body));
                case "animation/set-object-reference-curve":
                    return MCPAnimationCommands.SetObjectReferenceCurve(ParseJson(body));
                case "animation/add-layer":
                    return MCPAnimationCommands.AddLayer(ParseJson(body));
                case "animation/assign-controller":
                    return MCPAnimationCommands.AssignController(ParseJson(body));
                case "animation/get-curve-keyframes":
                    return MCPAnimationCommands.GetCurveKeyframes(ParseJson(body));
                case "animation/remove-curve":
                    return MCPAnimationCommands.RemoveCurve(ParseJson(body));
                case "animation/add-keyframe":
                    return MCPAnimationCommands.AddKeyframe(ParseJson(body));
                case "animation/remove-keyframe":
                    return MCPAnimationCommands.RemoveKeyframe(ParseJson(body));
                case "animation/add-event":
                    return MCPAnimationCommands.AddAnimationEvent(ParseJson(body));
                case "animation/remove-event":
                    return MCPAnimationCommands.RemoveAnimationEvent(ParseJson(body));
                case "animation/get-events":
                    return MCPAnimationCommands.GetAnimationEvents(ParseJson(body));
                case "animation/set-clip-settings":
                    return MCPAnimationCommands.SetClipSettings(ParseJson(body));
                case "animation/remove-transition":
                    return MCPAnimationCommands.RemoveTransition(ParseJson(body));
                case "animation/remove-layer":
                    return MCPAnimationCommands.RemoveLayer(ParseJson(body));
                case "animation/create-blend-tree":
                    return MCPAnimationCommands.CreateBlendTree(ParseJson(body));
                case "animation/get-blend-tree":
                    return MCPAnimationCommands.GetBlendTreeInfo(ParseJson(body));

                // ─── Prefab (Advanced) ───
                case "prefab/info":
                    return MCPPrefabCommands.GetPrefabInfo(ParseJson(body));
                case "prefab/create-variant":
                    return MCPPrefabCommands.CreateVariant(ParseJson(body));
                case "prefab/apply-overrides":
                    return MCPPrefabCommands.ApplyOverrides(ParseJson(body));
                case "prefab/revert-overrides":
                    return MCPPrefabCommands.RevertOverrides(ParseJson(body));
                case "prefab/unpack":
                    return MCPPrefabCommands.Unpack(ParseJson(body));
                case "prefab/set-object-reference":
                    return MCPPrefabCommands.SetObjectReference(ParseJson(body));
                case "prefab/duplicate":
                    return MCPPrefabCommands.Duplicate(ParseJson(body));
                case "prefab/set-active":
                    return MCPPrefabCommands.SetActive(ParseJson(body));
                case "prefab/reparent":
                    return MCPPrefabCommands.Reparent(ParseJson(body));

                // ─── Prefab Asset (Direct Editing) ───
                case "prefab-asset/hierarchy":
                    return MCPPrefabAssetCommands.GetHierarchy(ParseJson(body));
                case "prefab-asset/get-properties":
                    return MCPPrefabAssetCommands.GetComponentProperties(ParseJson(body));
                case "prefab-asset/set-property":
                    return MCPPrefabAssetCommands.SetComponentProperty(ParseJson(body));
                case "prefab-asset/add-component":
                    return MCPPrefabAssetCommands.AddComponent(ParseJson(body));
                case "prefab-asset/remove-component":
                    return MCPPrefabAssetCommands.RemoveComponent(ParseJson(body));
                case "prefab-asset/move-component":
                    return MCPPrefabAssetCommands.MoveComponent(ParseJson(body));
                case "prefab-asset/set-reference":
                    return MCPPrefabAssetCommands.SetReference(ParseJson(body));
                case "prefab-asset/add-gameobject":
                    return MCPPrefabAssetCommands.AddGameObject(ParseJson(body));
                case "prefab-asset/instantiate-prefab":
                case "prefab-asset/instantiate-child-prefab":
                    return MCPPrefabAssetCommands.InstantiatePrefab(ParseJson(body));
                case "prefab-asset/remove-gameobject":
                    return MCPPrefabAssetCommands.RemoveGameObject(ParseJson(body));
                case "prefab-asset/move-gameobject":
                    return MCPPrefabAssetCommands.MoveGameObject(ParseJson(body));
                case "prefab-asset/find":
                    return MCPPrefabAssetCommands.Find(ParseJson(body));
                case "prefab-asset/transaction-edit":
                    return MCPPrefabAssetCommands.TransactionEdit(ParseJson(body));
                case "prefab-asset/cleanup-missing-overrides":
                    return MCPPrefabAssetCommands.CleanupMissingVariantOverrides(ParseJson(body));

                // ─── Prefab Variant Management ───
                case "prefab-asset/variant-info":
                    return MCPPrefabAssetCommands.GetVariantInfo(ParseJson(body));
                case "prefab-asset/compare-variant":
                    return MCPPrefabAssetCommands.CompareVariantToBase(ParseJson(body));
                case "prefab-asset/apply-variant-override":
                    return MCPPrefabAssetCommands.ApplyVariantOverride(ParseJson(body));
                case "prefab-asset/revert-variant-override":
                    return MCPPrefabAssetCommands.RevertVariantOverride(ParseJson(body));
                case "prefab-asset/transfer-variant-overrides":
                    return MCPPrefabAssetCommands.TransferVariantOverrides(ParseJson(body));

                // ─── Physics ───
                case "physics/raycast":
                    return MCPPhysicsCommands.Raycast(ParseJson(body));
                case "physics/overlap-sphere":
                    return MCPPhysicsCommands.OverlapSphere(ParseJson(body));
                case "physics/overlap-box":
                    return MCPPhysicsCommands.OverlapBox(ParseJson(body));
                case "physics/collision-matrix":
                    return MCPPhysicsCommands.GetCollisionMatrix(ParseJson(body));
                case "physics/set-collision-layer":
                    return MCPPhysicsCommands.SetCollisionLayer(ParseJson(body));
                case "physics/set-gravity":
                    return MCPPhysicsCommands.SetGravity(ParseJson(body));

                // ─── Lighting ───
                case "lighting/info":
                    return MCPLightingCommands.GetLightingInfo(ParseJson(body));
                case "lighting/create":
                    return MCPLightingCommands.CreateLight(ParseJson(body));
                case "lighting/set-environment":
                    return MCPLightingCommands.SetEnvironment(ParseJson(body));
                case "lighting/create-reflection-probe":
                    return MCPLightingCommands.CreateReflectionProbe(ParseJson(body));
                case "lighting/create-light-probe-group":
                    return MCPLightingCommands.CreateLightProbeGroup(ParseJson(body));

                // ─── Audio ───
                case "audio/info":
                    return MCPAudioCommands.GetAudioInfo(ParseJson(body));
                case "audio/create-source":
                    return MCPAudioCommands.CreateAudioSource(ParseJson(body));
                case "audio/set-global":
                    return MCPAudioCommands.SetGlobalAudio(ParseJson(body));

                // ─── Tags & Layers ───
                case "taglayer/info":
                    return MCPTagLayerCommands.GetTagsAndLayers(ParseJson(body));
                case "taglayer/add-tag":
                    return MCPTagLayerCommands.AddTag(ParseJson(body));
                case "taglayer/set-tag":
                    return MCPTagLayerCommands.SetTag(ParseJson(body));
                case "taglayer/set-layer":
                    return MCPTagLayerCommands.SetLayer(ParseJson(body));
                case "taglayer/set-static":
                    return MCPTagLayerCommands.SetStatic(ParseJson(body));

                // ─── Selection & Scene View ───
                case "selection/get":
                    return MCPSelectionCommands.GetSelection(ParseJson(body));
                case "selection/set":
                    return MCPSelectionCommands.SetSelection(ParseJson(body));
                case "selection/focus-scene-view":
                    return MCPSelectionCommands.FocusSceneView(ParseJson(body));
                case "selection/find-by-type":
                    return MCPSelectionCommands.FindObjectsByType(ParseJson(body));

                // ─── Input Actions ───
                case "input/create":
                    return MCPInputCommands.CreateInputActions(ParseJson(body));
                case "input/info":
                    return MCPInputCommands.GetInputActionsInfo(ParseJson(body));
                case "input/add-map":
                    return MCPInputCommands.AddActionMap(ParseJson(body));
                case "input/remove-map":
                    return MCPInputCommands.RemoveActionMap(ParseJson(body));
                case "input/add-action":
                    return MCPInputCommands.AddAction(ParseJson(body));
                case "input/remove-action":
                    return MCPInputCommands.RemoveAction(ParseJson(body));
                case "input/add-binding":
                    return MCPInputCommands.AddBinding(ParseJson(body));
                case "input/add-composite-binding":
                    return MCPInputCommands.AddCompositeBinding(ParseJson(body));

                // ─── Assembly Definitions ───
                case "asmdef/create":
                    return MCPAssemblyDefCommands.CreateAssemblyDef(ParseJson(body));
                case "asmdef/info":
                    return MCPAssemblyDefCommands.GetAssemblyDefInfo(ParseJson(body));
                case "asmdef/list":
                    return MCPAssemblyDefCommands.ListAssemblyDefs(ParseJson(body));
                case "asmdef/add-references":
                    return MCPAssemblyDefCommands.AddReferences(ParseJson(body));
                case "asmdef/remove-references":
                    return MCPAssemblyDefCommands.RemoveReferences(ParseJson(body));
                case "asmdef/set-platforms":
                    return MCPAssemblyDefCommands.SetPlatforms(ParseJson(body));
                case "asmdef/update-settings":
                    return MCPAssemblyDefCommands.UpdateSettings(ParseJson(body));
                case "asmdef/create-ref":
                    return MCPAssemblyDefCommands.CreateAssemblyRef(ParseJson(body));

                // ─── Profiler ───
                case "profiler/enable":
                    return MCPProfilerCommands.EnableProfiler(ParseJson(body));
                case "profiler/stats":
                    return MCPProfilerCommands.GetRenderingStats(ParseJson(body));
                case "profiler/memory":
                    return MCPProfilerCommands.GetMemoryInfo(ParseJson(body));
                case "profiler/frame-data":
                    return MCPProfilerCommands.GetFrameData(ParseJson(body));
                case "profiler/analyze":
                    return MCPProfilerCommands.AnalyzePerformance(ParseJson(body));

                // ─── Frame Debugger ───
                case "debugger/enable":
                    return MCPProfilerCommands.EnableFrameDebugger(ParseJson(body));
                case "debugger/events":
                    return MCPProfilerCommands.GetFrameEvents(ParseJson(body));
                case "debugger/event-details":
                    return MCPProfilerCommands.GetFrameEventDetails(ParseJson(body));

                // ─── Memory Profiler ───
                case "profiler/memory-status":
                    return MCPMemoryProfilerCommands.GetStatus(ParseJson(body));
                case "profiler/memory-breakdown":
                    return MCPMemoryProfilerCommands.GetMemoryBreakdown(ParseJson(body));
                case "profiler/memory-top-assets":
                    return MCPMemoryProfilerCommands.GetTopMemoryConsumers(ParseJson(body));
                case "profiler/memory-snapshot":
                    return MCPMemoryProfilerCommands.TakeMemorySnapshot(ParseJson(body));

                // ─── Shader Graph ───
                case "shadergraph/status":
                    return MCPShaderGraphCommands.GetStatus(ParseJson(body));
                case "shadergraph/list-shaders":
                    return MCPShaderGraphCommands.ListShaders(ParseJson(body));
                case "shadergraph/list":
                    return MCPShaderGraphCommands.ListShaderGraphs(ParseJson(body));
                case "shadergraph/info":
                    return MCPShaderGraphCommands.GetShaderGraphInfo(ParseJson(body));
                case "shadergraph/get-properties":
                    return MCPShaderGraphCommands.GetShaderProperties(ParseJson(body));
                case "shadergraph/create":
                    return MCPShaderGraphCommands.CreateShaderGraph(ParseJson(body));
                case "shadergraph/open":
                    return MCPShaderGraphCommands.OpenShaderGraph(ParseJson(body));
                case "shadergraph/list-subgraphs":
                    return MCPShaderGraphCommands.ListSubGraphs(ParseJson(body));
                case "shadergraph/list-vfx":
                    return MCPShaderGraphCommands.ListVFXGraphs(ParseJson(body));
                case "shadergraph/open-vfx":
                    return MCPShaderGraphCommands.OpenVFXGraph(ParseJson(body));
                case "shadergraph/get-nodes":
                    return MCPShaderGraphCommands.GetGraphNodes(ParseJson(body));
                case "shadergraph/get-edges":
                    return MCPShaderGraphCommands.GetGraphEdges(ParseJson(body));
                case "shadergraph/add-node":
                    return MCPShaderGraphCommands.AddGraphNode(ParseJson(body));
                case "shadergraph/remove-node":
                    return MCPShaderGraphCommands.RemoveGraphNode(ParseJson(body));
                case "shadergraph/connect":
                    return MCPShaderGraphCommands.ConnectGraphNodes(ParseJson(body));
                case "shadergraph/disconnect":
                    return MCPShaderGraphCommands.DisconnectGraphNodes(ParseJson(body));
                case "shadergraph/set-node-property":
                    return MCPShaderGraphCommands.SetGraphNodeProperty(ParseJson(body));
                case "shadergraph/get-node-types":
                    return MCPShaderGraphCommands.GetNodeTypes(ParseJson(body));

                // ─── Amplify Shader Editor ───
                case "amplify/status":
                    return MCPAmplifyCommands.GetStatus(ParseJson(body));
                case "amplify/list":
                    return MCPAmplifyCommands.ListAmplifyShaders(ParseJson(body));
                case "amplify/info":
                    return MCPAmplifyCommands.GetAmplifyShaderInfo(ParseJson(body));
                case "amplify/open":
                    return MCPAmplifyCommands.OpenAmplifyShader(ParseJson(body));
                case "amplify/list-functions":
                    return MCPAmplifyCommands.ListAmplifyFunctions(ParseJson(body));
                case "amplify/get-node-types":
                    return MCPAmplifyCommands.GetAmplifyNodeTypes(ParseJson(body));
                case "amplify/get-nodes":
                    return MCPAmplifyCommands.GetAmplifyGraphNodes(ParseJson(body));
                case "amplify/get-connections":
                    return MCPAmplifyCommands.GetAmplifyGraphConnections(ParseJson(body));
                case "amplify/create-shader":
                    return MCPAmplifyCommands.CreateAmplifyShader(ParseJson(body));
                case "amplify/add-node":
                    return MCPAmplifyCommands.AddAmplifyNode(ParseJson(body));
                case "amplify/remove-node":
                    return MCPAmplifyCommands.RemoveAmplifyNode(ParseJson(body));
                case "amplify/connect":
                    return MCPAmplifyCommands.ConnectAmplifyNodes(ParseJson(body));
                case "amplify/disconnect":
                    return MCPAmplifyCommands.DisconnectAmplifyNodes(ParseJson(body));
                case "amplify/node-info":
                    return MCPAmplifyCommands.GetAmplifyNodeInfo(ParseJson(body));
                case "amplify/set-node-property":
                    return MCPAmplifyCommands.SetAmplifyNodeProperty(ParseJson(body));
                case "amplify/move-node":
                    return MCPAmplifyCommands.MoveAmplifyNode(ParseJson(body));
                case "amplify/save":
                    return MCPAmplifyCommands.SaveAmplifyGraph(ParseJson(body));
                case "amplify/close":
                    return MCPAmplifyCommands.CloseAmplifyEditor(ParseJson(body));
                case "amplify/create-from-template":
                    return MCPAmplifyCommands.CreateAmplifyFromTemplate(ParseJson(body));
                case "amplify/focus-node":
                    return MCPAmplifyCommands.FocusAmplifyNode(ParseJson(body));
                case "amplify/master-node-info":
                    return MCPAmplifyCommands.GetAmplifyMasterNodeInfo(ParseJson(body));
                case "amplify/disconnect-all":
                    return MCPAmplifyCommands.DisconnectAllAmplifyNode(ParseJson(body));
                case "amplify/duplicate-node":
                    return MCPAmplifyCommands.DuplicateAmplifyNode(ParseJson(body));

                // ─── Agent Management ───
                case "agents/list":
                    return MCPRequestQueue.GetActiveSessions();
                case "agents/log":
                {
                    var agentArgs = ParseJson(body);
                    string id = agentArgs.ContainsKey("agentId") ? agentArgs["agentId"].ToString() : "";
                    return new Dictionary<string, object>
                    {
                        { "agentId", id },
                        { "log", MCPRequestQueue.GetAgentLog(id) },
                    };
                }

                // ─── Search ───
                case "search/by-component":
                    return MCPSearchCommands.FindByComponent(ParseJson(body));
                case "search/by-tag":
                    return MCPSearchCommands.FindByTag(ParseJson(body));
                case "search/by-layer":
                    return MCPSearchCommands.FindByLayer(ParseJson(body));
                case "search/by-name":
                    return MCPSearchCommands.FindByName(ParseJson(body));
                case "search/by-shader":
                    return MCPSearchCommands.FindByShader(ParseJson(body));
                case "search/assets":
                    return MCPSearchCommands.SearchAssets(ParseJson(body));
                case "search/missing-references":
                    return MCPSearchCommands.FindMissingReferences(ParseJson(body));
                case "search/scene-stats":
                    return MCPSearchCommands.GetSceneStats(ParseJson(body));

                // ─── Project Settings ───
                case "settings/quality":
                    return MCPProjectSettingsCommands.GetQualitySettings(ParseJson(body));
                case "settings/quality-level":
                    return MCPProjectSettingsCommands.SetQualityLevel(ParseJson(body));
                case "settings/physics":
                    return MCPProjectSettingsCommands.GetPhysicsSettings(ParseJson(body));
                case "settings/set-physics":
                    return MCPProjectSettingsCommands.SetPhysicsSettings(ParseJson(body));
                case "settings/time":
                    return MCPProjectSettingsCommands.GetTimeSettings(ParseJson(body));
                case "settings/set-time":
                    return MCPProjectSettingsCommands.SetTimeSettings(ParseJson(body));
                case "settings/player":
                    return MCPProjectSettingsCommands.GetPlayerSettings(ParseJson(body));
                case "settings/set-player":
                    return MCPProjectSettingsCommands.SetPlayerSettings(ParseJson(body));
                case "settings/render-pipeline":
                    return MCPProjectSettingsCommands.GetRenderPipelineInfo(ParseJson(body));

                // ─── Undo ───
                case "undo/perform":
                    return MCPUndoCommands.PerformUndo(ParseJson(body));
                case "undo/redo":
                    return MCPUndoCommands.PerformRedo(ParseJson(body));
                case "undo/history":
                    return MCPUndoCommands.GetUndoHistory(ParseJson(body));
                case "undo/clear":
                    return MCPUndoCommands.ClearUndo(ParseJson(body));

                // ─── Screenshot / Scene View ───
                case "screenshot/game":
                    return MCPScreenshotCommands.CaptureGameView(ParseJson(body));
                case "screenshot/scene":
                    return MCPScreenshotCommands.CaptureSceneView(ParseJson(body));
                case "screenshot/editor-window":
                    return MCPScreenshotCommands.CaptureEditorWindow(ParseJson(body));
                case "screenshot/crop":
                    return MCPScreenshotCommands.CropImage(ParseJson(body));
                case "sceneview/info":
                    return MCPScreenshotCommands.GetSceneViewInfo(ParseJson(body));
                case "sceneview/set-camera":
                    return MCPScreenshotCommands.SetSceneViewCamera(ParseJson(body));
                case "gameview/info":
                    return MCPScreenshotCommands.GetGameViewInfo(ParseJson(body));
                case "gameview/set-resolution":
                    return MCPScreenshotCommands.SetGameViewResolution(ParseJson(body));
                case "gameview/set-scale":
                    return MCPScreenshotCommands.SetGameViewScale(ParseJson(body));
                case "gameview/set-min-scale":
                    return MCPScreenshotCommands.SetGameViewMinScale(ParseJson(body));

                // ─── Graphics & Visuals ───
                case "graphics/asset-preview":
                    return MCPGraphicsCommands.CaptureAssetPreview(ParseJson(body));
                case "graphics/scene-capture":
                    return MCPGraphicsCommands.CaptureSceneView(ParseJson(body));
                case "graphics/game-capture":
                    return MCPGraphicsCommands.CaptureGameView(ParseJson(body));
                case "graphics/prefab-render":
                    return MCPGraphicsCommands.RenderPrefabPreview(ParseJson(body));
                case "graphics/mesh-info":
                    return MCPGraphicsCommands.GetMeshInfo(ParseJson(body));
                case "graphics/material-info":
                    return MCPGraphicsCommands.GetMaterialInfo(ParseJson(body));
                case "graphics/texture-info":
                    return MCPGraphicsCommands.GetTextureInfo(ParseJson(body));
                case "graphics/image-alpha-bounds":
                    return MCPGraphicsCommands.InspectImageAlphaBounds(ParseJson(body));
                case "graphics/rect-gap":
                    return MCPGraphicsCommands.MeasureRectGap(ParseJson(body));
                case "graphics/annotate-rects":
                    return MCPGraphicsCommands.AnnotateRects(ParseJson(body));
                case "graphics/compare-images":
                    return MCPGraphicsCommands.CompareImages(ParseJson(body));
                case "graphics/renderer-info":
                    return MCPGraphicsCommands.GetRendererInfo(ParseJson(body));
                case "graphics/lighting-summary":
                    return MCPGraphicsCommands.GetLightingSummary(ParseJson(body));

                // ─── Sprite Sheet ───
                case "sprite/sheet-info":
                    return MCPSpriteSheetCommands.GetSheetInfo(ParseJson(body));
                case "sprite/pixel-check":
                    return MCPSpritePixelCommands.Check(ParseJson(body));
                case "sprite/replace-and-slice":
                    return MCPSpriteSheetCommands.ReplaceAndSlice(ParseJson(body));
                case "sprite/slice-sheet":
                    return MCPSpriteSheetCommands.SliceSheet(ParseJson(body));
                case "sprite/update-animation-clip":
                    return MCPSpriteSheetCommands.UpdateAnimationClip(ParseJson(body));
                case "sprite/replace-slice-update-clip":
                    return MCPSpriteSheetCommands.ReplaceSliceAndUpdateClip(ParseJson(body));

                // ─── Terrain ───
                case "terrain/create":
                    return MCPTerrainCommands.CreateTerrain(ParseJson(body));
                case "terrain/info":
                    return MCPTerrainCommands.GetTerrainInfo(ParseJson(body));
                case "terrain/set-height":
                    return MCPTerrainCommands.SetHeight(ParseJson(body));
                case "terrain/flatten":
                    return MCPTerrainCommands.FlattenTerrain(ParseJson(body));
                case "terrain/add-layer":
                    return MCPTerrainCommands.AddTerrainLayer(ParseJson(body));
                case "terrain/get-height":
                    return MCPTerrainCommands.GetHeightAtPosition(ParseJson(body));
                case "terrain/list":
                    return MCPTerrainCommands.ListTerrains(ParseJson(body));
                case "terrain/raise-lower":
                    return MCPTerrainCommands.RaiseLowerHeight(ParseJson(body));
                case "terrain/smooth":
                    return MCPTerrainCommands.SmoothHeight(ParseJson(body));
                case "terrain/noise":
                    return MCPTerrainCommands.SetHeightsFromNoise(ParseJson(body));
                case "terrain/set-heights-region":
                    return MCPTerrainCommands.SetHeightsRegion(ParseJson(body));
                case "terrain/get-heights-region":
                    return MCPTerrainCommands.GetHeightsRegion(ParseJson(body));
                case "terrain/remove-layer":
                    return MCPTerrainCommands.RemoveTerrainLayer(ParseJson(body));
                case "terrain/paint-layer":
                    return MCPTerrainCommands.PaintTerrainLayer(ParseJson(body));
                case "terrain/fill-layer":
                    return MCPTerrainCommands.FillTerrainLayer(ParseJson(body));
                case "terrain/add-tree-prototype":
                    return MCPTerrainCommands.AddTreePrototype(ParseJson(body));
                case "terrain/remove-tree-prototype":
                    return MCPTerrainCommands.RemoveTreePrototype(ParseJson(body));
                case "terrain/place-trees":
                    return MCPTerrainCommands.PlaceTrees(ParseJson(body));
                case "terrain/clear-trees":
                    return MCPTerrainCommands.ClearTrees(ParseJson(body));
                case "terrain/get-tree-instances":
                    return MCPTerrainCommands.GetTreeInstances(ParseJson(body));
                case "terrain/add-detail-prototype":
                    return MCPTerrainCommands.AddDetailPrototype(ParseJson(body));
                case "terrain/paint-detail":
                    return MCPTerrainCommands.PaintDetail(ParseJson(body));
                case "terrain/scatter-detail":
                    return MCPTerrainCommands.ScatterDetail(ParseJson(body));
                case "terrain/clear-detail":
                    return MCPTerrainCommands.ClearDetail(ParseJson(body));
                case "terrain/set-holes":
                    return MCPTerrainCommands.SetHoles(ParseJson(body));
                case "terrain/set-settings":
                    return MCPTerrainCommands.SetTerrainSettings(ParseJson(body));
                case "terrain/resize":
                    return MCPTerrainCommands.ResizeTerrain(ParseJson(body));
                case "terrain/create-grid":
                    return MCPTerrainCommands.CreateTerrainGrid(ParseJson(body));
                case "terrain/set-neighbors":
                    return MCPTerrainCommands.SetTerrainNeighbors(ParseJson(body));
                case "terrain/import-heightmap":
                    return MCPTerrainCommands.ImportHeightmap(ParseJson(body));
                case "terrain/export-heightmap":
                    return MCPTerrainCommands.ExportHeightmap(ParseJson(body));
                case "terrain/get-steepness":
                    return MCPTerrainCommands.GetSteepness(ParseJson(body));

                // ─── Particle System ───
                case "particle/create":
                    return MCPParticleCommands.CreateParticleSystem(ParseJson(body));
                case "particle/info":
                    return MCPParticleCommands.GetParticleSystemInfo(ParseJson(body));
                case "particle/set-main":
                    return MCPParticleCommands.SetMainModule(ParseJson(body));
                case "particle/set-emission":
                    return MCPParticleCommands.SetEmission(ParseJson(body));
                case "particle/set-shape":
                    return MCPParticleCommands.SetShape(ParseJson(body));
                case "particle/playback":
                    return MCPParticleCommands.PlaybackControl(ParseJson(body));

                // ─── ScriptableObject ───
                case "scriptableobject/create":
                    return MCPScriptableObjectCommands.CreateScriptableObject(ParseJson(body));
                case "scriptableobject/info":
                    return MCPScriptableObjectCommands.GetScriptableObjectInfo(ParseJson(body));
                case "scriptableobject/set-field":
                    return MCPScriptableObjectCommands.SetScriptableObjectField(ParseJson(body));
                case "scriptableobject/list-types":
                    return MCPScriptableObjectCommands.ListScriptableObjectTypes(ParseJson(body));

                // ─── Texture ───
                case "texture/info":
                    return MCPTextureCommands.GetTextureInfo(ParseJson(body));
                case "texture/find-duplicates":
                    return MCPImageDuplicateCommands.FindDuplicates(ParseJson(body));
                case "texture/set-import":
                    return MCPTextureCommands.SetTextureImportSettings(ParseJson(body));
                case "texture/reimport":
                    return MCPTextureCommands.ReimportTexture(ParseJson(body));
                case "texture/set-sprite":
                    return MCPTextureCommands.SetAsSprite(ParseJson(body));
                case "texture/set-normalmap":
                    return MCPTextureCommands.SetAsNormalMap(ParseJson(body));
                case "texture/apply-sprite-preset":
                    return MCPTextureCommands.ApplySpriteImportPreset(ParseJson(body));
                case "texture/import-image":
                    return MCPTextureCommands.ImportImage(ParseJson(body));
                case "texture/check-import-settings":
                    return MCPTextureCommands.CheckImportSettings(ParseJson(body));
                case "texture/check-ui-import-settings":
                    return MCPTextureCommands.CheckUIImportSettings(ParseJson(body));

                // ─── Sprite Atlas ───
                case "spriteatlas/create":
                    return MCPSpriteAtlasCommands.CreateSpriteAtlas(ParseJson(body));
                case "spriteatlas/info":
                    return MCPSpriteAtlasCommands.GetSpriteAtlasInfo(ParseJson(body));
                case "spriteatlas/add":
                    return MCPSpriteAtlasCommands.AddToSpriteAtlas(ParseJson(body));
                case "spriteatlas/remove":
                    return MCPSpriteAtlasCommands.RemoveFromSpriteAtlas(ParseJson(body));
                case "spriteatlas/settings":
                    return MCPSpriteAtlasCommands.SetSpriteAtlasSettings(ParseJson(body));
                case "spriteatlas/delete":
                    return MCPSpriteAtlasCommands.DeleteSpriteAtlas(ParseJson(body));
                case "spriteatlas/list":
                    return MCPSpriteAtlasCommands.ListSpriteAtlases(ParseJson(body));

                // ─── Navigation ───
                case "navigation/bake":
                    return MCPNavigationCommands.BakeNavMesh(ParseJson(body));
                case "navigation/clear":
                    return MCPNavigationCommands.ClearNavMesh(ParseJson(body));
                case "navigation/add-agent":
                    return MCPNavigationCommands.AddNavMeshAgent(ParseJson(body));
                case "navigation/add-obstacle":
                    return MCPNavigationCommands.AddNavMeshObstacle(ParseJson(body));
                case "navigation/info":
                    return MCPNavigationCommands.GetNavMeshInfo(ParseJson(body));
                case "navigation/set-destination":
                    return MCPNavigationCommands.SetAgentDestination(ParseJson(body));

                // ─── UI ───
                case "ui/create-canvas":
                    return MCPUICommands.CreateCanvas(ParseJson(body));
                case "ui/create-element":
                    return MCPUICommands.CreateUIElement(ParseJson(body));
                case "ui/info":
                    return MCPUICommands.GetUIInfo(ParseJson(body));
                case "ui/set-text":
                    return MCPUICommands.SetUIText(ParseJson(body));
                case "ui/set-image":
                    return MCPUICommands.SetUIImage(ParseJson(body));
                case "uitoolkit/windows":
                    return MCPUICommands.ListEditorUIWindows(ParseJson(body));
                case "uitoolkit/tree":
                    return MCPUICommands.GetEditorUITree(ParseJson(body));
                case "uitoolkit/query":
                    return MCPUICommands.QueryEditorUI(ParseJson(body));
                case "uitoolkit/style":
                    return MCPUICommands.GetEditorUIStyle(ParseJson(body));
                case "uitoolkit/repaint":
                    return MCPUICommands.RepaintEditorUI(ParseJson(body));
                case "uitoolkit/asset-inspect":
                    return MCPUICommands.InspectUIToolkitAsset(ParseJson(body));
                case "uitoolkit/runtime-documents":
                    return MCPUICommands.ListRuntimeUIDocuments(ParseJson(body));
                case "uitoolkit/runtime-tree":
                    return MCPUICommands.GetRuntimeUITree(ParseJson(body));
                case "uitoolkit/runtime-query":
                    return MCPUICommands.QueryRuntimeUI(ParseJson(body));
                case "uitoolkit/runtime-style":
                    return MCPUICommands.GetRuntimeUIStyle(ParseJson(body));
                case "uitoolkit/diagnose-runtime":
                    return MCPUICommands.DiagnoseRuntimeUI(ParseJson(body));
                case "uitoolkit/visual-check":
                    return MCPUICommands.VisualCheckRuntimeUI(ParseJson(body));
                case "uitoolkit/locate-element":
                    return MCPUICommands.LocateUIToolkitElement(ParseJson(body));
                case "uitoolkit/capture-element":
                    return MCPUICommands.CaptureUIToolkitElement(ParseJson(body));
                case "uitoolkit/compare-element":
                    return MCPUICommands.CompareUIToolkitElement(ParseJson(body));
                case "uitoolkit/generated-children":
                    return MCPUICommands.InspectUIToolkitGeneratedChildren(ParseJson(body));
                case "uitoolkit/resource-audit":
                    return MCPUICommands.AuditUIToolkitResources(ParseJson(body));
                case "uitoolkit/runtime-repaint":
                    return MCPUICommands.RepaintRuntimeUI(ParseJson(body));
                case "uitoolkit/refresh":
                    return MCPUICommands.RefreshUIToolkit(ParseJson(body));
                case "uitoolkit/assert-layout":
                    return MCPUICommands.AssertUIToolkitLayout(ParseJson(body));
                case "uitoolkit/builder-preview":
                    return MCPUICommands.OpenUIBuilderPreview(ParseJson(body));
                case "uitoolkit/edit-uxml":
                    return MCPUIAuthoringCommands.EditUxml(ParseJson(body));
                case "uitoolkit/edit-uss":
                    return MCPUIAuthoringCommands.EditUss(ParseJson(body));
                case "uitoolkit/authoring-transaction":
                    return MCPUIAuthoringCommands.AuthoringTransaction(ParseJson(body));

                // ─── Localization (optional package) ───
                case "localization/status":
                case "localization/locales":
                case "localization/create-locale":
                case "localization/set-selected-locale":
                case "localization/collections":
                case "localization/create-collection":
                case "localization/entries":
                case "localization/upsert-entry":
                case "localization/remove-entry":
                case "localization/validate":
                case "localization/settings":
                case "localization/variables":
                case "localization/upsert-variable":
                case "localization/remove-variable":
                    return MCPLocalizationBridge.Execute(path, ParseJson(body));

                // ─── Package Manager ───
                case "packages/list":
                    return MCPPackageManagerCommands.ListPackages(ParseJson(body));
                case "packages/add":
                    return MCPPackageManagerCommands.AddPackage(ParseJson(body));
                case "packages/remove":
                    return MCPPackageManagerCommands.RemovePackage(ParseJson(body));
                case "packages/search":
                    return MCPPackageManagerCommands.SearchPackage(ParseJson(body));
                case "packages/info":
                    return MCPPackageManagerCommands.GetPackageInfo(ParseJson(body));
                case "packages/status":
                    return MCPPackageManagerCommands.GetPackageStatus(ParseJson(body));
                case "packages/update-git":
                    return MCPPackageManagerCommands.UpdateGitPackage(ParseJson(body));
                case "packages/lint-metas":
                    return MCPPackageManagerCommands.LintPackageMetas(ParseJson(body));

                // ─── Constraints & LOD ───
                case "constraint/add":
                    return MCPConstraintCommands.AddConstraint(ParseJson(body));
                case "constraint/info":
                    return MCPConstraintCommands.GetConstraintInfo(ParseJson(body));
                case "lod/create":
                    return MCPConstraintCommands.CreateLODGroup(ParseJson(body));
                case "lod/info":
                    return MCPConstraintCommands.GetLODGroupInfo(ParseJson(body));

                // ─── Prefs ───
                case "editorprefs/get":
                    return MCPPrefsCommands.GetEditorPref(ParseJson(body));
                case "editorprefs/set":
                    return MCPPrefsCommands.SetEditorPref(ParseJson(body));
                case "editorprefs/delete":
                    return MCPPrefsCommands.DeleteEditorPref(ParseJson(body));
                case "playerprefs/get":
                    return MCPPrefsCommands.GetPlayerPref(ParseJson(body));
                case "playerprefs/set":
                    return MCPPrefsCommands.SetPlayerPref(ParseJson(body));
                case "playerprefs/delete":
                    return MCPPrefsCommands.DeletePlayerPref(ParseJson(body));
                case "playerprefs/delete-all":
                    return MCPPrefsCommands.DeleteAllPlayerPrefs(ParseJson(body));

                // ─── MPPM Scenario Management ───
                case "scenario/list":
                    return MCPScenarioCommands.ListScenarios(ParseJson(body));
                case "scenario/status":
                    return MCPScenarioCommands.GetScenarioStatus(ParseJson(body));
                case "scenario/activate":
                    return MCPScenarioCommands.ActivateScenario(ParseJson(body));
                case "scenario/start":
                    return MCPScenarioCommands.StartScenario(ParseJson(body));
                case "scenario/stop":
                    return MCPScenarioCommands.StopScenario(ParseJson(body));
                case "scenario/info":
                    return MCPScenarioCommands.GetMultiplayerInfo(ParseJson(body));
                case "scenario/create":
                    return MCPScenarioCommands.CreateScenario(ParseJson(body));

                // ─── MPPM Virtual Player management ───
                case "mppm/list-players":
                    return MCPScenarioCommands.MppmListPlayers(ParseJson(body));
                case "mppm/activate-player":
                    return MCPScenarioCommands.MppmActivatePlayer(ParseJson(body));
                case "mppm/deactivate-player":
                    return MCPScenarioCommands.MppmDeactivatePlayer(ParseJson(body));

#if UMA_INSTALLED
                // === UMA (Unity Multipurpose Avatar)
                case "uma/inspect-fbx":
                    return MCPUMACommands.InspectFbx(ParseJson(body));
                case "uma/create-slot":
                    return MCPUMACommands.CreateSlot(ParseJson(body));
                case "uma/create-overlay":
                    return MCPUMACommands.CreateOverlay(ParseJson(body));
                case "uma/create-wardrobe-recipe":
                    return MCPUMACommands.CreateWardrobeRecipe(ParseJson(body));
                case "uma/register-assets":
                    return MCPUMACommands.RegisterAssets(ParseJson(body));
                case "uma/list-global-library":
                    return MCPUMACommands.ListGlobalLibrary(ParseJson(body));
                case "uma/list-wardrobe-slots":
                    return MCPUMACommands.ListWardrobeSlots(ParseJson(body));
                case "uma/list-uma-materials":
                    return MCPUMACommands.ListUMAMaterials(ParseJson(body));
                case "uma/get-project-config":
                    return MCPUMACommands.GetProjectConfig(ParseJson(body));
                    case "uma/verify-recipe":
                        return MCPUMACommands.VerifyRecipe(ParseJson(body));
                    case "uma/rebuild-global-library":
                        return MCPUMACommands.RebuildGlobalLibrary(ParseJson(body));
                    case "uma/create-wardrobe-from-fbx":
                        return MCPUMACommands.CreateWardrobeFromFbx(ParseJson(body));
                    case "uma/wardrobe-equip":
                        return MCPUMACommands.WardrobeEquip(ParseJson(body));
                    case "uma/edit-race":
                        return MCPUMACommands.EditRace(ParseJson(body));
                    case "uma/create-race":
                        return MCPUMACommands.CreateRace(ParseJson(body));
                    case "uma/rename-asset":
                        return MCPUMACommands.RenameAsset(ParseJson(body));
#endif
                // ─── Testing ───
                case "testing/run-tests":
                    return MCPTestRunnerCommands.RunTests(ParseJson(body));
                case "testing/get-job":
                    return MCPTestRunnerCommands.GetTestJob(ParseJson(body));
                case "testing/run-package-tests":
                    return MCPPackageTestCommands.RunPackageTests(ParseJson(body));
                case "testing/get-package-job":
                    return MCPPackageTestCommands.GetPackageTestJob(ParseJson(body));
                // testing/list-tests is handled via the deferred path in HandleRequest

                default:
                    return new { error = $"Unknown API endpoint: {path}" };
            }
        }

        // ─── Helpers ───

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            return MiniJson.Deserialize(json) as Dictionary<string, object>
                ?? new Dictionary<string, object>();
        }

        // Response size limits (bytes) — prevents oversized payloads from crashing the MCP stdio pipe
        private const int ResponseSoftLimitBytes = 512 * 1024;
        private const int ResponseHardLimitBytes = 2 * 1024 * 1024;

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            if (statusCode >= 400 || MCPResponse.TryGetError(data, out _, out _, out _))
                data = MCPResponse.NormalizeError(data, statusCode == 408 ? "timeout" : "error", statusCode == 408);
            data = AttachInstanceContext(data);
            string json = MiniJson.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            // Size validation — protect against Write EOF on large projects
            if (buffer.Length > ResponseHardLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Response too large ({buffer.Length / (1024 * 1024)}MB), replacing with error. Use pagination parameters.");
                var errorData = MCPResponse.Error(
                    "Response exceeded size limit. Use pagination parameters (maxNodes, limit, maxResults) to request smaller chunks.",
                    "response_too_large", true, new Dictionary<string, object>
                    {
                        { "size", buffer.Length },
                        { "limit", ResponseHardLimitBytes },
                    });
                json = MiniJson.Serialize(errorData);
                buffer = Encoding.UTF8.GetBytes(json);
                response.StatusCode = 413; // Payload Too Large
            }
            else if (buffer.Length > ResponseSoftLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Large response ({buffer.Length / (1024 * 1024)}MB). Consider using pagination parameters.");
            }

            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        internal static object ExecutePersistedRoute(string path, string method, string body)
        {
            return RouteRequest(path, string.IsNullOrEmpty(method) ? "POST" : method, body ?? "");
        }

        internal static bool IsDeferredRoute(string path)
        {
            return !string.IsNullOrEmpty(path) && _deferredRoutes.ContainsKey(path);
        }

        internal static void ExecutePersistedDeferredRoute(string path, string body, Action<object> resolve,
            Action<object> progress)
        {
            if (!_deferredRoutes.TryGetValue(path, out var handler))
            {
                resolve(MCPResponse.Error($"Deferred route was not found: '{path}'.", "route_not_found"));
                return;
            }
            handler(ParseJson(body), resolve, progress);
        }

        private static object AttachInstanceContext(object data)
        {
            var dictionary = data as Dictionary<string, object>;
            if (dictionary == null)
                return data;

            if (dictionary.ContainsKey("mcpInstance") == false)
            {
                dictionary["mcpInstance"] = new Dictionary<string, object>
                {
                    { "projectPath", MCPInstanceRegistry.CurrentProjectPath },
                    { "projectName", MCPInstanceRegistry.CurrentProjectName },
                    { "port", ActivePort },
                };
            }

            return dictionary;
        }

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }

}

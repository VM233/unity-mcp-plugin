using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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

        // Legacy main-thread queue (kept for direct ExecuteOnMainThread calls)
        private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        // Routes whose Unity APIs use async callbacks (fire on next editor frame).
        // Register here instead of adding per-route if-conditions in HandleRequest/HandleQueueSubmit.
        private static readonly Dictionary<string, Action<Dictionary<string, object>, Action<object>>>
            _deferredRoutes = new Dictionary<string, Action<Dictionary<string, object>, Action<object>>>
        {
            { "testing/list-tests", MCPTestRunnerCommands.ListTests },
            { "wait/editor-idle", MCPEditorCommands.WaitForIdle },
            { "packages/update-git", MCPPackageManagerCommands.UpdateGitPackageDeferred },
        };

        // SessionState key to persist running state across domain reloads (Play Mode, recompile)
        private const string WasRunningKey = "UnityMCP_WasRunningBeforeReload";

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

        // ─── EditorApplication.update — processes both legacy queue AND ticket queue ───

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

            // 1. Process legacy main-thread actions
            ProcessMainThreadQueue();

            // 2. Process ticket-based queue (fair round-robin)
            MCPRequestQueue.ProcessNextRequests();
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

                // ═══ Queue endpoints (async, non-blocking) ═══
                if (apiPath == "queue/submit")
                {
                    HandleQueueSubmit(response, agentId, body);
                    return;
                }
                if (apiPath == "queue/status")
                {
                    HandleQueueStatus(response, request);
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
                if (_deferredRoutes.TryGetValue(apiPath, out var deferredHandler))
                {
                    var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                        () => ExecuteOnMainThreadDeferred(resolve =>
                            deferredHandler(ParseJson(body), resolve)));
                    SendJson(response, 200, result);
                    return;
                }

                // ═══ Legacy synchronous path (blocks until main thread processes) ═══
                {
                    var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                        () => ExecuteOnMainThread(() => RouteRequest(apiPath, request.HttpMethod, body)));
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

                // Override agentId if provided in the body
                if (args.ContainsKey("agentId") && !string.IsNullOrEmpty(args["agentId"]?.ToString()))
                    agentId = args["agentId"].ToString();

                MCPRequestQueue.RequestTicket ticket;
                if (_deferredRoutes.TryGetValue(apiPath, out var deferredHandler))
                {
                    ticket = MCPRequestQueue.SubmitDeferredRequest(agentId, apiPath, resolve =>
                        deferredHandler(ParseJson(innerBody), resolve));
                }
                else
                {
                    ticket = MCPRequestQueue.SubmitRequest(agentId, apiPath, () =>
                        RouteRequest(apiPath, "POST", innerBody));
                }

                // Return immediately with ticket info
                SendJson(response, 202, new Dictionary<string, object>
                {
                    { "ticketId",      ticket.TicketId },
                    { "status",        ticket.Status.ToString() },
                    { "queuePosition", ticket.QueuePosition },
                    { "agentId",       agentId },
                });
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = $"Queue submit failed: {ex.Message}" });
            }
        }

        // ─── Queue Status (polling) ───

        private static void HandleQueueStatus(HttpListenerResponse response, HttpListenerRequest request)
        {
            string ticketIdStr = request.QueryString["ticketId"];
            if (string.IsNullOrEmpty(ticketIdStr) || !long.TryParse(ticketIdStr, out long ticketId))
            {
                SendJson(response, 400, new { error = "Missing or invalid 'ticketId' query parameter" });
                return;
            }

            var status = MCPRequestQueue.GetTicketStatus(ticketId);
            if (status == null)
            {
                SendJson(response, 404, new { error = $"Ticket {ticketId} not found or expired" });
                return;
            }

            SendJson(response, 200, status);
        }

        // ─── Route Request (runs on main thread) ───

        private static string ExtractCategory(string path)
        {
            int slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        /// <summary>
        /// Returns all registered routes for dynamic tool discovery.
        /// Used by the MCP server's lazy loading system to discover tools
        /// added to the plugin without needing a server restart.
        /// </summary>
        private static object GetRegisteredRoutes()
        {
            var routes = GetRegisteredRouteList();

            // Group by category
            var grouped = new Dictionary<string, List<string>>();
            foreach (var route in routes)
            {
                string cat = ExtractCategory(route);
                if (!grouped.ContainsKey(cat)) grouped[cat] = new List<string>();
                grouped[cat].Add(route);
            }

            return new Dictionary<string, object>
            {
                { "routes", routes },
                { "categories", grouped },
                { "totalRoutes", routes.Count }
            };
        }

        private static object GetRegisteredTools()
        {
            var routes = GetRegisteredRouteList();
            var tools = routes.Select(BuildToolMetadata).ToList();

            var grouped = new Dictionary<string, List<string>>();
            foreach (var tool in tools)
            {
                string category = tool["category"].ToString();
                if (!grouped.ContainsKey(category))
                    grouped[category] = new List<string>();
                grouped[category].Add(tool["toolName"].ToString());
            }

            return new Dictionary<string, object>
            {
                { "routes", routes },
                { "tools", tools },
                { "categories", grouped },
                { "totalTools", tools.Count }
            };
        }

        private static List<string> GetRegisteredRouteList()
        {
            var routes = ExtractRouteCasesFromSource();
            return routes
                .Where(route => !string.IsNullOrEmpty(route))
                .Distinct()
                .OrderBy(route => route)
                .ToList();
        }

        private static List<string> ExtractRouteCasesFromSource()
        {
            try
            {
                foreach (string absolutePath in GetSourceCandidatePaths())
                {
                    if (!File.Exists(absolutePath))
                        continue;

                    string source = File.ReadAllText(absolutePath);
                    var routes = ExtractRouteCases(source);

                    if (routes.Count > 0)
                        return routes;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Unity MCP] Failed to extract routes from source: {ex.Message}");
            }

            return new List<string>();
        }

        private static IEnumerable<string> GetSourceCandidatePaths()
        {
            var paths = new List<string>();
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBridgeServer).Assembly);
            if (!string.IsNullOrEmpty(packageInfo?.resolvedPath))
                paths.Add(Path.Combine(packageInfo.resolvedPath, "Editor", "MCPBridgeServer.cs"));

            paths.Add(Path.Combine(projectRoot, "Packages", "com.anklebreaker.unity-mcp", "Editor", "MCPBridgeServer.cs"));

            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                paths.AddRange(Directory
                    .GetFiles(packageCacheRoot, "MCPBridgeServer.cs", SearchOption.AllDirectories)
                    .Where(path => path.Replace('\\', '/').Contains("com.anklebreaker.unity-mcp")));
            }

            foreach (string guid in AssetDatabase.FindAssets("MCPBridgeServer t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("MCPBridgeServer.cs", StringComparison.Ordinal))
                    continue;

                paths.Add(Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectRoot, path));
            }

            return paths
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct();
        }

        private static List<string> ExtractRouteCases(string source)
        {
            int methodIndex = source.LastIndexOf("private static object RouteRequest(string path", StringComparison.Ordinal);
            if (methodIndex < 0)
                return new List<string>();

            int switchIndex = source.IndexOf("switch (path)", methodIndex, StringComparison.Ordinal);
            if (switchIndex < 0)
                return new List<string>();

            int defaultIndex = source.IndexOf("default:", switchIndex, StringComparison.Ordinal);
            if (defaultIndex < 0)
                defaultIndex = source.Length;

            string switchBlock = source.Substring(switchIndex, defaultIndex - switchIndex);
            var routes = new List<string>();
            foreach (Match match in Regex.Matches(switchBlock, "case\\s+\"([^\"]+)\"\\s*:"))
            {
                routes.Add(match.Groups[1].Value);
            }

            return routes;
        }

        private static Dictionary<string, object> BuildToolMetadata(string route)
        {
            return new Dictionary<string, object>
            {
                { "route", route },
                { "toolName", RouteToToolName(route) },
                { "category", ExtractCategory(route) },
                { "description", GetToolDescription(route) },
                { "inputSchema", GetToolInputSchema(route) },
            };
        }

        private static string RouteToToolName(string route)
        {
            return "unity_" + route.Replace("/", "_").Replace("-", "_");
        }

        private static string GetToolDescription(string route)
        {
            switch (route)
            {
                case "packages/update-git":
                    return "Update a Git-based Unity package and return the resolved packages-lock hash.";
                case "packages/lint-metas":
                    return "Lint a Unity package root for missing .meta files.";
                case "wait/editor-idle":
                    return "Wait until the Unity Editor is idle after compilation, domain reload, package refresh, or asset import.";
                case "prefab-asset/instantiate-prefab":
                    return "Instantiate a prefab asset as a child inside another prefab asset.";
                case "prefab-asset/move-gameobject":
                    return "Move or reorder a GameObject inside a prefab asset.";
                case "prefab-asset/find":
                    return "Find GameObjects inside a prefab asset by name/path, component type, and serialized property value.";
                case "asset/rename":
                    return "Safely rename a Unity asset using AssetDatabase while preserving its .meta GUID.";
                case "asset/move":
                    return "Safely move a Unity asset using AssetDatabase while preserving its .meta GUID.";
                case "console/query":
                    return "Query recent Unity Console entries with time, source, message, stack, and last-Play filters.";
                case "animation/transition-info":
                    return "Read full Animator transition details including conditions, exit time, duration, and offset.";
                case "animation/update-state":
                    return "Modify an existing Animator state, including motion, speed, tag, graph position, and default state.";
                case "animation/update-transition":
                    return "Modify an existing Animator transition, including settings and condition edits.";
                case "animation/connect-states":
                    return "Create transitions between every pair of the provided Animator states.";
                case "uitoolkit/windows":
                    return "List open Unity Editor windows with UI Toolkit root metadata.";
                case "uitoolkit/tree":
                    return "Read a UI Toolkit visual tree from an EditorWindow.";
                case "uitoolkit/query":
                    return "Query UI Toolkit elements by name, className, typeName, or text.";
                case "uitoolkit/style":
                    return "Read inline and resolved style for a UI Toolkit element.";
                case "uitoolkit/repaint":
                    return "Trigger repaint on a UI Toolkit EditorWindow or element.";
                case "animation/set-object-reference-curve":
                    return "Set AnimationClip ObjectReference keyframes, such as SpriteRenderer.m_Sprite.";
                case "project-tools/list":
                    return "List project-defined MCP extension tools discovered in loaded Unity editor assemblies.";
                case "project-tools/execute":
                    return "Execute a project-defined MCP extension tool by toolName.";
                default:
                    return $"Lazy Unity route: {route}";
            }
        }

        private static Dictionary<string, object> GetToolInputSchema(string route)
        {
            switch (route)
            {
                case "packages/update-git":
                    return Schema(Props(
                        Prop("name", "string", "Package name, e.g. com.example.package"),
                        Prop("gitUrl", "string", "Optional Git URL. Defaults to the current manifest Git URL."),
                        Prop("ref", "string", "Optional branch, tag, or commit. Defaults to main."),
                        Prop("commit", "string", "Optional commit hash alias for ref."),
                        Prop("branch", "string", "Optional branch alias for ref.")
                    ), "name");
                case "packages/lint-metas":
                    return Schema(Props(
                        Prop("name", "string", "Installed package name to lint."),
                        Prop("path", "string", "Absolute or project-relative package path to lint."),
                        Prop("all", "boolean", "Lint all resolved package roots."),
                        Prop("checkDirectories", "boolean", "Also require directory .meta files. Defaults to true."),
                        Prop("maxResults", "number", "Maximum missing entries returned per package.")
                    ));
                case "wait/editor-idle":
                    return Schema(Props(
                        Prop("timeoutMs", "number", "Maximum wait time in milliseconds. Defaults to 30000."),
                        Prop("stableFrames", "number", "Number of consecutive idle editor frames required. Defaults to 3.")
                    ));
                case "prefab-asset/instantiate-prefab":
                    return Schema(Props(
                        Prop("assetPath", "string", "Target prefab asset path to edit."),
                        Prop("sourcePrefabPath", "string", "Prefab asset path to instantiate into the target prefab."),
                        Prop("parentPrefabPath", "string", "Parent path inside the target prefab. Empty means root."),
                        Prop("name", "string", "Optional name override for the created GameObject."),
                        Prop("siblingIndex", "number", "Optional sibling index under the parent."),
                        Prop("position", "object", "Optional local position object with x/y/z."),
                        Prop("rotation", "object", "Optional local Euler rotation object with x/y/z."),
                        Prop("scale", "object", "Optional local scale object with x/y/z.")
                    ), "assetPath", "sourcePrefabPath");
                case "prefab-asset/move-gameobject":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject to move inside the prefab."),
                        Prop("newParentPrefabPath", "string", "New parent path inside the prefab. Empty means root."),
                        Prop("siblingIndex", "number", "Optional sibling index under the new parent."),
                        Prop("worldPositionStays", "boolean", "Preserve world transform while reparenting. Defaults to false.")
                    ), "assetPath", "prefabPath");
                case "prefab-asset/find":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to search."),
                        Prop("name", "string", "Exact GameObject name filter."),
                        Prop("nameContains", "string", "Case-insensitive GameObject name contains filter."),
                        Prop("pathContains", "string", "Case-insensitive prefab path contains filter."),
                        Prop("componentType", "string", "Optional component type name or full name filter."),
                        Prop("propertyName", "string", "Optional serialized property name/path to require on the component."),
                        Prop("propertyValue", "string", "Optional serialized property value to match."),
                        Prop("maxResults", "number", "Maximum returned matches. Defaults to 50.")
                    ), "assetPath");
                case "asset/rename":
                    return Schema(Props(
                        Prop("path", "string", "Current asset path, e.g. Assets/Art/Old Name.png."),
                        Prop("newName", "string", "New file or folder name. Do not include a directory path.")
                    ), "path", "newName");
                case "asset/move":
                    return Schema(Props(
                        Prop("path", "string", "Current asset path."),
                        Prop("destinationPath", "string", "Destination asset path, or an existing folder path to keep the same file name.")
                    ), "path", "destinationPath");
                case "console/query":
                    return Schema(Props(
                        Prop("count", "number", "Maximum returned entries. Defaults to 50."),
                        Prop("type", "string", "Filter by all, error, warning, info, exception, or assert. Defaults to all."),
                        Prop("messageContains", "string", "Case-insensitive message substring filter."),
                        Prop("sourceContains", "string", "Case-insensitive source stack frame/path substring filter."),
                        Prop("stackContains", "string", "Case-insensitive full stack substring filter."),
                        Prop("since", "string", "Start time filter. Accepts ISO/local time, Unix seconds, or Unix milliseconds."),
                        Prop("until", "string", "End time filter. Accepts ISO/local time, Unix seconds, or Unix milliseconds."),
                        Prop("sinceSecondsAgo", "number", "Start time filter relative to now."),
                        Prop("sinceLastPlay", "boolean", "Only include entries recorded after the latest Play transition."),
                        Prop("includeStack", "boolean", "Include full stack traces. Defaults to true."),
                        Prop("newestFirst", "boolean", "Return newest entries first. Defaults to false.")
                    ));
                case "animation/transition-info":
                    return Schema(Props(
                        Prop("controllerPath", "string", "AnimatorController asset path."),
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("sourceState", "string", "Optional source state name filter."),
                        Prop("destinationState", "string", "Optional destination state, state machine, or Exit filter."),
                        Prop("fromAnyState", "boolean", "When true, only inspect Any State transitions. When false, only inspect state transitions."),
                        Prop("transitionIndex", "number", "Optional transition index under the source.")
                    ), "controllerPath");
                case "animation/update-state":
                    return Schema(Props(
                        Prop("controllerPath", "string", "AnimatorController asset path."),
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("stateName", "string", "State name to modify."),
                        Prop("newStateName", "string", "Optional new state name."),
                        Prop("motionPath", "string", "AnimationClip or Motion asset path to assign."),
                        Prop("clipPath", "string", "Alias for motionPath."),
                        Prop("clearMotion", "boolean", "Clear the state's motion."),
                        Prop("speed", "number", "State speed."),
                        Prop("tag", "string", "State tag."),
                        Prop("position", "object", "State graph position object with x/y."),
                        Prop("isDefault", "boolean", "Set this state as the layer default state."),
                        Prop("writeDefaultValues", "boolean", "State write default values flag."),
                        Prop("mirror", "boolean", "State mirror flag."),
                        Prop("iKOnFeet", "boolean", "State IK on feet flag."),
                        Prop("cycleOffset", "number", "State cycle offset.")
                    ), "controllerPath", "stateName");
                case "animation/update-transition":
                    return Schema(Props(
                        Prop("controllerPath", "string", "AnimatorController asset path."),
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("sourceState", "string", "Source state name. Required unless fromAnyState is true."),
                        Prop("destinationState", "string", "Destination state, state machine, or Exit filter."),
                        Prop("fromAnyState", "boolean", "Modify an Any State transition."),
                        Prop("transitionIndex", "number", "Optional transition index under the source."),
                        Prop("hasExitTime", "boolean", "Transition has exit time."),
                        Prop("exitTime", "number", "Transition exit time."),
                        Prop("duration", "number", "Transition duration."),
                        Prop("offset", "number", "Transition offset."),
                        Prop("hasFixedDuration", "boolean", "Use fixed duration."),
                        Prop("interruptionSource", "string", "TransitionInterruptionSource value."),
                        Prop("orderedInterruption", "boolean", "Ordered interruption flag."),
                        Prop("canTransitionToSelf", "boolean", "Any State can transition to self flag."),
                        Prop("conditions", "array", "Replace all conditions with this array."),
                        Prop("addConditions", "array", "Append conditions."),
                        Prop("updateConditions", "array", "Update conditions by index."),
                        Prop("removeConditionIndexes", "array", "Remove conditions by index.")
                    ), "controllerPath");
                case "animation/connect-states":
                    return Schema(Props(
                        Prop("controllerPath", "string", "AnimatorController asset path."),
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("stateNames", "array", "State names to connect pairwise."),
                        Prop("skipExisting", "boolean", "Skip existing transitions. Defaults to true."),
                        Prop("replaceExisting", "boolean", "Remove existing matching transitions before creating new ones."),
                        Prop("hasExitTime", "boolean", "Transition has exit time applied to created transitions."),
                        Prop("exitTime", "number", "Transition exit time applied to created transitions."),
                        Prop("duration", "number", "Transition duration applied to created transitions."),
                        Prop("offset", "number", "Transition offset applied to created transitions."),
                        Prop("hasFixedDuration", "boolean", "Fixed duration flag applied to created transitions."),
                        Prop("conditions", "array", "Conditions applied to every created transition.")
                    ), "controllerPath", "stateNames");
                case "project-tools/list":
                    return Schema(Props());
                case "project-tools/execute":
                    return Schema(Props(
                        Prop("toolName", "string", "Project tool name from project-tools/list."),
                        Prop("args", "object", "Arguments passed to the project tool as Dictionary<string, object>.")
                    ), "toolName");
                case "uitoolkit/windows":
                    return Schema(Props());
                case "uitoolkit/tree":
                    return EditorWindowSchema(Props(
                        Prop("maxDepth", "number", "Maximum tree depth. Defaults to 8."),
                        Prop("maxNodes", "number", "Maximum returned nodes. Defaults to 300."),
                        Prop("includeStyle", "boolean", "Include inline and resolved style summaries.")
                    ));
                case "uitoolkit/query":
                    return EditorWindowSchema(Props(
                        Prop("name", "string", "VisualElement.name exact match."),
                        Prop("className", "string", "USS class name exact match."),
                        Prop("typeName", "string", "VisualElement type name contains match."),
                        Prop("text", "string", "TextElement text contains match."),
                        Prop("maxResults", "number", "Maximum returned elements. Defaults to 50."),
                        Prop("includeStyle", "boolean", "Include inline and resolved style summaries.")
                    ));
                case "uitoolkit/style":
                    return EditorWindowSchema(Props(
                        Prop("path", "string", "Element path from uitoolkit/tree or uitoolkit/query."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("text", "string", "TextElement text contains match if path is omitted.")
                    ));
                case "uitoolkit/repaint":
                    return EditorWindowSchema(Props(
                        Prop("path", "string", "Optional element path from uitoolkit/tree or uitoolkit/query.")
                    ));
                default:
                    return new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>() },
                        { "additionalProperties", true }
                    };
            }
        }

        private static Dictionary<string, object> EditorWindowSchema(Dictionary<string, object> extraProps)
        {
            var props = Props(
                Prop("instanceId", "number", "EditorWindow instance id from uitoolkit/windows."),
                Prop("window", "string", "Window title, type name, full type name, or instance id."),
                Prop("windowType", "string", "EditorWindow type name or full type name."),
                Prop("title", "string", "EditorWindow title text.")
            );

            foreach (var pair in extraProps)
                props[pair.Key] = pair.Value;

            return Schema(props);
        }

        private static Dictionary<string, object> Schema(Dictionary<string, object> properties, params string[] required)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties },
            };

            if (required != null && required.Length > 0)
                schema["required"] = required.ToList();

            return schema;
        }

        private static Dictionary<string, object> Props(params KeyValuePair<string, object>[] properties)
        {
            var result = new Dictionary<string, object>();
            foreach (var pair in properties)
                result[pair.Key] = pair.Value;
            return result;
        }

        private static KeyValuePair<string, object> Prop(string name, string type, string description)
        {
            return new KeyValuePair<string, object>(name, new Dictionary<string, object>
            {
                { "type", type },
                { "description", description },
            });
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
                return GetRegisteredRoutes();
            }
            if (path == "_meta/tools")
            {
                return GetRegisteredTools();
            }

            // Check if category is enabled
            string category = ExtractCategory(path);
            if (category != "ping" && category != "agents" && category != "queue"
                && !MCPSettingsManager.IsCategoryEnabled(category))
            {
                return new { error = $"Category '{category}' is currently disabled. Enable it in Window > AB Unity MCP." };
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

                // ─── Editor State ───
                case "editor/state":
                    return MCPEditorCommands.GetEditorState();
                case "wait/editor-idle":
                    return new { error = "wait/editor-idle must be executed through the deferred route." };
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
                    return MCPComponentCommands.SetReference(ParseJson(body));
                case "component/batch-wire":
                    return MCPComponentCommands.BatchWireReferences(ParseJson(body));
                case "component/get-referenceable":
                    return MCPComponentCommands.GetReferenceableObjects(ParseJson(body));

                // ─── Assets ───
                case "asset/list":
                    return MCPAssetCommands.List(ParseJson(body));
                case "asset/import":
                    return MCPAssetCommands.Import(ParseJson(body));
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

                // ─── Console ───
                case "console/log":
                    return MCPConsoleCommands.GetLog(ParseJson(body));
                case "console/query":
                    return MCPConsoleCommands.Query(ParseJson(body));
                case "console/clear":
                    return MCPConsoleCommands.Clear();

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
                case "prefab-asset/set-reference":
                    return MCPPrefabAssetCommands.SetReference(ParseJson(body));
                case "prefab-asset/add-gameobject":
                    return MCPPrefabAssetCommands.AddGameObject(ParseJson(body));
                case "prefab-asset/instantiate-prefab":
                    return MCPPrefabAssetCommands.InstantiatePrefab(ParseJson(body));
                case "prefab-asset/remove-gameobject":
                    return MCPPrefabAssetCommands.RemoveGameObject(ParseJson(body));
                case "prefab-asset/move-gameobject":
                    return MCPPrefabAssetCommands.MoveGameObject(ParseJson(body));
                case "prefab-asset/find":
                    return MCPPrefabAssetCommands.Find(ParseJson(body));

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
                case "sceneview/info":
                    return MCPScreenshotCommands.GetSceneViewInfo(ParseJson(body));
                case "sceneview/set-camera":
                    return MCPScreenshotCommands.SetSceneViewCamera(ParseJson(body));

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
                case "graphics/renderer-info":
                    return MCPGraphicsCommands.GetRendererInfo(ParseJson(body));
                case "graphics/lighting-summary":
                    return MCPGraphicsCommands.GetLightingSummary(ParseJson(body));

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
                case "texture/set-import":
                    return MCPTextureCommands.SetTextureImportSettings(ParseJson(body));
                case "texture/reimport":
                    return MCPTextureCommands.ReimportTexture(ParseJson(body));
                case "texture/set-sprite":
                    return MCPTextureCommands.SetAsSprite(ParseJson(body));
                case "texture/set-normalmap":
                    return MCPTextureCommands.SetAsNormalMap(ParseJson(body));

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

        /// <summary>
        /// Execute a function on Unity's main thread and wait for the result.
        /// Used by the legacy synchronous path.
        /// </summary>
        private static object ExecuteOnMainThread(Func<object> action)
        {
            if (Thread.CurrentThread.ManagedThreadId == 1)
                return action();

            object result = null;
            Exception exception = null;
            var resetEvent = new ManualResetEventSlim(false);

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    try { result = action(); }
                    catch (Exception ex) { exception = ex; }
                    finally { resetEvent.Set(); }
                });
            }

            if (!resetEvent.Wait(MCPRequestQueue.SyncTimeoutMs))
                return new { error = $"Timeout waiting for Unity main thread after {MCPRequestQueue.SyncTimeoutMs / 1000}s" };

            if (exception != null)
                return new { error = exception.Message, stackTrace = exception.StackTrace };

            return result;
        }

        /// <summary>
        /// Execute an action on the main thread that completes asynchronously via callback.
        /// Unlike ExecuteOnMainThread, the calling thread blocks until the resolve callback
        /// is invoked — not when the action returns. Use for Unity APIs whose callbacks
        /// fire on a subsequent editor frame (e.g. TestRunnerApi.RetrieveTestList).
        /// </summary>
        private static object ExecuteOnMainThreadDeferred(Action<Action<object>> asyncAction)
        {
            object result = null;
            Exception exception = null;
            var resetEvent = new ManualResetEventSlim(false);

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        asyncAction(r =>
                        {
                            result = r;
                            resetEvent.Set();
                        });
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        resetEvent.Set();
                    }
                });
            }

            if (!resetEvent.Wait(MCPRequestQueue.SyncTimeoutMs))
                return new { error = $"Timeout waiting for Unity callback after {MCPRequestQueue.SyncTimeoutMs / 1000}s" };

            if (exception != null)
                return new { error = exception.Message, stackTrace = exception.StackTrace };

            return result;
        }

        private static void ProcessMainThreadQueue()
        {
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action?.Invoke(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AB-UMCP] Main thread action error: {ex}");
                    }
                }
            }
        }

        // Response size limits (bytes) — prevents oversized payloads from crashing the MCP stdio pipe
        private const int ResponseSoftLimitBytes = 8 * 1024 * 1024;  // 8 MB — log warning
        private const int ResponseHardLimitBytes = 16 * 1024 * 1024; // 16 MB — replace with error

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            string json = MiniJson.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            // Size validation — protect against Write EOF on large projects
            if (buffer.Length > ResponseHardLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Response too large ({buffer.Length / (1024 * 1024)}MB), replacing with error. Use pagination parameters.");
                var errorData = new Dictionary<string, object>
                {
                    { "error", "response_too_large" },
                    { "size", buffer.Length },
                    { "limit", ResponseHardLimitBytes },
                    { "message", "Response exceeded size limit. Use pagination parameters (maxNodes, limit, maxResults) to request smaller chunks." },
                };
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

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MCPProjectToolAttribute : Attribute
    {
        public string ToolName { get; }

        public string Description { get; set; }

        public MCPProjectToolAttribute(string toolName)
        {
            ToolName = toolName;
        }
    }

    public interface IMCPProjectTool
    {
        object Execute(Dictionary<string, object> args);
    }

    public static class MCPProjectToolCommands
    {
        public static object List(Dictionary<string, object> args)
        {
            var tools = DiscoverTools()
                .OrderBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(tool => tool.ToDictionary())
                .ToList();

            return new Dictionary<string, object>
            {
                { "tools", tools },
                { "totalTools", tools.Count }
            };
        }

        public static object Execute(Dictionary<string, object> args)
        {
            string toolName = GetString(args, "toolName");
            if (string.IsNullOrEmpty(toolName))
                toolName = GetString(args, "name");

            if (string.IsNullOrEmpty(toolName))
                return new { error = "toolName is required" };

            var matches = DiscoverTools()
                .Where(tool => string.Equals(tool.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Project tool '{toolName}' was not found." },
                    { "availableTools", DiscoverTools().Select(tool => tool.ToolName).OrderBy(name => name).ToList() }
                };
            }

            if (matches.Count > 1)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Project tool '{toolName}' is registered more than once." },
                    { "matches", matches.Select(tool => tool.ToDictionary()).ToList() }
                };
            }

            var descriptor = matches[0];
            if (!string.IsNullOrEmpty(descriptor.ValidationError))
                return new { error = descriptor.ValidationError, tool = descriptor.ToDictionary() };

            var toolArgs = GetDictionary(args, "args")
                ?? GetDictionary(args, "arguments")
                ?? GetDictionary(args, "parameters")
                ?? new Dictionary<string, object>();

            try
            {
                object result = descriptor.Invoke(toolArgs);
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "toolName", descriptor.ToolName },
                    { "result", result }
                };
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return new { error = inner.Message, stackTrace = inner.StackTrace, toolName = descriptor.ToolName };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message, stackTrace = ex.StackTrace, toolName = descriptor.ToolName };
            }
        }

        private static List<ProjectToolDescriptor> DiscoverTools()
        {
            var tools = new List<ProjectToolDescriptor>();

            foreach (var type in GetLoadableTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var attribute = method.GetCustomAttribute<MCPProjectToolAttribute>(false);
                    if (attribute == null)
                        continue;

                    tools.Add(ProjectToolDescriptor.FromMethod(attribute, method));
                }

                var classAttribute = type.GetCustomAttribute<MCPProjectToolAttribute>(false);
                if (classAttribute != null)
                    tools.Add(ProjectToolDescriptor.FromType(classAttribute, type));
            }

            return tools;
        }

        private static IEnumerable<Type> GetLoadableTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                    yield return type;
            }
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return null;

            return value.ToString();
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return null;

            return value as Dictionary<string, object>;
        }

        private sealed class ProjectToolDescriptor
        {
            public string ToolName;
            public string Description;
            public string Source;
            public string ValidationError;

            private MethodInfo method;
            private Type type;

            public static ProjectToolDescriptor FromMethod(MCPProjectToolAttribute attribute, MethodInfo method)
            {
                var descriptor = new ProjectToolDescriptor
                {
                    ToolName = attribute.ToolName,
                    Description = attribute.Description ?? "",
                    Source = method.DeclaringType.FullName + "." + method.Name,
                    method = method
                };

                descriptor.ValidationError = descriptor.ValidateMethod();
                return descriptor;
            }

            public static ProjectToolDescriptor FromType(MCPProjectToolAttribute attribute, Type type)
            {
                var descriptor = new ProjectToolDescriptor
                {
                    ToolName = attribute.ToolName,
                    Description = attribute.Description ?? "",
                    Source = type.FullName,
                    type = type
                };

                descriptor.ValidationError = descriptor.ValidateType();
                return descriptor;
            }

            public object Invoke(Dictionary<string, object> args)
            {
                if (method != null)
                {
                    var parameters = method.GetParameters();
                    object result = parameters.Length == 0
                        ? method.Invoke(null, null)
                        : method.Invoke(null, new object[] { args });

                    return method.ReturnType == typeof(void) ? "ok" : result;
                }

                var instance = Activator.CreateInstance(type) as IMCPProjectTool;
                object typeResult = instance.Execute(args);
                return typeResult;
            }

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "toolName", ToolName },
                    { "description", Description },
                    { "source", Source },
                    { "valid", string.IsNullOrEmpty(ValidationError) },
                    { "validationError", ValidationError ?? "" }
                };
            }

            private string ValidateMethod()
            {
                if (string.IsNullOrEmpty(ToolName))
                    return "MCPProjectToolAttribute toolName cannot be empty.";

                if (!method.IsStatic)
                    return $"Project tool method '{Source}' must be static.";

                var parameters = method.GetParameters();
                if (parameters.Length > 1)
                    return $"Project tool method '{Source}' must accept zero parameters or one Dictionary<string, object> parameter.";

                if (parameters.Length == 1 && parameters[0].ParameterType != typeof(Dictionary<string, object>))
                    return $"Project tool method '{Source}' parameter must be Dictionary<string, object>.";

                return null;
            }

            private string ValidateType()
            {
                if (string.IsNullOrEmpty(ToolName))
                    return "MCPProjectToolAttribute toolName cannot be empty.";

                if (!typeof(IMCPProjectTool).IsAssignableFrom(type))
                    return $"Project tool type '{Source}' must implement IMCPProjectTool.";

                if (type.IsAbstract)
                    return $"Project tool type '{Source}' cannot be abstract.";

                if (type.GetConstructor(Type.EmptyTypes) == null)
                    return $"Project tool type '{Source}' must have a public parameterless constructor.";

                return null;
            }
        }
    }
}

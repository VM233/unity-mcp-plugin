using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    internal static class MCPToolMetadata
    {
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
        public static object GetRegisteredRoutes()
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

        public static object GetRegisteredTools()
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
                case "advanced/execute":
                    return "Stable generic entrypoint that executes any Unity route by route name and arguments.";
                case "packages/lint-metas":
                    return "Lint a Unity package root for missing .meta files.";
                case "wait/editor-idle":
                    return "Wait until the Unity Editor is idle after compilation, domain reload, package refresh, or asset import.";
                case "mcp/health":
                    return "Inspect MCP bridge health, queue state, sessions, process memory, and recent slow requests.";
                case "mcp/set-autostart":
                    return "Enable or disable MCP bridge auto-start for this Unity Editor instance.";
                case "instance/current":
                    return "Return the current Unity Editor MCP instance identity, including project path and port.";
                case "instance/list":
                    return "List registered Unity Editor MCP instances across open Unity projects.";
                case "instance/resolve":
                    return "Resolve one Unity Editor MCP instance by project path, project name, or port.";
                case "instance/assert-project":
                    return "Assert that this MCP request reached the expected Unity project.";
                case "prefab-asset/instantiate-prefab":
                    return "Instantiate a prefab asset as a child inside another prefab asset.";
                case "prefab-asset/move-gameobject":
                    return "Move or reorder a GameObject inside a prefab asset.";
                case "prefab-asset/find":
                    return "Find GameObjects inside a prefab asset by name/path, component type, and serialized property value.";
                case "prefab-asset/batch-edit":
                    return "Apply multiple prefab asset edits in one transaction, save once, and return operation summaries plus prefab YAML diff.";
                case "prefab-asset/transaction-edit":
                    return "High-level prefab asset transaction edit with default summary diff for minimal-change review.";
                case "serialized-object/get":
                    return "Read serialized properties from a scene object, component, or asset via SerializedObject.";
                case "serialized-object/set":
                    return "Set one serialized property on a scene object, component, or asset via SerializedObject.";
                case "asset/rename":
                    return "Safely rename a Unity asset using AssetDatabase while preserving its .meta GUID.";
                case "asset/move":
                    return "Safely move a Unity asset using AssetDatabase while preserving its .meta GUID.";
                case "console/query":
                    return "Query recent Unity Console entries with time, source, message, stack, and last-Play filters.";
                case "debug/attach-unity":
                    return "Inspect Unity managed debugger attachment state and return MCP debug capability boundaries.";
                case "debug/set-breakpoint":
                    return "Request a managed source breakpoint. Currently reports that this requires an external debugger adapter.";
                case "debug/continue":
                    return "Continue Unity Play Mode pause state. Source breakpoint continuation requires an external debugger adapter.";
                case "debug/pause":
                    return "Pause Unity Play Mode from MCP. This is not a source-level managed debugger break.";
                case "debug/step-over":
                    return "Request managed step-over. Optionally supports one Unity frame step with stepFrame=true.";
                case "debug/step-into":
                    return "Request managed step-into. Optionally supports one Unity frame step with stepFrame=true.";
                case "debug/stack-trace":
                    return "Return the current MCP request stack trace. Paused managed frames require an external debugger adapter.";
                case "debug/variables":
                    return "Request variables for a paused managed frame. Currently reports that this requires an external debugger adapter.";
                case "debug/evaluate":
                    return "Evaluate C# code in the Unity Editor context. Paused frame evaluation requires an external debugger adapter.";
                case "animation/transition-info":
                    return "Read full Animator transition details including conditions, exit time, duration, and offset.";
                case "animation/update-state":
                    return "Modify an existing Animator state, including motion, speed, tag, graph position, and default state.";
                case "animation/update-transition":
                    return "Modify an existing Animator transition, including settings and condition edits.";
                case "animation/connect-states":
                    return "Create transitions between every pair of the provided Animator states.";
                case "animation/validate-controller":
                    return "Validate Animator parameters, states, motions, required transitions, and pairwise state connections.";
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
                case "uitoolkit/asset-inspect":
                    return "Inspect UXML and USS assets for VisualElement names, types, and default USS dimensions.";
                case "uitoolkit/runtime-documents":
                    return "List runtime UIDocuments with root visual element metadata.";
                case "uitoolkit/runtime-tree":
                    return "Read a runtime UIDocument UI Toolkit visual tree.";
                case "uitoolkit/runtime-query":
                    return "Query runtime UIDocument UI Toolkit elements by VisualElementPath, name, class, type, or text.";
                case "uitoolkit/runtime-style":
                    return "Read inline, resolved, and background style data for a runtime UI Toolkit element.";
                case "uitoolkit/diagnose-runtime":
                    return "Diagnose runtime UI Toolkit elements with VisualElementPath lookup, style, parent/children, background, and pixel-grid data.";
                case "uitoolkit/visual-check":
                    return "Run runtime UI Toolkit visual checks such as pixel-grid, background scale, and expected size.";
                case "uitoolkit/runtime-repaint":
                    return "Trigger repaint for a runtime UIDocument or one of its elements.";
                case "uitoolkit/refresh":
                    return "Refresh UI Toolkit assets and repaint runtime UIDocuments and Editor UI Toolkit windows.";
                case "uitoolkit/wait-refresh":
                    return "Refresh UI Toolkit assets, repaint panels, and wait for a few stable editor frames.";
                case "uitoolkit/assert-layout":
                    return "Assert UI Toolkit runtime layout constraints such as edge touching, containment, and size.";
                case "uitoolkit/builder-preview":
                    return "Open a UXML asset in UI Builder, wait for the preview to settle, and optionally capture the UI Builder window.";
                case "screenshot/crop":
                    return "Crop an existing screenshot or image file to a PNG.";
                case "graphics/image-alpha-bounds":
                    return "Inspect a PNG or texture asset and return alpha-based visible pixel bounds.";
                case "graphics/rect-gap":
                    return "Measure the gap or overlap between two rectangles along an edge pair.";
                case "graphics/annotate-rects":
                    return "Draw rectangle overlays on a screenshot or image file for visual verification.";
                case "sprite/sheet-info":
                    return "Inspect a sliced sprite sheet and return texture and sprite metadata.";
                case "sprite/pixel-check":
                    return "Check Sprite/Texture import settings, dimensions, pivot, border, and pixel-art suitability.";
                case "sprite/replace-and-slice":
                    return "Replace a sprite sheet image file and slice it into numbered sprites.";
                case "sprite/slice-sheet":
                    return "Slice an existing sprite sheet into numbered sprites while preserving existing sprite IDs by name.";
                case "sprite/update-animation-clip":
                    return "Update an AnimationClip SpriteRenderer.m_Sprite object-reference curve from a sprite sheet.";
                case "sprite/replace-slice-update-clip":
                    return "Replace a sprite sheet, slice it, then update an AnimationClip from the generated sprites.";
                case "texture/apply-sprite-preset":
                    return "Apply high-level TextureImporter/Sprite settings such as pixel sprite preset, PPU, pivot, border, and reference settings.";
                case "texture/import-image":
                    return "Import an external image from a URL or local path into Assets, optionally dedupe, then apply sprite import settings.";
                case "build/run-test":
                    return "Build the player, launch the built executable, sample Player.log, optionally capture its window, and terminate it.";
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
                case "advanced/execute":
                    return Schema(Props(
                        Prop("route", "string", "Unity route to execute, e.g. prefab-asset/batch-edit or project-tools/execute."),
                        Prop("method", "string", "HTTP-like method used for the nested route. Defaults to POST."),
                        Prop("args", "object", "Arguments passed to the nested route."),
                        Prop("arguments", "object", "Alias for args."),
                        Prop("parameters", "object", "Alias for args."),
                        Prop("body", "string", "Optional raw JSON body. If provided, args are ignored."),
                        Prop("expectedProjectPath", "string", "Optional safety check. The request is rejected if it reaches a different Unity project.")
                    ), "route");
                case "packages/update-git":
                    return Schema(Props(
                        Prop("name", "string", "Package name, e.g. com.example.package"),
                        Prop("gitUrl", "string", "Optional Git URL. Defaults to the current manifest Git URL."),
                        Prop("ref", "string", "Optional branch, tag, or commit. Defaults to main."),
                        Prop("commit", "string", "Optional commit hash alias for ref."),
                        Prop("branch", "string", "Optional branch alias for ref."),
                        Prop("skipIfResolved", "boolean", "Skip Package Manager resolve when packages-lock already matches the requested Git commit. Defaults to true."),
                        Prop("force", "boolean", "Force Package Manager resolve even when packages-lock already matches. Defaults to false.")
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
                        Prop("stableFrames", "number", "Number of consecutive idle editor frames required. Defaults to 3."),
                        Prop("stableMs", "number", "Minimum continuous idle time in milliseconds. Defaults to 500.")
                    ));
                case "mcp/health":
                    return Schema(Props(
                        Prop("recentCount", "number", "Number of recent MCP actions to return. Defaults to 20."),
                        Prop("slowThresholdMs", "number", "Recent actions at or above this duration are listed as slow. Defaults to 1000.")
                    ));
                case "mcp/set-autostart":
                    return Schema(Props(
                        Prop("enabled", "boolean", "Whether this Unity Editor instance should auto-start the MCP bridge after reload.")
                    ), "enabled");
                case "instance/current":
                    return Schema(Props());
                case "instance/list":
                    return Schema(Props(
                        Prop("includeStale", "boolean", "Include registry entries whose editor process may no longer be running. Defaults to false.")
                    ));
                case "instance/resolve":
                    return Schema(Props(
                        Prop("projectPath", "string", "Unity project root path to resolve. Exact normalized path match."),
                        Prop("expectedProjectPath", "string", "Alias for projectPath."),
                        Prop("targetProjectPath", "string", "Alias for projectPath."),
                        Prop("unityProjectPath", "string", "Alias for projectPath."),
                        Prop("projectName", "string", "Unity project name to resolve. Ambiguous names return an error."),
                        Prop("port", "number", "MCP bridge port to resolve.")
                    ));
                case "instance/assert-project":
                    return Schema(Props(
                        Prop("expectedProjectPath", "string", "Expected Unity project root path."),
                        Prop("targetProjectPath", "string", "Alias for expectedProjectPath."),
                        Prop("unityProjectPath", "string", "Alias for expectedProjectPath."),
                        Prop("expectedProjectName", "string", "Expected Unity project name."),
                        Prop("projectName", "string", "Alias for expectedProjectName.")
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
                case "prefab-asset/add-component":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name."),
                        Prop("waitForType", "boolean", "Wait for compilation/import until the component type is available. Defaults to true."),
                        Prop("typeResolveTimeoutMs", "number", "Maximum type wait time in milliseconds. Defaults to 30000."),
                        Prop("typeResolveStableMs", "number", "Continuous idle time after type resolution before editing. Defaults to 500."),
                        Prop("refreshAssets", "boolean", "Call AssetDatabase.Refresh once before waiting. Defaults to true."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffContextLines", "number", "Context lines around prefab YAML changes. Defaults to 2."),
                        Prop("prefabFileDiffMaxLines", "number", "Maximum diff lines returned. Defaults to 200."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: full, minimal, or summary. Defaults to full."),
                        Prop("prefabFileDiffIgnoreContains", "array", "Optional substrings used to hide noisy diff lines."),
                        Prop("prefabFileDiffIgnoreYamlProperties", "array", "Optional YAML property names used to hide noisy diff lines.")
                    ), "assetPath", "componentType");
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
                case "prefab-asset/batch-edit":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("operations", "array", "Ordered operations. Supported type values: addComponent, setProperty, setReference, addGameObject, instantiatePrefab, removeComponent, removeGameObject, moveGameObject."),
                        Prop("waitForTypes", "boolean", "Wait for all referenced component types before editing. Defaults to true."),
                        Prop("typeResolveTimeoutMs", "number", "Maximum type wait time in milliseconds. Defaults to 30000."),
                        Prop("typeResolveStableMs", "number", "Continuous idle time after type resolution before editing. Defaults to 500."),
                        Prop("refreshAssets", "boolean", "Call AssetDatabase.Refresh once before waiting. Defaults to true."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffContextLines", "number", "Context lines around prefab YAML changes. Defaults to 2."),
                        Prop("prefabFileDiffMaxLines", "number", "Maximum diff lines returned. Defaults to 200."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: full, minimal, or summary. Defaults to full."),
                        Prop("prefabFileDiffIgnoreContains", "array", "Optional substrings used to hide noisy diff lines."),
                        Prop("prefabFileDiffIgnoreYamlProperties", "array", "Optional YAML property names used to hide noisy diff lines.")
                    ), "assetPath", "operations");
                case "prefab-asset/transaction-edit":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("operations", "array", "Ordered operations. Same operation format as prefab-asset/batch-edit."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode. Defaults to summary for this high-level transaction route."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true.")
                    ), "assetPath", "operations");
                case "serialized-object/get":
                    return Schema(Props(
                        Prop("instanceId", "number", "Target Unity object instance id."),
                        Prop("assetPath", "string", "Target asset path if instanceId is omitted."),
                        Prop("assetType", "string", "Optional asset type name/full name used when loading assetPath."),
                        Prop("gameObjectPath", "string", "Scene GameObject path if instanceId and assetPath are omitted."),
                        Prop("componentType", "string", "Optional component type to select from a GameObject target."),
                        Prop("componentIndex", "number", "Component index when multiple components of the same type exist."),
                        Prop("propertyPath", "string", "Optional serialized property path to read."),
                        Prop("maxProperties", "number", "Maximum properties to return when propertyPath is omitted. Defaults to 200."),
                        Prop("includeChildren", "boolean", "Walk child properties. Defaults to true.")
                    ));
                case "serialized-object/set":
                    return Schema(Props(
                        Prop("instanceId", "number", "Target Unity object instance id."),
                        Prop("assetPath", "string", "Target asset path if instanceId is omitted."),
                        Prop("assetType", "string", "Optional asset type name/full name used when loading assetPath."),
                        Prop("gameObjectPath", "string", "Scene GameObject path if instanceId and assetPath are omitted."),
                        Prop("componentType", "string", "Optional component type to select from a GameObject target."),
                        Prop("componentIndex", "number", "Component index when multiple components of the same type exist."),
                        Prop("propertyPath", "string", "Serialized property path to write."),
                        Prop("value", "object", "Serialized value. ObjectReference supports assetPath, instanceId, or gameObject.")
                    ), "propertyPath", "value");
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
                case "debug/attach-unity":
                    return Schema(Props(
                        Prop("openWindow", "boolean", "Open Unity's Managed Debugger window. Defaults to false."),
                        Prop("waitForAttach", "boolean", "Wait briefly for an external managed debugger to attach. Defaults to false."),
                        Prop("timeoutMs", "number", "Attach wait timeout in milliseconds when waitForAttach is true. Defaults to 0.")
                    ));
                case "debug/set-breakpoint":
                    return Schema(Props(
                        Prop("file", "string", "Source file path for the requested breakpoint."),
                        Prop("line", "number", "1-based source line for the requested breakpoint.")
                    ), "file", "line");
                case "debug/continue":
                    return Schema(Props());
                case "debug/pause":
                    return Schema(Props(
                        Prop("breakPlayMode", "boolean", "Call Debug.Break when in Play Mode. Defaults to true.")
                    ));
                case "debug/step-over":
                case "debug/step-into":
                    return Schema(Props(
                        Prop("stepFrame", "boolean", "When true, perform one Unity frame step instead of source-level stepping. Defaults to false.")
                    ));
                case "debug/stack-trace":
                    return Schema(Props(
                        Prop("skipFrames", "number", "Number of MCP call frames to skip. Defaults to 0."),
                        Prop("maxFrames", "number", "Maximum stack frames to return. Defaults to 50.")
                    ));
                case "debug/variables":
                    return Schema(Props(
                        Prop("frameId", "number", "Paused debugger frame id.")
                    ), "frameId");
                case "debug/evaluate":
                    return Schema(Props(
                        Prop("expression", "string", "C# expression to evaluate in Unity Editor context. Wrapped as return <expression>; when code is omitted."),
                        Prop("code", "string", "Full C# method body for editor-context evaluation.")
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
                case "animation/validate-controller":
                    return Schema(Props(
                        Prop("controllerPath", "string", "AnimatorController asset path."),
                        Prop("path", "string", "Alias for controllerPath."),
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("requiredParameters", "array", "Strings or objects with name/parameterName and optional type/parameterType."),
                        Prop("requiredStates", "array", "State names that must exist."),
                        Prop("requireMotion", "boolean", "Require every state in the layer to have a motion."),
                        Prop("requiredTransitions", "array", "Objects with source/sourceState, destination/destinationState, and optional conditionParameter."),
                        Prop("requireFullMesh", "boolean", "Require all stateNames to have pairwise transitions."),
                        Prop("requireMutualTransitions", "boolean", "Alias for requireFullMesh."),
                        Prop("stateNames", "array", "States used by full mesh validation. Defaults to all layer states.")
                    ), "controllerPath");
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
                case "uitoolkit/asset-inspect":
                    return Schema(Props(
                        Prop("uxmlPath", "string", "UXML asset path, e.g. Assets/UI/HUD.uxml."),
                        Prop("assetPath", "string", "Alias for uxmlPath."),
                        Prop("path", "string", "Alias for uxmlPath."),
                        Prop("ussPath", "string", "Optional USS asset path. UXML Style src entries are also auto-resolved."),
                        Prop("ussPaths", "array", "Optional USS asset paths. UXML Style src entries are also auto-resolved."),
                        Prop("name", "string", "VisualElement.name exact match."),
                        Prop("names", "array", "VisualElement.name values to validate."),
                        Prop("className", "string", "USS class exact match."),
                        Prop("typeName", "string", "Expected or filtered VisualElement type name."),
                        Prop("maxResults", "number", "Maximum returned elements per query. Defaults to 100."),
                        Prop("includeUss", "boolean", "Parse USS files and attach default size declarations. Defaults to true.")
                    ));
                case "uitoolkit/runtime-documents":
                    return Schema(Props(
                        Prop("includeInactive", "boolean", "Include inactive scene UIDocuments. Defaults to true.")
                    ));
                case "uitoolkit/runtime-tree":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("maxDepth", "number", "Maximum tree depth. Defaults to 8."),
                        Prop("maxNodes", "number", "Maximum returned nodes. Defaults to 300."),
                        Prop("includeStyle", "boolean", "Include inline, resolved, and background style summaries.")
                    ));
                case "uitoolkit/runtime-query":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("path", "string", "Element tree path from runtime-tree, e.g. root/0/1."),
                        Prop("treePath", "string", "Alias for path."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names, e.g. MainMap/RightControls."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
                        Prop("names", "array", "Alias for visualElementNames."),
                        Prop("name", "string", "VisualElement.name exact match."),
                        Prop("className", "string", "USS class name exact match."),
                        Prop("typeName", "string", "VisualElement type name contains match."),
                        Prop("text", "string", "TextElement text contains match."),
                        Prop("maxResults", "number", "Maximum returned elements. Defaults to 50."),
                        Prop("includeStyle", "boolean", "Include inline, resolved, and background style summaries.")
                    ));
                case "uitoolkit/runtime-style":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("path", "string", "Element tree path from runtime-tree, e.g. root/0/1."),
                        Prop("treePath", "string", "Alias for path."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
                        Prop("names", "array", "Alias for visualElementNames."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("text", "string", "TextElement text contains match if path is omitted.")
                    ));
                case "uitoolkit/diagnose-runtime":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("queries", "array", "Optional list of element queries. Each accepts path, visualElementPath, name, className, typeName, text, and pixelScale."),
                        Prop("path", "string", "Element tree path if queries is omitted."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names if queries is omitted."),
                        Prop("visualElementNames", "array", "VisualElementPath names array if queries is omitted."),
                        Prop("name", "string", "VisualElement.name exact match if queries is omitted."),
                        Prop("pixelScale", "number", "Pixel grid scale used for pixel diagnostics. Defaults to 1.")
                    ));
                case "uitoolkit/visual-check":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("checks", "array", "Visual checks. Supported type values: pixel-grid, background-scale, size."),
                        Prop("path", "string", "Element tree path if checks is omitted."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names if checks is omitted."),
                        Prop("pixelScale", "number", "Pixel grid scale. Defaults to 1."),
                        Prop("expectedScale", "number", "Expected background image scale for background-scale checks."),
                        Prop("width", "number", "Expected element width for size checks."),
                        Prop("height", "number", "Expected element height for size checks."),
                        Prop("tolerance", "number", "Allowed pixel delta. Defaults to 0.01.")
                    ));
                case "uitoolkit/runtime-repaint":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("path", "string", "Optional element tree path from runtime-tree."),
                        Prop("visualElementPath", "string", "Optional slash-separated VisualElementPath names."),
                        Prop("visualElementNames", "array", "Optional VisualElementPath names array.")
                    ));
                case "uitoolkit/refresh":
                    return Schema(Props(
                        Prop("refreshAssets", "boolean", "Call AssetDatabase.Refresh before repainting. Defaults to true."),
                        Prop("forceSynchronousImport", "boolean", "Use ForceSynchronousImport. Defaults to true.")
                    ));
                case "uitoolkit/wait-refresh":
                    return Schema(Props(
                        Prop("refreshAssets", "boolean", "Call AssetDatabase.Refresh before repainting. Defaults to true."),
                        Prop("forceSynchronousImport", "boolean", "Use ForceSynchronousImport. Defaults to true."),
                        Prop("timeoutMs", "number", "Maximum wait time in milliseconds. Defaults to 10000."),
                        Prop("stableFrames", "number", "Consecutive idle repaint frames required. Defaults to 2.")
                    ));
                case "uitoolkit/builder-preview":
                    return Schema(Props(
                        Prop("uxmlPath", "string", "UXML asset path to open in UI Builder."),
                        Prop("assetPath", "string", "Alias for uxmlPath."),
                        Prop("path", "string", "Alias for uxmlPath."),
                        Prop("waitFrames", "number", "Editor frames to wait before capturing. Defaults to 8."),
                        Prop("capture", "boolean", "Capture the UI Builder window after opening. Defaults to true."),
                        Prop("screenshotPath", "string", "PNG path for the UI Builder screenshot."),
                        Prop("maxDimension", "number", "Maximum screenshot dimension. Defaults to 8192."),
                        Prop("zoom", "number", "Requested zoom, recorded for diagnostics. UI Builder has no stable public zoom API.")
                    ));
                case "uitoolkit/assert-layout":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("assertions", "array", "Layout assertions. Supported types: edge-touch, inside, size.")
                    ), "assertions");
                case "screenshot/crop":
                    return Schema(Props(
                        Prop("sourcePath", "string", "Image path to crop. Aliases: imagePath, path."),
                        Prop("imagePath", "string", "Alias for sourcePath."),
                        Prop("path", "string", "Alias for sourcePath."),
                        Prop("rect", "object", "Crop rect with x, y, width, height."),
                        Prop("outputPath", "string", "Output PNG path. Defaults next to source with _crop suffix."),
                        Prop("originTopLeft", "boolean", "Treat rect x/y as top-left image coordinates. Defaults to true.")
                    ));
                case "graphics/image-alpha-bounds":
                    return Schema(Props(
                        Prop("assetPath", "string", "Texture2D asset path."),
                        Prop("filePath", "string", "Absolute or project-relative PNG path if assetPath is omitted."),
                        Prop("path", "string", "Alias for assetPath."),
                        Prop("alphaThreshold", "number", "Alpha threshold. 0-1 or 0-255. Defaults to 0.01.")
                    ));
                case "graphics/rect-gap":
                    return Schema(Props(
                        Prop("firstRect", "object", "First rect with x, y, width, height."),
                        Prop("secondRect", "object", "Second rect with x, y, width, height."),
                        Prop("axis", "string", "x or y. Defaults to x."),
                        Prop("firstEdge", "string", "First rect edge. Defaults to right for x, bottom for y."),
                        Prop("secondEdge", "string", "Second rect edge. Defaults to left for x, top for y."),
                        Prop("tolerance", "number", "Touch tolerance in pixels. Defaults to 0.5.")
                    ), "firstRect", "secondRect");
                case "graphics/annotate-rects":
                    return Schema(Props(
                        Prop("sourcePath", "string", "Image path to annotate. Aliases: imagePath, filePath, path."),
                        Prop("imagePath", "string", "Alias for sourcePath."),
                        Prop("filePath", "string", "Alias for sourcePath."),
                        Prop("path", "string", "Alias for sourcePath."),
                        Prop("outputPath", "string", "Output PNG path. Defaults next to source with _annotated suffix."),
                        Prop("rects", "array", "Rectangles to draw. Each has x, y, width, height, optional color and thickness."),
                        Prop("originTopLeft", "boolean", "Treat rect x/y as top-left image coordinates. Defaults to true."),
                        Prop("color", "string", "Default HTML color, e.g. #ff00ffff."),
                        Prop("thickness", "number", "Default border thickness in pixels. Defaults to 2.")
                    ), "rects");
                case "sprite/sheet-info":
                    return Schema(Props(
                        Prop("texturePath", "string", "Sprite sheet texture asset path. Aliases: assetPath, path.")
                    ));
                case "sprite/pixel-check":
                    return Schema(Props(
                        Prop("assetPath", "string", "Texture/Sprite asset path."),
                        Prop("assetPaths", "array", "Texture/Sprite asset paths."),
                        Prop("folderPath", "string", "Folder to scan recursively for Texture2D assets."),
                        Prop("dimensionsMultipleOf", "number", "Optional divisor required for texture width/height."),
                        Prop("expectedScale", "number", "Optional UI scale used to check source dimensions after scaling."),
                        Prop("tolerance", "number", "Allowed pixel delta. Defaults to 0.01."),
                        Prop("requirePointFilter", "boolean", "Warn if FilterMode is not Point. Defaults to true."),
                        Prop("requireNoCompression", "boolean", "Warn if default platform format is compressed. Defaults to true."),
                        Prop("requireNoMipMaps", "boolean", "Warn if mip maps are enabled. Defaults to true.")
                    ));
                case "sprite/replace-and-slice":
                case "sprite/slice-sheet":
                    return Schema(Props(
                        Prop("texturePath", "string", "Sprite sheet texture asset path. Aliases: assetPath, path."),
                        Prop("sourcePath", "string", "External image file to copy over texturePath. Required for replace-and-slice."),
                        Prop("frameWidth", "number", "Frame width in pixels."),
                        Prop("frameHeight", "number", "Frame height in pixels."),
                        Prop("frameCount", "number", "Optional frame count. Defaults to the full grid."),
                        Prop("baseName", "string", "Generated sprite name prefix. Defaults to texture file name."),
                        Prop("columns", "number", "Grid column count. Defaults to textureWidth / frameWidth."),
                        Prop("startX", "number", "Grid start x in pixels. Defaults to 0."),
                        Prop("startY", "number", "Grid start y in top-left pixels. Defaults to 0."),
                        Prop("pivotX", "number", "Optional normalized pivot x."),
                        Prop("pivotY", "number", "Optional normalized pivot y."),
                        Prop("preserveSpriteIDs", "boolean", "Preserve existing sprite IDs by generated name. Defaults to true.")
                    ), "texturePath", "frameWidth", "frameHeight");
                case "sprite/update-animation-clip":
                    return Schema(Props(
                        Prop("clipPath", "string", "AnimationClip asset path."),
                        Prop("texturePath", "string", "Sprite sheet texture asset path. Aliases: assetPath, path."),
                        Prop("bindingPath", "string", "Animation binding path to SpriteRenderer. Empty means the animated object itself."),
                        Prop("frameRate", "number", "Animation frame rate. Defaults to the clip frame rate or 12."),
                        Prop("spriteNames", "array", "Optional exact sprite names to use."),
                        Prop("loopTime", "boolean", "Whether the clip loops. Defaults to the current clip setting.")
                    ), "clipPath", "texturePath");
                case "sprite/replace-slice-update-clip":
                    return Schema(Props(
                        Prop("texturePath", "string", "Sprite sheet texture asset path. Aliases: assetPath, path."),
                        Prop("sourcePath", "string", "External image file to copy over texturePath."),
                        Prop("clipPath", "string", "Optional AnimationClip asset path to update after slicing."),
                        Prop("frameWidth", "number", "Frame width in pixels."),
                        Prop("frameHeight", "number", "Frame height in pixels."),
                        Prop("frameCount", "number", "Optional frame count. Defaults to the full grid."),
                        Prop("baseName", "string", "Generated sprite name prefix. Defaults to texture file name."),
                        Prop("frameRate", "number", "Animation frame rate. Defaults to the clip frame rate or 12."),
                        Prop("bindingPath", "string", "Animation binding path to SpriteRenderer.")
                    ), "texturePath", "sourcePath", "frameWidth", "frameHeight");
                case "texture/apply-sprite-preset":
                    return Schema(Props(
                        Prop("path", "string", "Texture asset path. Alias: assetPath."),
                        Prop("assetPath", "string", "Alias for path."),
                        Prop("referencePath", "string", "Optional texture asset whose importer settings are copied first."),
                        Prop("preset", "string", "High-level preset. Supported: pixel-sprite."),
                        Prop("pixelsPerUnit", "number", "Sprite pixels per unit. Alias: spritePixelsPerUnit."),
                        Prop("spritePixelsPerUnit", "number", "Sprite pixels per unit."),
                        Prop("filterMode", "string", "Texture FilterMode, e.g. Point."),
                        Prop("textureCompression", "string", "TextureImporterCompression value."),
                        Prop("defaultPlatformFormat", "string", "Default platform TextureImporterFormat, e.g. RGBA32."),
                        Prop("defaultPlatformCompression", "string", "Default platform TextureImporterCompression."),
                        Prop("readable", "boolean", "Texture is readable."),
                        Prop("mipmapEnabled", "boolean", "Generate mipmaps."),
                        Prop("alphaIsTransparency", "boolean", "Alpha is transparency."),
                        Prop("pivot", "object", "Sprite pivot with x/y."),
                        Prop("border", "object", "Sprite border. Accepts number, [left,bottom,right,top], or object with left/bottom/right/top.")
                    ), "path");
                case "texture/import-image":
                    return Schema(Props(
                        Prop("sourcePath", "string", "Local image file path."),
                        Prop("sourceUrl", "string", "Remote image URL. Alias: url."),
                        Prop("url", "string", "Alias for sourceUrl."),
                        Prop("targetPath", "string", "Target asset path inside Assets."),
                        Prop("targetFolder", "string", "Target folder used with assetName/name."),
                        Prop("assetName", "string", "Target file name used with targetFolder. Alias: name."),
                        Prop("name", "string", "Alias for assetName."),
                        Prop("overwrite", "boolean", "Overwrite targetPath if content differs. Defaults to false."),
                        Prop("dedupeByHash", "boolean", "Skip if the target folder already contains identical image bytes. Defaults to true."),
                        Prop("applySpritePreset", "boolean", "Apply sprite import settings after import. Defaults to true."),
                        Prop("preset", "string", "Preset passed to texture/apply-sprite-preset. Defaults to pixel-sprite.")
                    ));
                case "build/run-test":
                    return Schema(Props(
                        Prop("target", "string", "BuildTarget. Defaults to StandaloneWindows64."),
                        Prop("outputPath", "string", "Player output executable path."),
                        Prop("developmentBuild", "boolean", "Build with Development flag."),
                        Prop("scenes", "array", "Optional scene paths. Defaults to enabled Build Settings scenes."),
                        Prop("overwrite", "boolean", "Delete existing exe and Data folder before build. Defaults to true."),
                        Prop("run", "boolean", "Launch the built executable after a successful build. Defaults to true."),
                        Prop("runSeconds", "number", "Seconds to let the executable run before sampling/termination. Defaults to 5."),
                        Prop("terminateAfter", "boolean", "Kill the process after sampling. Defaults to true."),
                        Prop("captureWindow", "boolean", "Capture the built player's main window on Windows. Defaults to false."),
                        Prop("screenshotPath", "string", "PNG path for captureWindow output."),
                        Prop("windowWaitMs", "number", "Milliseconds to wait for the main window. Defaults to 5000."),
                        Prop("logTailLines", "number", "Player.log tail lines to return. Defaults to 120.")
                    ), "outputPath");
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

        private static Dictionary<string, object> RuntimeUIDocumentSchema(Dictionary<string, object> extraProps, params string[] required)
        {
            var props = Props(
                Prop("documentInstanceId", "number", "UIDocument instance id from uitoolkit/runtime-documents."),
                Prop("uidocumentInstanceId", "number", "Alias for documentInstanceId."),
                Prop("gameObjectPath", "string", "Scene GameObject path that owns the UIDocument."),
                Prop("gameObjectName", "string", "Scene GameObject name that owns the UIDocument."),
                Prop("documentName", "string", "UIDocument component name."),
                Prop("includeInactive", "boolean", "Include inactive scene UIDocuments. Defaults to true.")
            );

            foreach (var pair in extraProps)
                props[pair.Key] = pair.Value;

            return Schema(props, required);
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

    }
}
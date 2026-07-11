using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPToolMetadata
    {
        private static List<string> _cachedRoutes;
        private static List<Dictionary<string, object>> _cachedTools;
        private static List<Dictionary<string, object>> _cachedFirstClassTools;

        private const string ExposureFirstClass = "first-class";
        private const string ExposureFallback = "fallback";
        private const string ExposureLazy = "lazy";

        private sealed class ToolProfile
        {
            public string Exposure;
            public bool Preferred;
            public bool ReadOnly;
            public bool MutatesAssets;
            public bool Dangerous;
            public bool LongRunning;
            public bool MayReloadDomain;
            public bool RequiresPlayMode;

            public static ToolProfile FirstClass(bool readOnly = false, bool mutatesAssets = false,
                bool dangerous = false, bool longRunning = false, bool mayReloadDomain = false,
                bool requiresPlayMode = false)
            {
                return new ToolProfile
                {
                    Exposure = ExposureFirstClass,
                    Preferred = true,
                    ReadOnly = readOnly,
                    MutatesAssets = mutatesAssets,
                    Dangerous = dangerous,
                    LongRunning = longRunning,
                    MayReloadDomain = mayReloadDomain,
                    RequiresPlayMode = requiresPlayMode,
                };
            }

            public static ToolProfile Fallback()
            {
                return new ToolProfile
                {
                    Exposure = ExposureFallback,
                    Preferred = false,
                    ReadOnly = false,
                    MutatesAssets = true,
                    Dangerous = true,
                    LongRunning = false,
                    MayReloadDomain = false,
                    RequiresPlayMode = false,
                };
            }

            public static ToolProfile Lazy()
            {
                return new ToolProfile
                {
                    Exposure = ExposureLazy,
                    Preferred = false,
                    ReadOnly = false,
                    MutatesAssets = false,
                    Dangerous = false,
                    LongRunning = false,
                    MayReloadDomain = false,
                    RequiresPlayMode = false,
                };
            }

            public Dictionary<string, object> ToAnnotations()
            {
                var annotations = new Dictionary<string, object>();
                if (ReadOnly)
                {
                    annotations["readOnlyHint"] = true;
                    annotations["idempotentHint"] = true;
                }
                if (Dangerous)
                    annotations["destructiveHint"] = true;
                return annotations;
            }
        }

        private static readonly Dictionary<string, ToolProfile> ToolProfiles = BuildToolProfiles();

        private static Dictionary<string, ToolProfile> BuildToolProfiles()
        {
            var profiles = new Dictionary<string, ToolProfile>(StringComparer.Ordinal);

            AddProfile(profiles, ToolProfile.FirstClass(readOnly: true),
                "scene/hierarchy",
                "serialized-object/get",
                "prefab-asset/get-properties",
                "prefab-asset/hierarchy",
                "prefab-asset/find",
                "console/query",
                "uitoolkit/asset-inspect",
                "uitoolkit/runtime-documents",
                "uitoolkit/runtime-tree",
                "uitoolkit/runtime-query",
                "uitoolkit/runtime-style",
                "uitoolkit/locate-element",
                "uitoolkit/capture-element",
                "uitoolkit/compare-element",
                "localization/status",
                "localization/locales",
                "localization/collections",
                "localization/entries",
                "localization/validate",
                "localization/variables",
                "packages/list",
                "packages/info",
                "packages/status",
                "packages/lint-metas",
                "testing/get-job",
                "testing/get-package-job",
                "project-tools/list");

            AddProfile(profiles, ToolProfile.FirstClass(readOnly: true, longRunning: true),
                "wait/editor-idle",
                "testing/list-tests",
                "uitoolkit/wait-refresh",
                "uitoolkit/builder-preview");

            AddProfile(profiles, ToolProfile.FirstClass(readOnly: true, longRunning: true,
                    requiresPlayMode: true),
                "screenshot/game");

            AddProfile(profiles, ToolProfile.FirstClass(longRunning: true),
                "testing/run-tests");

            AddProfile(profiles, ToolProfile.FirstClass(mutatesAssets: true),
                "serialized-object/set",
                "prefab-asset/add-component",
                "prefab-asset/add-gameobject",
                "prefab-asset/instantiate-prefab",
                "prefab-asset/move-component",
                "prefab-asset/move-gameobject",
                "prefab-asset/remove-component",
                "prefab-asset/remove-gameobject",
                "prefab-asset/set-property",
                "prefab-asset/set-reference",
                "prefab-asset/transaction-edit",
                "asset/rename",
                "asset/move",
                "asset/export-unitypackage",
                "uitoolkit/runtime-repaint",
                "uitoolkit/refresh",
                "uitoolkit/assert-layout",
                "localization/create-locale",
                "localization/create-collection",
                "localization/upsert-entry",
                "localization/remove-entry",
                "localization/settings",
                "localization/upsert-variable",
                "localization/remove-variable");

            AddProfile(profiles, ToolProfile.FirstClass(mutatesAssets: true, longRunning: true,
                    mayReloadDomain: true),
                "asset/refresh");

            AddProfile(profiles, ToolProfile.FirstClass(),
                "localization/set-selected-locale",
                "component/set-reference");

            AddProfile(profiles, ToolProfile.FirstClass(mutatesAssets: true, longRunning: true,
                    mayReloadDomain: true),
                "packages/update-git",
                "testing/run-package-tests");

            AddProfile(profiles, ToolProfile.Fallback(),
                "advanced/execute");

            AddProfile(profiles, ToolProfile.FirstClass(),
                "project-tools/execute");

            return profiles;
        }

        private static void AddProfile(Dictionary<string, ToolProfile> profiles, ToolProfile profile,
            params string[] routes)
        {
            foreach (string route in routes)
                profiles[route] = profile;
        }

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
            EnsureRouteCache();
            var routes = _cachedRoutes;

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

        public static object GetRegisteredTools(bool firstClassOnly = true, bool compact = true,
            bool includeSchema = false, int offset = 0, int limit = 50, string category = null,
            bool includeCollections = false)
        {
            if (firstClassOnly)
                EnsureFirstClassToolMetadataCache();
            else
                EnsureToolMetadataCache();
            IEnumerable<Dictionary<string, object>> query = firstClassOnly ? _cachedFirstClassTools : _cachedTools;
            if (!string.IsNullOrEmpty(category))
            {
                query = query.Where(tool => string.Equals(
                    tool.TryGetValue("category", out var value) ? value?.ToString() : "",
                    category, StringComparison.OrdinalIgnoreCase));
            }

            var tools = query.ToList();
            offset = Math.Max(0, offset);
            limit = Math.Max(1, Math.Min(limit, 200));
            var page = tools.Skip(offset).Take(limit).ToList();
            int nextOffset = offset + page.Count;
            var result = new Dictionary<string, object>
            {
                { "schemaVersion", 3 },
                { "compact", compact },
                { "firstClassOnly", firstClassOnly },
                { "includeSchema", includeSchema },
                { "offset", offset },
                { "limit", limit },
                { "returnedTools", page.Count },
                { "totalTools", tools.Count },
                { "hasMore", nextOffset < tools.Count },
                { "nextOffset", nextOffset < tools.Count ? (object)nextOffset : null },
            };
            if (!string.IsNullOrEmpty(category))
                result["category"] = category;

            if (compact)
            {
                result["tools"] = page.Select(tool => ToCompactToolDescriptor(tool, includeSchema)).ToList();
                return result;
            }

            result["metadataSource"] = "MCPToolMetadata.ToolProfiles";
            result["tools"] = page.Select(tool => ToDetailedToolDescriptor(tool, includeSchema)).ToList();
            result["metadataIssues"] = BuildMetadataIssues(page);
            if (!includeCollections)
                return result;

            var routes = page.Select(tool => tool["route"].ToString()).ToList();
            var firstClassTools = page.Where(IsFirstClassTool).ToList();
            var fallbackTools = page.Where(tool => string.Equals(
                tool.TryGetValue("exposure", out var exposure) ? exposure?.ToString() : "",
                "fallback", StringComparison.Ordinal)).ToList();

            var grouped = new Dictionary<string, List<string>>();
            foreach (var tool in tools)
            {
                string toolCategory = tool["category"].ToString();
                if (!grouped.ContainsKey(toolCategory))
                    grouped[toolCategory] = new List<string>();
                grouped[toolCategory].Add(tool["toolName"].ToString());
            }

            result["routes"] = routes;
            result["mcpTools"] = firstClassTools.Select(tool =>
                ToMcpToolDescriptor(tool, includeSchema)).ToList();
            result["firstClassTools"] = firstClassTools.Select(tool =>
                ToDetailedToolDescriptor(tool, includeSchema)).ToList();
            result["fallbackTools"] = fallbackTools.Select(tool =>
                ToDetailedToolDescriptor(tool, includeSchema)).ToList();
            result["categories"] = grouped;
            return result;
        }

        private static Dictionary<string, object> ToCompactToolDescriptor(Dictionary<string, object> tool,
            bool includeSchema)
        {
            var descriptor = new Dictionary<string, object>
            {
                { "route", tool["route"] },
                { "toolName", tool["toolName"] },
                { "description", tool["description"] },
                { "annotations", tool["annotations"] },
                { "firstClass", IsFirstClassTool(tool) },
                { "exposure", tool["exposure"] }
            };
            if (includeSchema)
                descriptor["inputSchema"] = tool["inputSchema"];
            if (tool.TryGetValue("projectToolName", out var projectToolName))
                descriptor["projectToolName"] = projectToolName;
            return descriptor;
        }

        private static Dictionary<string, object> ToDetailedToolDescriptor(Dictionary<string, object> tool,
            bool includeSchema)
        {
            var descriptor = tool.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (!includeSchema)
                descriptor.Remove("inputSchema");
            return descriptor;
        }

        private static void EnsureToolMetadataCache()
        {
            EnsureRouteCache();
            if (_cachedTools != null)
                return;

            _cachedTools = _cachedRoutes.Select(BuildToolMetadata).ToList();
        }

        private static void EnsureFirstClassToolMetadataCache()
        {
            EnsureRouteCache();
            if (_cachedFirstClassTools != null)
                return;

            _cachedFirstClassTools = _cachedRoutes
                .Where(route => route.StartsWith("project-tools/call/", StringComparison.Ordinal) ||
                                ToolProfiles.TryGetValue(route, out var profile) &&
                                profile.Exposure == ExposureFirstClass)
                .Select(BuildToolMetadata)
                .Where(IsFirstClassTool)
                .ToList();
        }

        private static void EnsureRouteCache()
        {
            if (_cachedRoutes == null)
                _cachedRoutes = GetRegisteredRouteList();
        }

        private static bool IsFirstClassTool(Dictionary<string, object> tool)
        {
            return string.Equals(tool.TryGetValue("exposure", out var exposure) ? exposure?.ToString() : "",
                "first-class", StringComparison.Ordinal);
        }

        private static List<string> GetRegisteredRouteList()
        {
            var routes = ExtractRouteCasesFromSource();
            routes.AddRange(MCPBridgeServer.DeferredRouteNames);
            routes.AddRange(MCPProjectToolCommands.GetDirectRoutePaths());
            return routes
                .Where(route => !string.IsNullOrEmpty(route))
                .Where(route => IsRouteAvailable(route, MCPLocalizationBridge.IsAvailable))
                .Distinct()
                .OrderBy(route => route)
                .ToList();
        }

        private static bool IsRouteAvailable(string route, bool localizationAvailable)
        {
            return localizationAvailable ||
                   !route.StartsWith("localization/", StringComparison.Ordinal);
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
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBridgeServer).Assembly);
            if (!string.IsNullOrEmpty(packageInfo?.resolvedPath))
                yield return Path.Combine(packageInfo.resolvedPath, "Editor", "MCPBridgeServer.cs");

            yield return Path.Combine(projectRoot, "Packages", "com.anklebreaker.unity-mcp", "Editor",
                "MCPBridgeServer.cs");

            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                foreach (string path in Directory.EnumerateFiles(packageCacheRoot, "MCPBridgeServer.cs",
                             SearchOption.AllDirectories))
                {
                    if (path.Replace('\\', '/').Contains("com.anklebreaker.unity-mcp"))
                        yield return path;
                }
            }

            foreach (string guid in AssetDatabase.FindAssets("MCPBridgeServer t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("MCPBridgeServer.cs", StringComparison.Ordinal))
                    continue;

                yield return Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectRoot, path);
            }
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
            if (MCPProjectToolCommands.TryGetToolDictionaryForDirectRoute(route, out var projectTool))
                return BuildProjectToolMetadata(route, projectTool);

            string toolName = RouteToToolName(route);
            string description = GetToolDescription(route);
            Dictionary<string, object> inputSchema = GetToolInputSchema(route);
            ToolProfile profile = GetToolProfile(route);
            bool isFirstClass = string.Equals(profile.Exposure, ExposureFirstClass, StringComparison.Ordinal);
            return new Dictionary<string, object>
            {
                { "route", route },
                { "toolName", toolName },
                { "name", toolName },
                { "category", ExtractCategory(route) },
                { "description", description },
                { "inputSchema", inputSchema },
                { "firstClass", isFirstClass },
                { "exposure", profile.Exposure },
                { "preferred", profile.Preferred },
                { "readOnly", profile.ReadOnly },
                { "mutatesAssets", profile.MutatesAssets },
                { "dangerous", profile.Dangerous },
                { "longRunning", profile.LongRunning },
                { "mayReloadDomain", profile.MayReloadDomain },
                { "requiresPlayMode", profile.RequiresPlayMode },
                { "annotations", profile.ToAnnotations() },
                { "fallbackRoute", isFirstClass ? "" : "advanced/execute" },
            };
        }

        private static Dictionary<string, object> BuildProjectToolMetadata(string route,
            Dictionary<string, object> projectTool)
        {
            var projectToolName = projectTool.TryGetValue("toolName", out var name) ? name?.ToString() : "";
            var description = projectTool.TryGetValue("description", out var desc) ? desc?.ToString() : "";
            var inputSchema = projectTool.TryGetValue("inputSchema", out var schema)
                ? schema
                : new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", new Dictionary<string, object>() },
                    { "additionalProperties", true }
                };

            var shortName = projectTool.TryGetValue("shortName", out var shortNameValue)
                ? shortNameValue?.ToString()
                : "";
            string toolName = ProjectToolNameToToolName(projectToolName, shortName);
            string legacyToolName = "unity_project_tool_" + NormalizeProjectToolName(projectToolName);
            bool explicitMutatesAssets = GetBool(projectTool, "mutatesAssets", false);
            bool readOnly = GetBool(projectTool, "readOnly", false) ||
                            (!explicitMutatesAssets && InferProjectToolReadOnly(projectToolName));
            bool mutatesAssets = explicitMutatesAssets ||
                                 (!readOnly && InferProjectToolMutatesAssets(projectToolName));
            bool dangerous = GetBool(projectTool, "dangerous", false);
            bool longRunning = GetBool(projectTool, "longRunning", false);
            bool mayReloadDomain = GetBool(projectTool, "mayReloadDomain", false);
            bool requiresPlayMode = GetBool(projectTool, "requiresPlayMode", false);
            bool isFirstClass = readOnly || mutatesAssets;
            var profile = new ToolProfile
            {
                Exposure = isFirstClass ? ExposureFirstClass : ExposureLazy,
                Preferred = isFirstClass,
                ReadOnly = readOnly,
                MutatesAssets = mutatesAssets,
                Dangerous = dangerous,
                LongRunning = longRunning,
                MayReloadDomain = mayReloadDomain,
                RequiresPlayMode = requiresPlayMode,
            };

            return new Dictionary<string, object>
            {
                { "route", route },
                { "toolName", toolName },
                { "name", toolName },
                { "category", "project-tools" },
                { "description", string.IsNullOrEmpty(description) ? $"Project MCP tool: {projectToolName}" : description },
                { "inputSchema", inputSchema },
                { "projectToolName", projectToolName },
                { "legacyToolName", legacyToolName },
                { "firstClass", isFirstClass },
                { "exposure", profile.Exposure },
                { "preferred", profile.Preferred },
                { "readOnly", readOnly },
                { "mutatesAssets", mutatesAssets },
                { "dangerous", dangerous },
                { "longRunning", longRunning },
                { "mayReloadDomain", mayReloadDomain },
                { "requiresPlayMode", requiresPlayMode },
                { "annotations", profile.ToAnnotations() },
                { "source", projectTool.TryGetValue("source", out var source) ? source : "" },
                { "fallbackRoute", isFirstClass ? "" : "project-tools/execute" },
            };
        }

        private static bool GetBool(Dictionary<string, object> dictionary, string key, bool fallback)
        {
            if (dictionary == null || !dictionary.TryGetValue(key, out var value) || value == null)
                return fallback;

            if (value is bool boolValue)
                return boolValue;

            return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        private static bool InferProjectToolReadOnly(string projectToolName)
        {
            string name = (projectToolName ?? "").ToLowerInvariant();
            string action = name.Contains("/") ? name.Substring(name.LastIndexOf('/') + 1) : name;
            return action.StartsWith("get-") ||
                   action.StartsWith("list-") ||
                   action.StartsWith("find-") ||
                   action.StartsWith("inspect-") ||
                   action.StartsWith("query-") ||
                   action.EndsWith("-summary") ||
                   action.EndsWith("-state") ||
                   action.EndsWith("-info") ||
                   action.EndsWith("-status");
        }

        private static bool InferProjectToolMutatesAssets(string projectToolName)
        {
            string name = (projectToolName ?? "").ToLowerInvariant();
            string action = name.Contains("/") ? name.Substring(name.LastIndexOf('/') + 1) : name;
            bool assetLike = action.Contains("asset") ||
                             action.Contains("prefab") ||
                             action.Contains("resource") ||
                             action.Contains("config");

            if (!assetLike)
                return false;

            return action.StartsWith("add-") ||
                   action.StartsWith("create-") ||
                   action.StartsWith("delete-") ||
                   action.StartsWith("move-") ||
                   action.StartsWith("rename-") ||
                   action.StartsWith("remove-") ||
                   action.StartsWith("replace-") ||
                   action.StartsWith("set-") ||
                   action.StartsWith("update-");
        }

        private static ToolProfile GetToolProfile(string route)
        {
            return ToolProfiles.TryGetValue(route, out var profile) ? profile : ToolProfile.Lazy();
        }

        private static Dictionary<string, object> ToMcpToolDescriptor(Dictionary<string, object> tool,
            bool includeSchema)
        {
            var descriptor = new Dictionary<string, object>
            {
                { "name", tool.TryGetValue("toolName", out var name) ? name : "" },
                { "description", tool.TryGetValue("description", out var description) ? description : "" },
                { "annotations", tool.TryGetValue("annotations", out var annotations) ? annotations : new Dictionary<string, object>() },
                { "route", tool.TryGetValue("route", out var route) ? route : "" },
            };
            if (includeSchema)
                descriptor["inputSchema"] = tool.TryGetValue("inputSchema", out var schema)
                    ? schema
                    : new Dictionary<string, object>();
            return descriptor;
        }

        private static List<Dictionary<string, object>> BuildMetadataIssues(List<Dictionary<string, object>> tools)
        {
            var issues = new List<Dictionary<string, object>>();
            foreach (var tool in tools)
            {
                string route = tool.TryGetValue("route", out var routeObj) ? routeObj?.ToString() : "";
                string exposure = tool.TryGetValue("exposure", out var exposureObj) ? exposureObj?.ToString() : "";
                string description = tool.TryGetValue("description", out var descObj) ? descObj?.ToString() : "";
                bool hasProfile = ToolProfiles.ContainsKey(route) ||
                                  MCPProjectToolCommands.TryGetToolDictionaryForDirectRoute(route, out _);

                if (!hasProfile && string.Equals(exposure, ExposureFirstClass, StringComparison.Ordinal))
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "route", route },
                        { "issue", "first_class_without_profile" },
                    });
                }

                if (description.StartsWith("Execute Unity MCP route ", StringComparison.Ordinal))
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "route", route },
                        { "issue", "default_description" },
                    });
                }
            }

            return issues;
        }

        private static string RouteToToolName(string route)
        {
            return "unity_" + route.Replace("/", "_").Replace("-", "_");
        }

        internal static string ProjectToolNameToToolName(string projectToolName, string shortName = "")
        {
            var normalized = NormalizeProjectToolName(string.IsNullOrEmpty(shortName) ? projectToolName : shortName);

            if (string.IsNullOrEmpty(normalized))
                normalized = "tool";

            var tokens = normalized.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CompactProjectToolToken)
                .ToArray();
            string compact = "unity_pt_" + string.Join("_", tokens);
            const int maxLength = 48;
            if (compact.Length <= maxLength)
                return compact;

            string hash = ComputeStableNameHash(normalized);
            int prefixLength = maxLength - hash.Length - 1;
            return compact.Substring(0, prefixLength).TrimEnd('_') + "_" + hash;
        }

        private static string NormalizeProjectToolName(string projectToolName)
        {
            return Regex.Replace(projectToolName ?? "", "[^A-Za-z0-9]+", "_")
                .Trim('_')
                .ToLowerInvariant();
        }

        private static string CompactProjectToolToken(string token)
        {
            switch (token)
            {
                case "vmframework": return "vmf";
                case "battleidle": return "battle";
                case "visual": return "ui";
                case "element": return "el";
                case "elements": return "els";
                case "property": return "prop";
                case "properties": return "props";
                case "configuration": return "config";
                case "configurations": return "configs";
                case "wrapper": return "wrap";
                case "wrappers": return "wraps";
                default: return token;
            }
        }

        private static string ComputeStableNameHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                return hash.ToString("x8");
            }
        }

        private static string GetToolDescription(string route)
        {
            switch (route)
            {
                case "packages/update-git":
                    return "Update a Git-based Unity package and return the resolved packages-lock hash.";
                case "packages/status":
                    return "Read Package Manager manifest and lock status for one package or all Git packages.";
                case "advanced/execute":
                    return "Fallback generic entrypoint for routes that do not have a concrete tool yet. Prefer route-specific unity_* tools first.";
                case "packages/lint-metas":
                    return "Lint a Unity package root for missing .meta files.";
                case "wait/editor-idle":
                    return "Wait until the Unity Editor is idle after compilation, domain reload, package refresh, or asset import.";
                case "testing/list-tests":
                    return "List discoverable Unity tests with mode and name filters.";
                case "testing/run-tests":
                    return "Start a Unity Test Runner job and return a job ID for polling.";
                case "testing/get-job":
                    return "Poll a Unity Test Runner job, including progress, failures, and optional result details.";
                case "testing/run-package-tests":
                    return "Run tests from a Git package by temporarily enabling package testables, surviving domain reloads, and restoring manifest.json exactly.";
                case "testing/get-package-job":
                    return "Poll a persistent package test workflow through testable enablement, test execution, and exact manifest restoration.";
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
                case "scene/hierarchy":
                    return "Read the active scene hierarchy, optionally returning compact matches filtered by component type.";
                case "scene/instantiate-prefab":
                case "asset/instantiate-prefab":
                    return "Instantiate a prefab asset into the currently open scene.";
                case "prefab-asset/add-component":
                    return "Add a component to a prefab asset after waiting for a newly compiled script type to become available.";
                case "prefab-asset/add-gameobject":
                    return "Create a child GameObject inside a prefab asset.";
                case "prefab-asset/instantiate-prefab":
                case "prefab-asset/instantiate-child-prefab":
                    return "Instantiate a prefab asset as a child inside another prefab asset.";
                case "prefab-asset/hierarchy":
                    return "Get the full hierarchy tree of a prefab asset directly from disk.";
                case "prefab-asset/get-properties":
                    return "Read serialized properties from a component on a GameObject inside a prefab asset.";
                case "prefab-asset/set-property":
                    return "Set a serialized property on a component inside a prefab asset.";
                case "prefab-asset/set-reference":
                    return "Set an ObjectReference property on a component inside a prefab asset.";
                case "prefab-asset/move-gameobject":
                    return "Move or reorder a GameObject inside a prefab asset.";
                case "prefab-asset/move-component":
                    return "Atomically move a component between GameObjects inside one prefab asset while preserving serialized data.";
                case "prefab-asset/remove-component":
                    return "Remove a component from a GameObject inside a prefab asset.";
                case "prefab-asset/remove-gameobject":
                    return "Remove a child GameObject from inside a prefab asset.";
                case "prefab-asset/find":
                    return "Find GameObjects inside a prefab asset by name/path, component type, and serialized property value.";
                case "prefab-asset/transaction-edit":
                    return "Apply ordered prefab edits in one transaction with configurable immediate or frame-batched execution.";
                case "component/set-reference":
                    return "Assign one or more component ObjectReference properties with configurable immediate or frame-batched execution.";
                case "serialized-object/get":
                    return "Read serialized properties from a scene object, component, or asset via SerializedObject.";
                case "serialized-object/set":
                    return "Set one serialized property on a scene object, component, or asset via SerializedObject. SerializeReference values use '$managedReferenceType' when their concrete type cannot be inferred.";
                case "asset/refresh":
                    return "Import selected assets in order, then synchronously reconcile all external AssetDatabase changes by default.";
                case "asset/rename":
                    return "Safely rename a Unity asset using AssetDatabase while preserving its .meta GUID.";
                case "asset/move":
                    return "Preflight and move one or more Unity assets with configurable execution, GUID preservation, and rollback.";
                case "asset/export-unitypackage":
                    return "Export one or more Unity assets to a .unitypackage file using AssetDatabase.ExportPackage.";
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
                case "uitoolkit/locate-element":
                    return "Locate an Editor or runtime UI Toolkit element and return its VisualElementPath, world bounds, crop rect, and context.";
                case "uitoolkit/capture-element":
                    return "Capture an Editor or runtime UI Toolkit element by taking its containing window screenshot and cropping to the element bounds.";
                case "uitoolkit/compare-element":
                    return "Capture a UI Toolkit element and compare the cropped image against a reference image.";
                case "uitoolkit/generated-children":
                    return "Inspect generated UI Toolkit child elements such as arrows, checkmarks, scrollers, TabView internals, and unnamed unity-* subparts.";
                case "uitoolkit/resource-audit":
                    return "Audit UI Toolkit elements for resolved background assets, generated child visuals, highlighted-state misuse, and scale metadata.";
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
                case "screenshot/game":
                    return "Capture the Game View and return only after the PNG is fully written and decodable.";
                case "screenshot/crop":
                    return "Crop an existing screenshot or image file to a PNG.";
                case "gameview/info":
                    return "Read the Unity Editor Game View resolution, selected size, scale, and minimum scale.";
                case "gameview/set-resolution":
                    return "Set the Unity Editor Game View to a custom resolution.";
                case "gameview/set-scale":
                    return "Set the Unity Editor Game View zoom scale.";
                case "gameview/set-min-scale":
                    return "Set the Unity Editor Game View zoom scale to its minimum slider scale.";
                case "graphics/image-alpha-bounds":
                    return "Inspect a PNG or texture asset and return alpha-based visible pixel bounds.";
                case "graphics/rect-gap":
                    return "Measure the gap or overlap between two rectangles along an edge pair.";
                case "graphics/annotate-rects":
                    return "Draw rectangle overlays on a screenshot or image file for visual verification.";
                case "graphics/compare-images":
                    return "Compare two screenshots or image files, optionally within crop rects, and return pixel-difference bounds plus an optional diff image.";
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
                case "texture/check-import-settings":
                    return "Check TextureImporter settings against a reference texture or a pixel-sprite preset without modifying assets.";
                case "texture/check-ui-import-settings":
                    return "Check UI pixel-art image import settings, including pixel sprite defaults plus optional expected dimensions, border, and max texture size.";
                case "build/run-test":
                    return "Build the player, launch the built executable, sample Player.log, optionally capture its window, and terminate it.";
                case "animation/set-object-reference-curve":
                    return "Set AnimationClip ObjectReference keyframes, such as SpriteRenderer.m_Sprite.";
                case "localization/status":
                    return "Inspect Unity Localization package, settings, locale, and table collection status.";
                case "localization/locales":
                    return "List project Locales registered with Unity Localization.";
                case "localization/create-locale":
                    return "Create a Locale asset and optionally register it with Localization Settings.";
                case "localization/set-selected-locale":
                    return "Set the currently selected Unity Localization Locale.";
                case "localization/collections":
                    return "List String and Asset Table Collections with their Locale tables.";
                case "localization/create-collection":
                    return "Create a String or Asset Table Collection for selected Locales.";
                case "localization/entries":
                    return "Read paginated String or Asset Table entries across Locale tables.";
                case "localization/upsert-entry":
                    return "Create or update one or more localized String, Smart String, or Asset Table entries with configurable execution.";
                case "localization/remove-entry":
                    return "Remove a localization entry from one Locale table or the entire collection.";
                case "localization/validate":
                    return "Find missing, empty, and duplicate localization entries across Locale tables.";
                case "localization/settings":
                    return "Read or update Localization Settings, project Locale, and selected Locale.";
                case "localization/variables":
                    return "List Smart String persistent variable groups and values.";
                case "localization/upsert-variable":
                    return "Create or update a Smart String persistent variable and optionally create its group asset.";
                case "localization/remove-variable":
                    return "Remove a Smart String persistent variable from a registered group.";
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
                        Prop("route", "string", "Unity route to execute, e.g. prefab-asset/transaction-edit or project-tools/execute."),
                        Prop("method", "string", "HTTP-like method used for the nested route. Defaults to POST."),
                        Prop("args", "object", "Arguments passed to the nested route."),
                        Prop("body", "string", "Optional raw JSON body. If provided, args are ignored."),
                        Prop("expectedProjectPath", "string", "Optional safety check. The request is rejected if it reaches a different Unity project.")
                    ), "route");
                case "localization/status":
                    return Schema(Props());
                case "localization/locales":
                    return Schema(Props(
                        Prop("includePseudo", "boolean", "Include PseudoLocale assets. Defaults to true.")
                    ));
                case "localization/create-locale":
                    return Schema(Props(
                        Prop("code", "string", "Locale code, for example en-US or zh-CN."),
                        Prop("assetPath", "string", "Locale asset path under Assets ending in .asset."),
                        Prop("name", "string", "Optional Locale display name."),
                        Prop("addToProject", "boolean", "Register the Locale with Localization Settings. Defaults to true.")
                    ), "code", "assetPath");
                case "localization/set-selected-locale":
                    return Schema(Props(
                        Prop("locale", "string", "Registered Locale code to select.")
                    ), "locale");
                case "localization/collections":
                    return Schema(Props(
                        Prop("type", "string", "Optional collection type filter: string or asset."),
                        Prop("nameContains", "string", "Optional case-insensitive collection name filter.")
                    ));
                case "localization/create-collection":
                    return Schema(Props(
                        Prop("name", "string", "Table Collection name."),
                        Prop("type", "string", "Collection type: string or asset."),
                        Prop("assetDirectory", "string", "Existing or new directory under Assets."),
                        Prop("locales", "array", "Optional Locale codes. Defaults to every registered Locale."),
                        Prop("group", "string", "Optional Localization window group."),
                        Prop("preload", "boolean", "Optional preload flag for all created tables.")
                    ), "name", "type", "assetDirectory");
                case "localization/entries":
                    return Schema(Props(
                        Prop("collection", "string", "Table Collection name or GUID."),
                        Prop("type", "string", "Collection type: string or asset. Defaults to string."),
                        Prop("locale", "string", "Optional Locale code filter."),
                        Prop("keyContains", "string", "Optional case-insensitive key filter."),
                        Prop("offset", "number", "Filtered key offset. Defaults to 0."),
                        Prop("limit", "number", "Maximum keys returned. Defaults to 100; capped at 500.")
                    ), "collection");
                case "localization/upsert-entry":
                    return LocalizationUpsertEntriesSchema();
                case "localization/remove-entry":
                    return Schema(Props(
                        Prop("collection", "string", "Table Collection name or GUID."),
                        Prop("type", "string", "Collection type: string or asset. Defaults to string."),
                        Prop("key", "string", "Localization key to remove."),
                        Prop("locale", "string", "Optional Locale code. Omit to remove the shared key from every table.")
                    ), "collection", "key");
                case "localization/validate":
                    return Schema(Props(
                        Prop("collection", "string", "Optional Table Collection name or GUID."),
                        Prop("type", "string", "Optional collection type filter: string or asset."),
                        Prop("includeEmpty", "boolean", "Report empty values as well as missing entries. Defaults to true."),
                        Prop("maxIssues", "number", "Maximum issues returned. Defaults to 200; capped at 2000.")
                    ));
                case "localization/settings":
                    return Schema(Props(
                        Prop("initializeSynchronously", "boolean", "Optional Localization initialization mode."),
                        Prop("projectLocale", "string", "Optional registered project Locale code."),
                        Prop("selectedLocale", "string", "Optional registered selected Locale code.")
                    ));
                case "localization/variables":
                    return Schema(Props(
                        Prop("group", "string", "Optional case-insensitive persistent variable group filter."),
                        Prop("nameContains", "string", "Optional case-insensitive variable name filter.")
                    ));
                case "localization/upsert-variable":
                    return Schema(Props(
                        Prop("group", "string", "Persistent variable group name."),
                        Prop("name", "string", "Variable name inside the group."),
                        Prop("type", "string", "Variable type: bool, int, long, float, double, string, or object."),
                        Prop("value", "object", "Variable value. Object variables accept an Assets path."),
                        Prop("groupAssetPath", "string", "Required asset path when creating a missing VariablesGroupAsset.")
                    ), "group", "name", "type", "value");
                case "localization/remove-variable":
                    return Schema(Props(
                        Prop("group", "string", "Persistent variable group name."),
                        Prop("name", "string", "Variable name to remove.")
                    ), "group", "name");
                case "packages/update-git":
                    return Schema(Props(
                        Prop("name", "string", "Package name, e.g. com.example.package"),
                        Prop("gitUrl", "string", "Optional Git URL. Defaults to the current manifest Git URL."),
                        Prop("ref", "string", "Optional branch, tag, or commit. Defaults to main."),
                        Prop("skipIfResolved", "boolean", "Skip Package Manager resolve when packages-lock already matches the requested Git commit. Defaults to true."),
                        Prop("force", "boolean", "Force Package Manager resolve even when packages-lock already matches. Defaults to false.")
                    ), "name");
                case "packages/status":
                    return Schema(Props(
                        Prop("name", "string", "Optional package name. If omitted, returns all Git dependencies from the manifest."),
                        Prop("includeResolved", "boolean", "Include Package Manager resolved package data when available. Defaults to false.")
                    ));
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
                        Prop("projectName", "string", "Unity project name to resolve. Ambiguous names return an error."),
                        Prop("port", "number", "MCP bridge port to resolve.")
                    ));
                case "instance/assert-project":
                    return Schema(Props(
                        Prop("expectedProjectPath", "string", "Expected Unity project root path."),
                        Prop("expectedProjectName", "string", "Expected Unity project name.")
                    ));
                case "asset/export-unitypackage":
                    return Schema(Props(
                        Prop("assetPaths", "array", "Unity asset paths to export, e.g. Assets/MyFolder or Assets/MyPrefab.prefab."),
                        Prop("outputPath", "string", "Absolute path or project-root-relative path for the .unitypackage output."),
                        Prop("includeDependencies", "boolean", "Include asset dependencies. Defaults to true."),
                        Prop("recurse", "boolean", "Recursively export folder contents. Defaults to true."),
                        Prop("overwrite", "boolean", "Replace an existing output file. Defaults to false."),
                        Prop("interactive", "boolean", "Show Unity's export package UI. Defaults to false.")
                    ), "outputPath");
                case "editor/execute-code":
                    return Schema(Props(
                        Prop("code", "string", "C# method body to execute. Return a value to serialize it."),
                        Prop("usings", "array", "Additional namespace imports. UnityEngine.UIElements is included by default."),
                        Prop("maxResultItems", "number", "Maximum serialized collection/object entries across the result. Defaults to 200; capped at 2000."),
                        Prop("maxResultDepth", "number", "Maximum serialized result depth. Defaults to 8; capped at 16."),
                        Prop("maxResultStringLength", "number", "Maximum characters per returned string. Defaults to 20000; capped at 200000.")
                    ), "code");
                case "scene/hierarchy":
                    return Schema(Props(
                        Prop("maxDepth", "number", "Maximum hierarchy depth to return. Defaults to 10."),
                        Prop("maxNodes", "number", "Maximum hierarchy nodes to return. Defaults to 250; capped at 2000."),
                        Prop("parentPath", "string", "Optional GameObject path used as the search root."),
                        Prop("componentType", "string", "Optional component type name or full name. When set, returns compact flat matches instead of the full hierarchy."),
                        Prop("nameContains", "string", "Optional case-insensitive GameObject name filter used with componentType."),
                        Prop("pathContains", "string", "Optional case-insensitive hierarchy path filter used with componentType."),
                        Prop("offset", "number", "Component-filtered result offset. Defaults to 0."),
                        Prop("maxResults", "number", "Maximum component-filtered matches. Defaults to min(maxNodes, 50); capped at 200.")
                    ));
                case "testing/list-tests":
                    return Schema(Props(
                        Prop("mode", "string", "Test mode: EditMode or PlayMode. Defaults to EditMode."),
                        Prop("nameFilter", "string", "Optional case-insensitive test full-name filter."),
                        Prop("offset", "number", "Test result offset. Defaults to 0."),
                        Prop("maxResults", "number", "Maximum tests to return. Defaults to 100; capped at 500.")
                    ));
                case "testing/run-tests":
                    return Schema(Props(
                        Prop("mode", "string", "Test mode: EditMode or PlayMode. Defaults to EditMode."),
                        Prop("testNames", "array", "Optional exact test full names."),
                        Prop("categories", "array", "Optional test categories."),
                        Prop("assemblies", "array", "Optional test assembly names."),
                        Prop("groupNames", "array", "Optional Unity Test Runner group names."),
                        Prop("clearStuck", "boolean", "Force-clear a previously stuck job before starting. Defaults to false.")
                    ));
                case "testing/get-job":
                    return Schema(Props(
                        Prop("jobId", "string", "Optional job ID. Defaults to the current or latest job."),
                        Prop("includeDetails", "boolean", "Include paginated individual test results. Defaults to false."),
                        Prop("includeFailedOnly", "boolean", "Include only failed or inconclusive test results."),
                        Prop("includeStackTrace", "boolean", "Include test stack traces. Defaults to false."),
                        Prop("offset", "number", "Individual test result offset. Defaults to 0."),
                        Prop("limit", "number", "Individual test result limit. Defaults to 100; capped at 500."),
                        Prop("failureLimit", "number", "Maximum failures included in progress. Defaults to 20; capped at 100.")
                    ));
                case "testing/run-package-tests":
                    return Schema(Props(
                        Prop("packageName", "string", "Git package name. Defaults to com.anklebreaker.unity-mcp."),
                        Prop("mode", "string", "Test mode: EditMode or PlayMode. Defaults to EditMode."),
                        Prop("assemblies", "array", "Test assembly names. Defaults to the Unity MCP regression assembly for the Unity MCP package."),
                        Prop("testNames", "array", "Optional exact test full names."),
                        Prop("categories", "array", "Optional test categories."),
                        Prop("groupNames", "array", "Optional Unity Test Runner group names.")
                    ));
                case "testing/get-package-job":
                    return Schema(Props(
                        Prop("workflowId", "string", "Optional package test workflow ID. Defaults to the active or latest workflow."),
                        Prop("clear", "boolean", "Delete terminal workflow state after returning it. Defaults to false.")
                    ));
                case "scene/instantiate-prefab":
                case "asset/instantiate-prefab":
                    return Schema(Props(
                        Prop("prefabPath", "string", "Prefab asset path to instantiate into the currently open scene."),
                        Prop("name", "string", "Optional name for the created scene instance."),
                        Prop("parent", "string", "Optional scene GameObject name used as the parent."),
                        Prop("position", "object", "Optional world position object with x/y/z."),
                        Prop("rotation", "object", "Optional world Euler rotation object with x/y/z.")
                    ), "prefabPath");
                case "prefab-asset/hierarchy":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to inspect."),
                        Prop("prefabPath", "string", "Optional GameObject path used as the hierarchy root."),
                        Prop("maxDepth", "number", "Maximum hierarchy depth to return. Defaults to 10."),
                        Prop("maxNodes", "number", "Maximum hierarchy nodes to return. Defaults to 250; capped at 2000.")
                    ), "assetPath");
                case "prefab-asset/get-properties":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to inspect."),
                        Prop("prefabPath", "string", "Path of the GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name.")
                    ), "assetPath", "componentType");
                case "prefab-asset/set-property":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name."),
                        Prop("propertyName", "string", "Serialized property name or property path to set."),
                        Prop("value", "object", "Serialized value to assign."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffContextLines", "number", "Context lines around prefab YAML changes. Defaults to 2."),
                        Prop("prefabFileDiffMaxLines", "number", "Maximum diff lines returned. Defaults to 200."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
                    ), "assetPath", "componentType", "propertyName", "value");
                case "prefab-asset/set-reference":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name. Optional when propertyName can identify the component."),
                        Prop("propertyName", "string", "ObjectReference serialized property name or property path."),
                        Prop("referenceAssetPath", "string", "Project asset path to assign."),
                        Prop("referencePrefabPath", "string", "Path of a GameObject inside the same prefab to assign."),
                        Prop("referenceComponentType", "string", "When using referencePrefabPath, assign this component instead of the GameObject."),
                        Prop("clear", "boolean", "Clear the ObjectReference."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
                    ), "assetPath", "propertyName");
                case "prefab-asset/instantiate-prefab":
                case "prefab-asset/instantiate-child-prefab":
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
                case "prefab-asset/add-gameobject":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("parentPrefabPath", "string", "Parent path inside the prefab. Empty means root."),
                        Prop("name", "string", "Name of the new child GameObject."),
                        Prop("primitiveType", "string", "Optional Unity PrimitiveType to create, e.g. Cube or Sphere."),
                        Prop("position", "object", "Optional local position object with x/y/z."),
                        Prop("rotation", "object", "Optional local Euler rotation object with x/y/z."),
                        Prop("scale", "object", "Optional local scale object with x/y/z."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
                    ), "assetPath", "name");
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
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary."),
                        Prop("prefabFileDiffIgnoreContains", "array", "Optional substrings used to hide noisy diff lines."),
                        Prop("prefabFileDiffIgnoreYamlProperties", "array", "Optional YAML property names used to hide noisy diff lines.")
                    ), "assetPath", "componentType");
                case "prefab-asset/remove-component":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name."),
                        Prop("index", "number", "Component index when multiple components of the same type exist. Defaults to 0."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
                    ), "assetPath", "componentType");
                case "prefab-asset/move-component":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("sourcePrefabPath", "string", "Path of the source GameObject inside the prefab. Empty means root."),
                        Prop("targetPrefabPath", "string", "Path of the target GameObject inside the prefab. Empty means root."),
                        Prop("componentType", "string", "Component type name or full name."),
                        Prop("componentIndex", "number", "Component index on the source GameObject. Defaults to 0."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffContextLines", "number", "Context lines around prefab YAML changes. Defaults to 2."),
                        Prop("prefabFileDiffMaxLines", "number", "Maximum diff lines returned. Defaults to 200."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
                    ), "assetPath", "sourcePrefabPath", "targetPrefabPath", "componentType");
                case "prefab-asset/move-gameobject":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the GameObject to move inside the prefab."),
                        Prop("newParentPrefabPath", "string", "New parent path inside the prefab. Empty means root."),
                        Prop("siblingIndex", "number", "Optional sibling index under the new parent."),
                        Prop("worldPositionStays", "boolean", "Preserve world transform while reparenting. Defaults to false.")
                    ), "assetPath", "prefabPath");
                case "prefab-asset/remove-gameobject":
                    return Schema(Props(
                        Prop("assetPath", "string", "Prefab asset path to edit."),
                        Prop("prefabPath", "string", "Path of the child GameObject to remove. Cannot be root."),
                        Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                        Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary.")
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
                case "prefab-asset/transaction-edit":
                    return PrefabAssetTransactionEditSchema();
                case "component/set-reference":
                    return ComponentSetReferenceSchema();
                case "serialized-object/get":
                    return Schema(Props(
                        Prop("instanceId", "number", "Target Unity object instance id."),
                        Prop("assetPath", "string", "Target asset path if instanceId is omitted."),
                        Prop("assetType", "string", "Optional asset type name/full name used when loading assetPath."),
                        Prop("gameObjectPath", "string", "Scene GameObject path if instanceId and assetPath are omitted."),
                        Prop("componentType", "string", "Optional component type to select from a GameObject target."),
                        Prop("componentIndex", "number", "Component index when multiple components of the same type exist."),
                        Prop("propertyPath", "string", "Optional serialized property path to read."),
                        Prop("offset", "number", "Visible property offset. Defaults to 0."),
                        Prop("maxProperties", "number", "Maximum properties to return when propertyPath is omitted. Defaults to 50; capped at 500."),
                        Prop("includeChildren", "boolean", "Walk child properties. Defaults to false."),
                        Prop("maxDepth", "number", "Maximum nested serialized value depth. Defaults to 3; capped at 8."),
                        Prop("maxArrayElements", "number", "Maximum elements returned per serialized array. Defaults to 50; capped at 500.")
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
                        Prop("value", "object", "Serialized value. ObjectReference supports assetPath, instanceId, or gameObject. SerializeReference objects may include '$managedReferenceType' as 'AssemblyName::Namespace.TypeName'.")
                    ), "propertyPath", "value");
                case "asset/rename":
                    return Schema(Props(
                        Prop("path", "string", "Current asset path, e.g. Assets/Art/Old Name.png."),
                        Prop("newName", "string", "New file or folder name. Do not include a directory path."),
                        Prop("dryRun", "boolean", "Validate and return expected paths without renaming.")
                    ));
                case "asset/refresh":
                    return Schema(Props(
                        Prop("assetPaths", "array", "Optional Unity asset paths to import first in the provided order."),
                        Prop("forceUpdate", "boolean", "Use ImportAssetOptions.ForceUpdate. Defaults to true."),
                        Prop("saveAssets", "boolean", "Call AssetDatabase.SaveAssets after refresh/import. Defaults to false."),
                        Prop("reconcileExternalChanges", "boolean", "Run a synchronous full AssetDatabase refresh after targeted imports so externally deleted or created files are reconciled before success is returned. Defaults to true.")
                    ));
                case "asset/move":
                    return AssetMoveSchema();
                case "console/query":
                    return Schema(Props(
                        Prop("count", "number", "Maximum returned entries. Defaults to 50; capped at 200."),
                        Prop("offset", "number", "Filtered entry offset, counting from the newest match. Defaults to 0."),
                        Prop("type", "string", "Filter by all, error, warning, info, exception, or assert. Defaults to all."),
                        Prop("messageContains", "string", "Case-insensitive message substring filter."),
                        Prop("sourceContains", "string", "Case-insensitive source stack frame/path substring filter."),
                        Prop("stackContains", "string", "Case-insensitive full stack substring filter."),
                        Prop("since", "string", "Start time filter. Accepts ISO/local time, Unix seconds, or Unix milliseconds."),
                        Prop("until", "string", "End time filter. Accepts ISO/local time, Unix seconds, or Unix milliseconds."),
                        Prop("sinceSecondsAgo", "number", "Start time filter relative to now."),
                        Prop("sinceLastPlay", "boolean", "Only include entries recorded after the latest Play transition."),
                        Prop("includeStack", "boolean", "Include full stack traces. Defaults to false."),
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
                        Prop("layerIndex", "number", "Layer index. Defaults to 0."),
                        Prop("requiredParameters", "array", "Strings or objects with name/parameterName and optional type/parameterType."),
                        Prop("requiredStates", "array", "State names that must exist."),
                        Prop("requireMotion", "boolean", "Require every state in the layer to have a motion."),
                        Prop("requiredTransitions", "array", "Objects with source/sourceState, destination/destinationState, and optional conditionParameter."),
                        Prop("requireFullMesh", "boolean", "Require all stateNames to have pairwise transitions."),
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
                        Prop("ussPath", "string", "Optional USS asset path. UXML Style src entries are also auto-resolved."),
                        Prop("ussPaths", "array", "Optional USS asset paths. UXML Style src entries are also auto-resolved."),
                        Prop("name", "string", "VisualElement.name exact match."),
                        Prop("names", "array", "VisualElement.name values to validate."),
                        Prop("className", "string", "USS class exact match."),
                        Prop("typeName", "string", "Expected or filtered VisualElement type name."),
                        Prop("maxResults", "number", "Total result budget for elements and name matches. Defaults to 100."),
                        Prop("includeUss", "boolean", "Parse USS files and attach default size declarations. Defaults to true."),
                        Prop("includeElements", "boolean", "Return the general elements collection. Defaults to false for names queries and true otherwise."),
                        Prop("includeAllUssClasses", "boolean", "Return every parsed USS class. Targeted queries default to only classes used by returned elements.")
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
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names, e.g. MainMap/RightControls."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
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
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
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
                case "uitoolkit/locate-element":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("runtime", "boolean", "Locate a runtime UIDocument element when true; otherwise locate an EditorWindow UI Toolkit element. Defaults to false."),
                        Prop("window", "string", "EditorWindow type/title. Runtime defaults to Game when capture uses it later."),
                        Prop("path", "string", "Element tree path, e.g. root/0/1."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("text", "string", "TextElement text contains match if path is omitted."),
                        Prop("pixelScale", "number", "Scale from UI points to captured pixels. Defaults to EditorGUIUtility.pixelsPerPoint."),
                        Prop("padding", "number", "Extra crop padding in pixels. Defaults to 0.")
                    ));
                case "uitoolkit/capture-element":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("runtime", "boolean", "Capture a runtime UIDocument element when true; otherwise capture an EditorWindow UI Toolkit element. Defaults to false."),
                        Prop("window", "string", "EditorWindow type/title to capture. Runtime defaults to Game, editor defaults to the focused/matched window."),
                        Prop("path", "string", "Element tree path, e.g. root/0/1."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("visualElementNames", "array", "VisualElementPath names array."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("text", "string", "TextElement text contains match if path is omitted."),
                        Prop("outputPath", "string", "Output PNG path for the cropped element screenshot."),
                        Prop("windowOutputPath", "string", "Output PNG path for the full containing window screenshot."),
                        Prop("pixelScale", "number", "Scale from UI points to captured pixels. Defaults to EditorGUIUtility.pixelsPerPoint."),
                        Prop("padding", "number", "Extra crop padding in pixels. Defaults to 0.")
                    ));
                case "uitoolkit/compare-element":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("runtime", "boolean", "Capture a runtime UIDocument element when true; otherwise capture an EditorWindow UI Toolkit element. Defaults to false."),
                        Prop("window", "string", "EditorWindow type/title to capture. Runtime defaults to Game."),
                        Prop("path", "string", "Element tree path, e.g. root/0/1."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("referencePath", "string", "Reference PNG path."),
                        Prop("actualPath", "string", "Output path for captured current element PNG."),
                        Prop("diffOutputPath", "string", "Optional output path for diff PNG."),
                        Prop("referenceRect", "object", "Optional comparison rect in reference image."),
                        Prop("actualRect", "object", "Optional comparison rect in captured image."),
                        Prop("tolerance", "number", "Allowed per-channel pixel delta. Defaults to 0."),
                        Prop("padding", "number", "Extra capture padding in pixels. Defaults to 0.")
                    ));
                case "uitoolkit/generated-children":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("runtime", "boolean", "Inspect a runtime UIDocument element when true; otherwise inspect an EditorWindow UI Toolkit element. Defaults to false."),
                        Prop("window", "string", "EditorWindow type/title for editor inspection."),
                        Prop("path", "string", "Element tree path, e.g. root/0/1."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names."),
                        Prop("name", "string", "VisualElement.name exact match if path is omitted."),
                        Prop("className", "string", "USS class name exact match if path is omitted."),
                        Prop("typeName", "string", "VisualElement type name contains match if path is omitted."),
                        Prop("maxDepth", "number", "Descendant depth to inspect. Defaults to 4."),
                        Prop("includeAll", "boolean", "Return all descendants, not only generated-looking children. Defaults to false."),
                        Prop("forbiddenClassContains", "array", "Class substrings that should produce warnings when found."),
                        Prop("forbiddenTypeContains", "array", "Type-name substrings that should produce warnings when found.")
                    ));
                case "uitoolkit/resource-audit":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("runtime", "boolean", "Audit runtime UIDocument elements when true; otherwise audit EditorWindow UI Toolkit elements. Defaults to false."),
                        Prop("window", "string", "EditorWindow type/title for editor audits."),
                        Prop("queries", "array", "Optional list of element queries. Each accepts path, visualElementPath, name, className, typeName, text, expectedBackgroundContains, forbiddenBackgroundContains, requireBackground."),
                        Prop("path", "string", "Element tree path if queries is omitted."),
                        Prop("visualElementPath", "string", "Slash-separated VisualElementPath names if queries is omitted."),
                        Prop("name", "string", "VisualElement.name exact match if queries is omitted."),
                        Prop("expectedBackgroundContains", "string", "Expected substring in resolved background asset path or name."),
                        Prop("forbiddenBackgroundContains", "array", "Substrings that must not appear in the resolved background asset path or name."),
                        Prop("requireBackground", "boolean", "Warn if the target has no resolved background image."),
                        Prop("warnHighlighted", "boolean", "Warn when a target appears to use a highlighted asset. Defaults to true."),
                        Prop("maxDepth", "number", "Descendant depth to scan for background resources. Defaults to 3.")
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
                        Prop("waitFrames", "number", "Editor frames to wait before capturing. Defaults to 8."),
                        Prop("stableFrames", "number", "Consecutive ready UI Builder frames required. Defaults to 2."),
                        Prop("timeoutMs", "number", "Maximum time to wait for the requested document and canvas. Defaults to 10000."),
                        Prop("capture", "boolean", "Capture the UI Builder window after opening. Defaults to true."),
                        Prop("screenshotPath", "string", "PNG path for the UI Builder screenshot."),
                        Prop("maxDimension", "number", "Maximum screenshot dimension. Defaults to 8192."),
                        Prop("zoom", "number", "Requested zoom, recorded for diagnostics. UI Builder has no stable public zoom API.")
                    ));
                case "uitoolkit/assert-layout":
                    return RuntimeUIDocumentSchema(Props(
                        Prop("assertions", "array", "Layout assertions. Supported types: edge-touch, same-edge, same-center, inside, size.")
                    ), "assertions");
                case "screenshot/game":
                    return Schema(Props(
                        Prop("path", "string", "Output PNG path. Defaults under Assets/Screenshots."),
                        Prop("superSize", "number", "Resolution multiplier. Defaults to 1."),
                        Prop("waitFrames", "number", "Frames to wait before requesting the capture. Defaults to 2."),
                        Prop("stableFrames", "number", "Consecutive stable file-size frames required. Defaults to 2."),
                        Prop("timeoutMs", "number", "Maximum time to wait for a complete decodable PNG. Defaults to 10000.")
                    ));
                case "screenshot/crop":
                    return Schema(Props(
                        Prop("sourcePath", "string", "Image path to crop."),
                        Prop("rect", "object", "Crop rect with x, y, width, height."),
                        Prop("outputPath", "string", "Output PNG path. Defaults next to source with _crop suffix."),
                        Prop("originTopLeft", "boolean", "Treat rect x/y as top-left image coordinates. Defaults to true.")
                    ));
                case "gameview/info":
                    return Schema(Props());
                case "gameview/set-resolution":
                    return Schema(Props(
                        Prop("width", "number", "Game View custom resolution width in pixels."),
                        Prop("height", "number", "Game View custom resolution height in pixels."),
                        Prop("label", "string", "Optional custom size label shown in the Game View size menu.")
                    ), "width", "height");
                case "gameview/set-scale":
                    return Schema(Props(
                        Prop("scale", "number", "Game View zoom scale, e.g. 0.76 or 1.")
                    ), "scale");
                case "gameview/set-min-scale":
                    return Schema(Props(
                        Prop("fallbackScale", "number", "Fallback minimum scale used if Unity internals do not expose a valid one. Defaults to 0.76.")
                    ));
                case "graphics/image-alpha-bounds":
                    return Schema(Props(
                        Prop("assetPath", "string", "Texture2D asset path."),
                        Prop("filePath", "string", "Absolute or project-relative PNG path if assetPath is omitted."),
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
                        Prop("sourcePath", "string", "Image path to annotate."),
                        Prop("outputPath", "string", "Output PNG path. Defaults next to source with _annotated suffix."),
                        Prop("rects", "array", "Rectangles to draw. Each has x, y, width, height, optional color and thickness."),
                        Prop("originTopLeft", "boolean", "Treat rect x/y as top-left image coordinates. Defaults to true."),
                        Prop("color", "string", "Default HTML color, e.g. #ff00ffff."),
                        Prop("thickness", "number", "Default border thickness in pixels. Defaults to 2.")
                    ), "rects");
                case "graphics/compare-images":
                    return Schema(Props(
                        Prop("expectedPath", "string", "Reference image path."),
                        Prop("actualPath", "string", "Current image path."),
                        Prop("expectedRect", "object", "Optional reference crop rect with x, y, width, height."),
                        Prop("actualRect", "object", "Optional current crop rect with x, y, width, height."),
                        Prop("tolerance", "number", "Per-channel pixel tolerance, 0-255. Defaults to 0."),
                        Prop("maxSamples", "number", "Maximum differing pixel samples returned. Defaults to 20."),
                        Prop("diffOutputPath", "string", "Optional PNG path to write a red-highlight diff image.")
                    ));
                case "sprite/sheet-info":
                    return Schema(Props(
                        Prop("texturePath", "string", "Sprite sheet texture asset path.")
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
                        Prop("texturePath", "string", "Sprite sheet texture asset path."),
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
                        Prop("texturePath", "string", "Sprite sheet texture asset path."),
                        Prop("bindingPath", "string", "Animation binding path to SpriteRenderer. Empty means the animated object itself."),
                        Prop("frameRate", "number", "Animation frame rate. Defaults to the clip frame rate or 12."),
                        Prop("spriteNames", "array", "Optional exact sprite names to use."),
                        Prop("loopTime", "boolean", "Whether the clip loops. Defaults to the current clip setting.")
                    ), "clipPath", "texturePath");
                case "sprite/replace-slice-update-clip":
                    return Schema(Props(
                        Prop("texturePath", "string", "Sprite sheet texture asset path."),
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
                        Prop("path", "string", "Texture asset path."),
                        Prop("referencePath", "string", "Optional texture asset whose importer settings are copied first."),
                        Prop("preset", "string", "High-level preset. Supported: pixel-sprite."),
                        Prop("pixelsPerUnit", "number", "Sprite pixels per unit."),
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
                        Prop("sourceUrl", "string", "Remote image URL."),
                        Prop("targetPath", "string", "Target asset path inside Assets."),
                        Prop("targetFolder", "string", "Target folder used with assetName."),
                        Prop("assetName", "string", "Target file name used with targetFolder."),
                        Prop("overwrite", "boolean", "Overwrite targetPath if content differs. Defaults to false."),
                        Prop("dedupeByHash", "boolean", "Skip if the target folder already contains identical image bytes. Defaults to true."),
                        Prop("applySpritePreset", "boolean", "Apply sprite import settings after import. Defaults to true."),
                        Prop("preset", "string", "Preset passed to texture/apply-sprite-preset. Defaults to pixel-sprite.")
                    ));
                case "texture/check-import-settings":
                    return Schema(Props(
                        Prop("assetPath", "string", "Texture asset path to check."),
                        Prop("assetPaths", "array", "Texture asset paths to check."),
                        Prop("folderPath", "string", "Folder to scan recursively for Texture2D assets."),
                        Prop("referencePath", "string", "Optional texture asset whose importer settings are treated as expected."),
                        Prop("preset", "string", "Optional high-level preset to check. Supported: pixel-sprite."),
                        Prop("requirePixelSprite", "boolean", "Shortcut for preset=pixel-sprite. Defaults to true when referencePath is omitted."),
                        Prop("includeMatching", "boolean", "Include passing comparisons in the returned comparisons list. Defaults to false.")
                    ));
                case "texture/check-ui-import-settings":
                    return Schema(Props(
                        Prop("assetPath", "string", "Texture asset path to check."),
                        Prop("assetPaths", "array", "Texture asset paths to check."),
                        Prop("folderPath", "string", "Folder to scan recursively for Texture2D assets."),
                        Prop("referencePath", "string", "Optional texture asset whose importer settings are treated as expected."),
                        Prop("includeMatching", "boolean", "Include passing comparisons in the returned comparisons list. Defaults to false."),
                        Prop("expectedWidth", "number", "Optional exact texture width check."),
                        Prop("expectedHeight", "number", "Optional exact texture height check."),
                        Prop("expectedBorder", "object", "Optional sprite border check. Accepts object with left/bottom/right/top or x/y/z/w."),
                        Prop("maxTextureSize", "number", "Optional exact TextureImporter maxTextureSize check."),
                        Prop("tolerance", "number", "Float tolerance for border/PPU checks. Defaults to 0.001.")
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

        private static Dictionary<string, object> ExecutionSchema(bool includeContinueOnError = true)
        {
            var properties = Props(
                Prop("operationsPerFrame", "number", "Maximum operations processed in one editor frame. Defaults to 25."),
                Prop("frameBudgetMs", "number", "Soft per-frame execution budget in milliseconds. Defaults to 8."),
                Prop("timeoutMs", "number", "Maximum total execution time in milliseconds. Defaults to 90000."));
            properties["mode"] = new Dictionary<string, object>
            {
                { "type", "string" },
                { "description", "Execution mode. auto batches multi-operation requests, immediate runs in one frame, and batched yields across frames." },
                { "enum", new List<object> { "auto", "immediate", "batched" } },
            };
            if (includeContinueOnError)
                properties["continueOnError"] = Prop("continueOnError", "boolean",
                    "Continue processing later operations after one fails. Defaults to false.").Value;
            return Schema(properties);
        }

        private static Dictionary<string, object> ComponentSetReferenceSchema()
        {
            var referenceProperties = Props(
                Prop("path", "string", "Target scene GameObject path or name."),
                Prop("instanceId", "string", "Target scene GameObject instance ID."),
                Prop("componentType", "string", "Component containing the property."),
                Prop("propertyName", "string", "ObjectReference property to assign."),
                Prop("assetPath", "string", "Asset path to assign."),
                Prop("referenceGameObject", "string", "Scene GameObject path or name to assign."),
                Prop("referenceComponentType", "string", "Component type on the referenced GameObject."),
                Prop("referenceInstanceId", "number", "Unity object instance ID to assign."),
                Prop("clear", "boolean", "Clear the reference."));
            var properties = Props(
                Prop("path", "string", "Default target GameObject inherited by reference items."),
                Prop("instanceId", "string", "Default target instance ID inherited by reference items."),
                Prop("componentType", "string", "Default component type inherited by reference items."));
            properties["execution"] = ExecutionSchema();
            properties["references"] = new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", "Reference assignments. Every item requires propertyName and one reference source or clear=true." },
                { "items", Schema(referenceProperties, "propertyName") },
            };
            return Schema(properties, "references");
        }

        private static Dictionary<string, object> PrefabAssetTransactionEditSchema()
        {
            var properties = Props(
                Prop("assetPath", "string", "Prefab asset path to edit."),
                Prop("waitForTypes", "boolean", "Wait for all referenced component types before editing. Defaults to true."),
                Prop("typeResolveTimeoutMs", "number", "Maximum type wait time in milliseconds. Defaults to 30000."),
                Prop("typeResolveStableMs", "number", "Continuous idle time after type resolution before editing. Defaults to 500."),
                Prop("refreshAssets", "boolean", "When referenced component types are missing, return a retryable response and schedule AssetDatabase.Refresh after the response. The refresh is skipped when all types are already loaded. Defaults to true."),
                Prop("includePrefabFileDiff", "boolean", "Return before/after prefab YAML diff. Defaults to true."),
                Prop("prefabFileDiffContextLines", "number", "Context lines around prefab YAML changes. Defaults to 2."),
                Prop("prefabFileDiffMaxLines", "number", "Maximum diff lines returned. Defaults to 200."),
                Prop("prefabFileDiffMode", "string", "Diff return mode: summary, minimal, or full. Defaults to summary."),
                Prop("prefabFileDiffIgnoreContains", "array", "Optional substrings used to hide noisy diff lines."),
                Prop("prefabFileDiffIgnoreYamlProperties", "array", "Optional YAML property names used to hide noisy diff lines.")
            );
            properties["execution"] = ExecutionSchema(includeContinueOnError: false);

            properties["operations"] = new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", "Ordered prefab edits. Each item uses type plus the fields accepted by the matching prefab-asset route." },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", new Dictionary<string, object>
                            {
                                { "type", new Dictionary<string, object>
                                    {
                                        { "type", "string" },
                                        { "enum", new List<object>
                                            {
                                                "addComponent", "setProperty", "setReference", "addGameObject",
                                                "instantiatePrefab", "removeComponent", "removeGameObject", "moveGameObject"
                                            }
                                        }
                                    }
                            }
                        }
                        },
                        { "required", new List<object> { "type" } },
                        { "additionalProperties", true }
                    }
                }
            };

            return Schema(properties, "assetPath", "operations");
        }

        private static Dictionary<string, object> AssetMoveSchema()
        {
            var moveProperties = Props(
                Prop("path", "string", "Current asset path."),
                Prop("destinationPath", "string", "Destination asset path, or an existing folder path to keep the same file name."),
                Prop("destinationFolder", "string", "Existing folder path to keep the same file name.")
            );

            var properties = Props(
                Prop("dryRun", "boolean", "Validate every move and return expected paths without moving."));
            properties["execution"] = ExecutionSchema();
            properties["moves"] = new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", "Move requests. Every item needs path and either destinationPath or destinationFolder. Duplicate sources and targets are rejected before execution." },
                { "items", Schema(moveProperties) }
            };

            return Schema(properties, "moves");
        }

        private static Dictionary<string, object> LocalizationUpsertEntriesSchema()
        {
            var entryProperties = Props(
                Prop("key", "string", "Shared localization key."),
                Prop("locale", "string", "Target Locale code."),
                Prop("value", "string", "String or Smart String value when type is string."),
                Prop("smart", "boolean", "Optional Smart String flag when type is string."),
                Prop("assetPath", "string", "Asset path when type is asset."),
                Prop("subAssetName", "string", "Optional exact sub-asset name at assetPath."));

            var properties = Props(
                Prop("collection", "string", "Table Collection name or GUID."),
                Prop("type", "string", "Collection type: string or asset. Defaults to string."),
                Prop("createTables", "boolean", "Create missing Locale tables. Defaults to true."));
            properties["execution"] = ExecutionSchema();
            properties["entries"] = new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", "Up to 500 Locale entry writes. The entire request is validated before changes are made." },
                { "items", Schema(entryProperties, "key", "locale") },
            };

            return Schema(properties, "collection", "entries");
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

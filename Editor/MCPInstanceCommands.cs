using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityMCP.Editor
{
    public static class MCPInstanceCommands
    {
        public static object Current(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "instance", MCPInstanceRegistry.GetCurrentInstanceInfo() },
                { "activePort", MCPBridgeServer.ActivePort },
                { "registeredPort", MCPInstanceRegistry.RegisteredPort }
            };
        }

        public static object List(Dictionary<string, object> args)
        {
            bool includeStale = GetBool(args, "includeStale", false);
            var instances = MCPInstanceRegistry.GetRegisteredInstances(includeStale == false);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "currentProjectPath", MCPInstanceRegistry.CurrentProjectPath },
                { "currentPort", MCPBridgeServer.ActivePort },
                { "instances", instances },
                { "totalInstances", instances.Count }
            };
        }

        public static object Resolve(Dictionary<string, object> args)
        {
            var instances = MCPInstanceRegistry.GetRegisteredInstances();
            var matches = GetMatches(instances, args).ToList();

            if (matches.Count == 1)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "instance", matches[0] },
                    { "port", GetInt(matches[0], "port", 0) },
                    { "totalMatches", 1 }
                };
            }

            var baseResult = new Dictionary<string, object>
            {
                { "success", false },
                { "matches", matches },
                { "totalMatches", matches.Count },
                { "instances", instances }
            };

            baseResult["error"] = matches.Count == 0
                ? "No Unity MCP instance matched the requested project."
                : "More than one Unity MCP instance matched the requested project.";

            return baseResult;
        }

        public static object AssertProject(Dictionary<string, object> args)
        {
            string expectedProjectPath = GetExpectedProjectPath(args);
            string expectedProjectName = GetString(args, "expectedProjectName");
            if (string.IsNullOrEmpty(expectedProjectName))
                expectedProjectName = GetString(args, "projectName");

            var mismatch = BuildProjectMismatch(expectedProjectPath, expectedProjectName, "instance/assert-project");
            if (mismatch != null)
                return mismatch;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "projectPath", MCPInstanceRegistry.CurrentProjectPath },
                { "projectName", MCPInstanceRegistry.CurrentProjectName },
                { "port", MCPBridgeServer.ActivePort }
            };
        }

        public static object BuildProjectMismatch(string expectedProjectPath, string expectedProjectName, string route)
        {
            string currentProjectPath = MCPInstanceRegistry.CurrentProjectPath;
            if (!string.IsNullOrEmpty(expectedProjectPath) &&
                MCPInstanceRegistry.ProjectPathEquals(currentProjectPath, expectedProjectPath) == false)
            {
                return BuildMismatchResult(route, expectedProjectPath, expectedProjectName);
            }

            if (!string.IsNullOrEmpty(expectedProjectName) &&
                string.Equals(MCPInstanceRegistry.CurrentProjectName, expectedProjectName,
                    StringComparison.OrdinalIgnoreCase) == false)
            {
                return BuildMismatchResult(route, expectedProjectPath, expectedProjectName);
            }

            return null;
        }

        public static string GetExpectedProjectPath(Dictionary<string, object> args)
        {
            string expectedProjectPath = GetString(args, "expectedProjectPath");
            if (string.IsNullOrEmpty(expectedProjectPath))
                expectedProjectPath = GetString(args, "targetProjectPath");
            if (string.IsNullOrEmpty(expectedProjectPath))
                expectedProjectPath = GetString(args, "unityProjectPath");

            return expectedProjectPath;
        }

        private static object BuildMismatchResult(string route, string expectedProjectPath, string expectedProjectName)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "wrong_unity_project" },
                { "message", "This request targeted a different Unity project. Resolve the correct instance by project path and retry with its port." },
                { "route", route },
                { "expectedProjectPath", expectedProjectPath ?? "" },
                { "expectedProjectName", expectedProjectName ?? "" },
                { "actualProjectPath", MCPInstanceRegistry.CurrentProjectPath },
                { "actualProjectName", MCPInstanceRegistry.CurrentProjectName },
                { "actualPort", MCPBridgeServer.ActivePort },
                { "currentInstance", MCPInstanceRegistry.GetCurrentInstanceInfo() }
            };
        }

        private static IEnumerable<Dictionary<string, object>> GetMatches(
            List<Dictionary<string, object>> instances, Dictionary<string, object> args)
        {
            string projectPath = GetString(args, "projectPath");
            if (string.IsNullOrEmpty(projectPath))
                projectPath = GetString(args, "path");
            if (string.IsNullOrEmpty(projectPath))
                projectPath = GetExpectedProjectPath(args);

            string projectName = GetString(args, "projectName");
            string expectedProjectName = GetString(args, "expectedProjectName");
            if (string.IsNullOrEmpty(projectName))
                projectName = expectedProjectName;

            int port = GetInt(args, "port", 0);

            if (string.IsNullOrEmpty(projectPath) && string.IsNullOrEmpty(projectName) && port <= 0)
                return Enumerable.Empty<Dictionary<string, object>>();

            return instances.Where(instance =>
            {
                if (!string.IsNullOrEmpty(projectPath))
                {
                    string instancePath = GetString(instance, "projectPath");
                    if (MCPInstanceRegistry.ProjectPathEquals(instancePath, projectPath) == false)
                        return false;
                }

                if (!string.IsNullOrEmpty(projectName))
                {
                    string instanceName = GetString(instance, "projectName");
                    if (string.Equals(instanceName, projectName, StringComparison.OrdinalIgnoreCase) == false)
                        return false;
                }

                if (port > 0 && GetInt(instance, "port", 0) != port)
                    return false;

                return true;
            });
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            if (args == null || args.TryGetValue(key, out var value) == false || value == null)
                return "";

            return value.ToString();
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || args.TryGetValue(key, out var value) == false || value == null)
                return defaultValue;

            return bool.TryParse(value.ToString(), out bool result) ? result : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || args.TryGetValue(key, out var value) == false || value == null)
                return defaultValue;

            return int.TryParse(value.ToString(), out int result) ? result : defaultValue;
        }
    }
}

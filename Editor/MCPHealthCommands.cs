using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityMCP.Editor
{
    public static class MCPHealthCommands
    {
        public static object GetHealth(Dictionary<string, object> args)
        {
            int recentCount = GetInt(args, "recentCount", 20);
            int slowThresholdMs = GetInt(args, "slowThresholdMs", 1000);

            var process = Process.GetCurrentProcess();
            var recent = MCPActionHistory.GetRecent(Math.Max(1, recentCount));
            var slowActions = recent
                .Where(record => record.ExecutionTimeMs >= slowThresholdMs)
                .OrderByDescending(record => record.ExecutionTimeMs)
                .Select(ActionToDictionary)
                .ToList();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "server", new Dictionary<string, object>
                    {
                        { "isRunning", MCPBridgeServer.IsRunning },
                        { "activePort", MCPBridgeServer.ActivePort },
                        { "autoStart", MCPSettingsManager.AutoStart },
                        { "useManualPort", MCPSettingsManager.UseManualPort },
                        { "configuredPort", MCPSettingsManager.Port },
                        { "startOnVirtualPlayers", MCPSettingsManager.StartOnVirtualPlayers },
                    }
                },
                { "editor", new Dictionary<string, object>
                    {
                        { "projectPath", GetProjectRoot() },
                        { "unityVersion", Application.unityVersion },
                        { "isPlaying", EditorApplication.isPlaying },
                        { "isPaused", EditorApplication.isPaused },
                        { "isCompiling", EditorApplication.isCompiling },
                        { "isUpdating", EditorApplication.isUpdating },
                        { "timeSinceStartup", Math.Round(EditorApplication.timeSinceStartup, 3) },
                    }
                },
                { "process", new Dictionary<string, object>
                    {
                        { "id", process.Id },
                        { "processName", process.ProcessName },
                        { "workingSetBytes", process.WorkingSet64 },
                        { "privateMemoryBytes", process.PrivateMemorySize64 },
                        { "managedMemoryBytes", GC.GetTotalMemory(false) },
                        { "threadCount", process.Threads.Count },
                    }
                },
                { "profiler", new Dictionary<string, object>
                    {
                        { "totalAllocatedMemoryBytes", Profiler.GetTotalAllocatedMemoryLong() },
                        { "totalReservedMemoryBytes", Profiler.GetTotalReservedMemoryLong() },
                        { "monoUsedSizeBytes", Profiler.GetMonoUsedSizeLong() },
                        { "monoHeapSizeBytes", Profiler.GetMonoHeapSizeLong() },
                    }
                },
                { "queue", MCPRequestQueue.GetQueueInfo() },
                { "sessions", new Dictionary<string, object>
                    {
                        { "activeSessionCount", MCPRequestQueue.ActiveSessionCount },
                        { "totalSessionCount", MCPRequestQueue.TotalSessionCount },
                        { "activeSessions", MCPRequestQueue.GetActiveSessions() },
                    }
                },
                { "history", new Dictionary<string, object>
                    {
                        { "count", MCPActionHistory.Count },
                        { "recentCount", recent.Count },
                        { "recent", recent.Select(ActionToDictionary).ToList() },
                        { "slowThresholdMs", slowThresholdMs },
                        { "slowActions", slowActions },
                    }
                },
            };
        }

        public static object SetServerAutoStart(Dictionary<string, object> args)
        {
            if (args == null || args.ContainsKey("enabled") == false)
                return new { error = "enabled is required" };

            bool enabled = GetBool(args, "enabled", MCPSettingsManager.AutoStart);
            MCPSettingsManager.AutoStart = enabled;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "autoStart", MCPSettingsManager.AutoStart },
                { "message", enabled ? "MCP bridge will auto-start for this Unity instance." : "MCP bridge auto-start is disabled for this Unity instance." },
            };
        }

        private static Dictionary<string, object> ActionToDictionary(MCPActionRecord record)
        {
            return new Dictionary<string, object>
            {
                { "id", record.Id },
                { "timestamp", record.Timestamp.ToString("O") },
                { "agentId", record.AgentId ?? "" },
                { "actionName", record.ActionName ?? "" },
                { "category", record.Category ?? "" },
                { "status", record.Status ?? "" },
                { "executionTimeMs", record.ExecutionTimeMs },
                { "errorMessage", record.ErrorMessage ?? "" },
                { "targetPath", record.TargetPath ?? "" },
                { "targetType", record.TargetType ?? "" },
            };
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            return int.TryParse(args[key].ToString(), out int parsed) ? parsed : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            if (args[key] is bool value)
                return value;

            return bool.TryParse(args[key].ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static string GetProjectRoot()
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
        }
    }
}

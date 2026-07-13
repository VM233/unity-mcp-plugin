using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    internal static class MCPAssetRefreshWorkflow
    {
        private static readonly TimeSpan StableIdleDuration = TimeSpan.FromMilliseconds(500);
        private static AssetRefreshJob _job;
        private static bool _updateRegistered;

        static MCPAssetRefreshWorkflow()
        {
            _job = LoadJob();
            if (_job == null || _job.IsTerminal)
                return;

            if (_job.Status == "running")
            {
                _job.Status = "waiting-for-editor";
                _job.RecoveredAfterReload = true;
                TouchAndSave();
            }
            EnsureUpdateRegistered();
        }

        public static object Start(Dictionary<string, object> args)
        {
            bool clearStuck = GetBool(args, "clearStuck", false);
            if (_job != null && !_job.IsTerminal && !clearStuck)
            {
                var active = BuildResponse(_job);
                active["success"] = false;
                active["error"] = "An AssetDatabase refresh job is already running.";
                return active;
            }

            var copiedArgs = args != null
                ? MiniJson.Deserialize(MiniJson.Serialize(args)) as Dictionary<string, object>
                : new Dictionary<string, object>();
            copiedArgs ??= new Dictionary<string, object>();
            copiedArgs.Remove("clearStuck");
            _job = new AssetRefreshJob
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                Status = "queued",
                Arguments = copiedArgs,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            SaveJob();
            EnsureUpdateRegistered();
            return BuildResponse(_job);
        }

        public static object Get(Dictionary<string, object> args)
        {
            if (_job == null)
                _job = LoadJob();
            if (_job == null)
                return new { error = "No AssetDatabase refresh job was found." };

            string jobId = GetString(args, "jobId");
            if (!string.IsNullOrEmpty(jobId) && jobId != _job.JobId)
                return new { error = $"AssetDatabase refresh job '{jobId}' was not found." };

            var response = BuildResponse(_job);
            if (_job.IsTerminal && GetBool(args, "clear", false))
            {
                DeleteJobFile();
                _job = null;
                response["cleared"] = true;
            }
            return response;
        }

        private static void ContinueJob()
        {
            if (_job == null || _job.IsTerminal)
            {
                UnregisterUpdate();
                return;
            }
            if (_job.Status != "waiting-for-editor" &&
                (EditorApplication.isCompiling || EditorApplication.isUpdating))
                return;

            if (_job.Status == "waiting-for-editor")
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    if (_job.IdleSince != default)
                    {
                        _job.IdleSince = default;
                        TouchAndSave();
                    }
                    return;
                }

                if (_job.IdleSince == default)
                {
                    _job.IdleSince = DateTime.UtcNow;
                    TouchAndSave();
                    return;
                }

                if (DateTime.UtcNow - _job.IdleSince < StableIdleDuration)
                    return;

                if (GetBool(_job.Arguments, "saveAssets", false))
                    AssetDatabase.SaveAssets();
                _job.Result ??= BuildRecoveredResult(_job.Arguments);
                _job.Result["isUpdating"] = false;
                _job.Result["isCompiling"] = false;
                _job.Result["settledAfterRefresh"] = true;
                _job.Status = "succeeded";
                TouchAndSave();
                UnregisterUpdate();
                return;
            }

            if (_job.Status != "queued")
                return;

            _job.Status = "running";
            TouchAndSave();
            try
            {
                _job.Result = MCPResponse.ToDictionary(
                    MCPAssetCommands.ExecuteRefreshImmediate(_job.Arguments));
                bool success = _job.Result != null && _job.Result.TryGetValue("success", out object value) &&
                               value is bool succeeded && succeeded;
                _job.Status = success ? "waiting-for-editor" : "failed";
                if (!success && _job.Result != null && _job.Result.TryGetValue("error", out object error))
                    _job.Error = error?.ToString();
            }
            catch (Exception ex)
            {
                _job.Status = "failed";
                _job.Error = ex.GetBaseException().Message;
            }
            finally
            {
                TouchAndSave();
                if (_job.Status != "waiting-for-editor")
                    UnregisterUpdate();
            }
        }

        private static Dictionary<string, object> BuildRecoveredResult(Dictionary<string, object> args)
        {
            var paths = GetStringList(args, "assetPaths");
            bool reconcile = GetBool(args, "reconcileExternalChanges", true);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "forceUpdate", GetBool(args, "forceUpdate", true) },
                { "saveAssets", GetBool(args, "saveAssets", false) },
                { "importedPaths", paths },
                { "preReconciledExternalChanges", paths.Count > 0 && reconcile },
                { "reconciledExternalChanges", paths.Count == 0 || reconcile },
                { "refreshedAllAssets", paths.Count == 0 || reconcile },
                { "recoveredAfterReload", true },
                { "isUpdating", EditorApplication.isUpdating },
                { "isCompiling", EditorApplication.isCompiling },
            };
        }

        private static Dictionary<string, object> BuildResponse(AssetRefreshJob job)
        {
            var response = new Dictionary<string, object>
            {
                { "success", job.IsTerminal ? job.Status == "succeeded" : true },
                { "jobId", job.JobId },
                { "status", job.Status },
                { "pollRoute", "asset/get-refresh-job" },
                { "recoveredAfterReload", job.RecoveredAfterReload },
                { "startedAt", job.StartedAt.ToString("O") },
                { "updatedAt", job.UpdatedAt.ToString("O") },
            };
            if (!string.IsNullOrEmpty(job.Error))
                response["error"] = job.Error;
            if (job.Result != null)
                response["result"] = job.Result;
            return response;
        }

        private static void EnsureUpdateRegistered()
        {
            if (_updateRegistered)
                return;
            EditorApplication.update += ContinueJob;
            _updateRegistered = true;
        }

        private static void UnregisterUpdate()
        {
            if (!_updateRegistered)
                return;
            EditorApplication.update -= ContinueJob;
            _updateRegistered = false;
        }

        private static void TouchAndSave()
        {
            _job.UpdatedAt = DateTime.UtcNow;
            SaveJob();
        }

        private static void SaveJob()
        {
            if (_job == null)
                return;
            string path = GetJobPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, MiniJson.Serialize(_job.ToDictionary()));
        }

        private static AssetRefreshJob LoadJob()
        {
            try
            {
                string path = GetJobPath();
                if (!File.Exists(path))
                    return null;
                return AssetRefreshJob.FromDictionary(
                    MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Unity MCP Asset Refresh] Failed to restore refresh job: {ex.Message}");
                return null;
            }
        }

        private static string GetJobPath()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, "Library", "UnityMCP", "asset-refresh-job.json");
        }

        private static void DeleteJobFile()
        {
            string path = GetJobPath();
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : "";
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;
            if (value is bool result)
                return result;
            return bool.TryParse(value.ToString(), out result) ? result : defaultValue;
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return new List<string>();
            if (value is string single)
                return new List<string> { single };
            if (value is System.Collections.IEnumerable enumerable)
                return enumerable.Cast<object>().Where(item => item != null)
                    .Select(item => item.ToString()).Where(item => item.Length > 0).Distinct().ToList();
            return new List<string>();
        }

        private sealed class AssetRefreshJob
        {
            public string JobId;
            public string Status;
            public Dictionary<string, object> Arguments;
            public Dictionary<string, object> Result;
            public string Error;
            public bool RecoveredAfterReload;
            public DateTime IdleSince;
            public DateTime StartedAt;
            public DateTime UpdatedAt;

            public bool IsTerminal => Status == "succeeded" || Status == "failed";

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "jobId", JobId },
                    { "status", Status },
                    { "arguments", Arguments },
                    { "result", Result },
                    { "error", Error ?? "" },
                    { "recoveredAfterReload", RecoveredAfterReload },
                    { "idleSince", IdleSince == default ? "" : IdleSince.ToString("O") },
                    { "startedAt", StartedAt.ToString("O") },
                    { "updatedAt", UpdatedAt.ToString("O") },
                };
            }

            public static AssetRefreshJob FromDictionary(Dictionary<string, object> values)
            {
                if (values == null)
                    return null;
                return new AssetRefreshJob
                {
                    JobId = GetString(values, "jobId"),
                    Status = GetString(values, "status"),
                    Arguments = values.TryGetValue("arguments", out object arguments)
                        ? arguments as Dictionary<string, object>
                        : new Dictionary<string, object>(),
                    Result = values.TryGetValue("result", out object result)
                        ? result as Dictionary<string, object>
                        : null,
                    Error = GetString(values, "error"),
                    RecoveredAfterReload = GetBool(values, "recoveredAfterReload", false),
                    IdleSince = ParseDate(values, "idleSince", useUtcNowFallback: false),
                    StartedAt = ParseDate(values, "startedAt"),
                    UpdatedAt = ParseDate(values, "updatedAt"),
                };
            }

            private static DateTime ParseDate(Dictionary<string, object> values, string key,
                bool useUtcNowFallback = true)
            {
                return DateTime.TryParse(GetString(values, key), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
                    ? parsed
                    : useUtcNowFallback ? DateTime.UtcNow : default;
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public static class MCPPackageTestCommands
    {
        private const string DefaultPackageName = "com.anklebreaker.unity-mcp";
        private const string DefaultTestAssembly = "AnkleBreaker.UnityMCP.Editor.Tests";
        private const double WorkflowTimeoutMinutes = 10;

        private static PackageTestWorkflow _workflow;
        private static bool _updateRegistered;

        static MCPPackageTestCommands()
        {
            _workflow = LoadWorkflow();
            if (_workflow != null && !_workflow.IsTerminal)
                EnsureUpdateRegistered();
        }

        public static object RunPackageTests(Dictionary<string, object> args)
        {
            if (_workflow != null && !_workflow.IsTerminal)
            {
                if (!string.Equals(_workflow.OwnerAgentId ?? "anonymous",
                        GetString(args, "_agentId", "anonymous"), StringComparison.Ordinal))
                    return MCPResponse.Error("Package test workflow belongs to another agent.",
                        "job_owner_mismatch");
                EnsureUpdateRegistered();
                ContinueWorkflow();
                return new Dictionary<string, object>
                {
                    { "error", "A package test workflow is already running" },
                    { "workflow", BuildResponse(_workflow) }
                };
            }

            string packageName = GetString(args, "packageName", DefaultPackageName);
            string[] assemblies = ParseStringArray(args, "assemblies");
            if ((assemblies == null || assemblies.Length == 0) && packageName == DefaultPackageName)
                assemblies = new[] { DefaultTestAssembly };
            if (assemblies == null || assemblies.Length == 0)
                return new { error = "assemblies is required for package tests outside the Unity MCP package" };

            string manifestPath = GetManifestPath();
            if (!File.Exists(manifestPath))
                return new { error = $"Package manifest not found at '{manifestPath}'" };

            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            string manifestText = File.ReadAllText(manifestPath);
            if (!TryParseManifest(manifestText, out var manifest, out string manifestError))
                return new { error = manifestError };

            bool alreadyTestable = IsPackageTestable(manifest, packageName);
            _workflow = new PackageTestWorkflow
            {
                WorkflowId = Guid.NewGuid().ToString("N").Substring(0, 12),
                PackageName = packageName,
                Mode = GetString(args, "mode", "EditMode"),
                Assemblies = assemblies,
                TestNames = ParseStringArray(args, "testNames"),
                Categories = ParseStringArray(args, "categories"),
                GroupNames = ParseStringArray(args, "groupNames"),
                ManifestPath = manifestPath,
                OriginalManifestBase64 = Convert.ToBase64String(manifestBytes),
                OriginalManifestHadUtf8Bom = HasUtf8Bom(manifestBytes),
                ManifestChanged = !alreadyTestable,
                State = alreadyTestable ? "waiting-for-assembly" : "enabling",
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                OwnerAgentId = GetString(args, "_agentId", "anonymous"),
            };
            SaveWorkflow();
            EnsureUpdateRegistered();
            Debug.Log($"[MCP Package Tests] Started workflow {_workflow.WorkflowId} for {_workflow.PackageName}");

            return BuildResponse(_workflow);
        }

        public static object GetPackageTestJob(Dictionary<string, object> args)
        {
            if (_workflow == null)
                _workflow = LoadWorkflow();
            if (_workflow == null)
                return new { error = "No package test workflow found" };
            if (!string.Equals(_workflow.OwnerAgentId ?? "anonymous", GetString(args, "_agentId", "anonymous"),
                    StringComparison.Ordinal))
                return MCPResponse.Error("Package test workflow belongs to another agent.",
                    "job_owner_mismatch");

            string workflowId = GetString(args, "workflowId");
            if (!string.IsNullOrEmpty(workflowId) && workflowId != _workflow.WorkflowId)
                return new { error = $"Package test workflow '{workflowId}' not found" };

            if (!_workflow.IsTerminal)
            {
                EnsureUpdateRegistered();
                ContinueWorkflow();
            }

            bool clear = GetBool(args, "clear", false);
            var response = BuildResponse(_workflow);
            if (clear && _workflow.IsTerminal)
            {
                DeleteWorkflowFile();
                _workflow = null;
                response["cleared"] = true;
            }
            return response;
        }

        internal static bool TryGetActiveWorkflow(out string workflowId, out string packageName,
            out string state)
        {
            if (_workflow == null)
                _workflow = LoadWorkflow();

            if (_workflow == null || _workflow.IsTerminal)
            {
                workflowId = "";
                packageName = "";
                state = "";
                return false;
            }

            workflowId = _workflow.WorkflowId ?? "";
            packageName = _workflow.PackageName ?? "";
            state = _workflow.State ?? "";
            return true;
        }

        private static void EnablePackageTests()
        {
            if (_workflow == null || _workflow.State != "enabling")
                return;

            try
            {
                string manifestText = File.ReadAllText(_workflow.ManifestPath);
                if (!TryParseManifest(manifestText, out var manifest, out string error))
                    throw new InvalidOperationException(error);

                if (!IsPackageTestable(manifest, _workflow.PackageName))
                {
                    if (!(manifest.TryGetValue("testables", out var rawTestables) &&
                          rawTestables is List<object> testables))
                    {
                        testables = new List<object>();
                        manifest["testables"] = testables;
                    }
                    testables.Add(_workflow.PackageName);
                }

                string updatedManifest = SerializePrettyJson(manifest, 0) + "\n";
                File.WriteAllText(_workflow.ManifestPath, updatedManifest,
                    new UTF8Encoding(_workflow.OriginalManifestHadUtf8Bom));
                _workflow.State = "waiting-for-assembly";
                TouchAndSaveWorkflow();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Client.Resolve();
            }
            catch (Exception ex)
            {
                FailWorkflow($"Failed to enable package tests: {ex.Message}");
            }
        }

        private static void ContinueWorkflow()
        {
            if (_workflow == null || _workflow.IsTerminal)
            {
                UnregisterUpdate();
                return;
            }

            if (_workflow.State != "restoring" &&
                (DateTime.UtcNow - _workflow.StartedAt).TotalMinutes > WorkflowTimeoutMinutes)
            {
                FailWorkflow($"Package test workflow exceeded {WorkflowTimeoutMinutes:0} minutes");
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            try
            {
                switch (_workflow.State)
                {
                    case "enabling":
                        if (ManifestContainsPackageTestable(_workflow.ManifestPath, _workflow.PackageName))
                        {
                            _workflow.State = "waiting-for-assembly";
                            TouchAndSaveWorkflow();
                        }
                        else
                        {
                            EnablePackageTests();
                        }
                        break;
                    case "waiting-for-assembly":
                        if (AreAssembliesLoaded(_workflow.Assemblies))
                        {
                            StartTestRun();
                        }
                        else if (TryGetCompilationFailure(out string compilationError))
                        {
                            FailWorkflow(compilationError);
                        }
                        break;
                    case "running":
                        UpdateRunningTestJob();
                        break;
                    case "restoring":
                        CompleteRestoreWhenReady();
                        break;
                    default:
                        FailWorkflow($"Unknown package test workflow state '{_workflow.State}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                FailWorkflow(ex.GetBaseException().Message);
            }
        }

        private static void StartTestRun()
        {
            var runArgs = new Dictionary<string, object>
            {
                { "mode", _workflow.Mode },
                { "assemblies", _workflow.Assemblies.Cast<object>().ToList() },
                { "_agentId", _workflow.OwnerAgentId ?? "anonymous" }
            };
            AddArray(runArgs, "testNames", _workflow.TestNames);
            AddArray(runArgs, "categories", _workflow.Categories);
            AddArray(runArgs, "groupNames", _workflow.GroupNames);

            var runResult = MCPResponse.ToDictionary(MCPTestRunnerCommands.RunTests(runArgs));
            if (runResult == null || !GetBool(runResult, "success", false))
            {
                string error = runResult != null ? GetString(runResult, "error", "Failed to start tests") :
                    "Failed to start tests";
                FailWorkflow(error);
                return;
            }

            _workflow.TestJobId = GetString(runResult, "jobId");
            _workflow.State = "running";
            TouchAndSaveWorkflow();
            Debug.Log($"[MCP Package Tests] Workflow {_workflow.WorkflowId} started test job " +
                      _workflow.TestJobId);
        }

        private static void UpdateRunningTestJob()
        {
            var jobResult = MCPResponse.ToDictionary(MCPTestRunnerCommands.GetTestJob(
                new Dictionary<string, object>
                {
                    { "jobId", _workflow.TestJobId },
                    { "includeFailedOnly", true },
                    { "includeStackTrace", true },
                    { "limit", 100 },
                    { "_agentId", _workflow.OwnerAgentId ?? "anonymous" },
                }));
            if (jobResult == null)
                return;

            string status = GetString(jobResult, "status");
            if (status == "running")
                return;

            _workflow.TestResult = jobResult;
            _workflow.TestSucceeded = status == "succeeded";
            if (!_workflow.TestSucceeded)
                _workflow.Error = GetString(jobResult, "error", "Package tests failed");
            if (_workflow.ManifestChanged)
                BeginRestore();
            else
                CompleteWorkflow();
        }

        private static void BeginRestore()
        {
            _workflow.State = "restoring";
            TouchAndSaveWorkflow();
            Debug.Log($"[MCP Package Tests] Workflow {_workflow.WorkflowId} restoring package manifest");
        }

        private static void RestoreManifest()
        {
            if (_workflow == null || !_workflow.ManifestChanged)
                return;

            try
            {
                byte[] originalBytes = Convert.FromBase64String(_workflow.OriginalManifestBase64);
                File.WriteAllBytes(_workflow.ManifestPath, originalBytes);
                TouchAndSaveWorkflow();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Client.Resolve();
            }
            catch (Exception ex)
            {
                _workflow.Error = $"Failed to restore package manifest: {ex.Message}";
                _workflow.State = "failed";
                TouchAndSaveWorkflow();
            }
        }

        private static void CompleteRestoreWhenReady()
        {
            byte[] originalBytes = Convert.FromBase64String(_workflow.OriginalManifestBase64);
            if (!File.Exists(_workflow.ManifestPath) ||
                !File.ReadAllBytes(_workflow.ManifestPath).SequenceEqual(originalBytes))
            {
                RestoreManifest();
                return;
            }

            if (AreAssembliesLoaded(_workflow.Assemblies))
                return;

            CompleteWorkflow();
        }

        private static void CompleteWorkflow()
        {
            _workflow.State = string.IsNullOrEmpty(_workflow.Error) && _workflow.TestSucceeded
                ? "succeeded"
                : "failed";
            TouchAndSaveWorkflow();
            UnregisterUpdate();
            Debug.Log($"[MCP Package Tests] Workflow {_workflow.WorkflowId} finished with state {_workflow.State}");
        }

        private static void FailWorkflow(string error)
        {
            if (_workflow == null)
                return;

            _workflow.Error = error;
            _workflow.TestSucceeded = false;
            if (_workflow.ManifestChanged)
                BeginRestore();
            else
            {
                _workflow.State = "failed";
                TouchAndSaveWorkflow();
                UnregisterUpdate();
            }
        }

        private static Dictionary<string, object> BuildResponse(PackageTestWorkflow workflow)
        {
            var response = new Dictionary<string, object>
            {
                { "success", workflow.IsTerminal ? workflow.State == "succeeded" : true },
                { "workflowId", workflow.WorkflowId },
                { "status", workflow.State },
                { "packageName", workflow.PackageName },
                { "mode", workflow.Mode },
                { "assemblies", workflow.Assemblies ?? Array.Empty<string>() },
                { "manifestChanged", workflow.ManifestChanged },
                { "manifestRestored", !workflow.ManifestChanged || ManifestIsRestored(workflow) },
                { "startedAt", workflow.StartedAt.ToString("O") },
                { "updatedAt", workflow.UpdatedAt.ToString("O") },
            };
            if (!string.IsNullOrEmpty(workflow.TestJobId))
                response["testJobId"] = workflow.TestJobId;
            if (!string.IsNullOrEmpty(workflow.Error))
                response["error"] = workflow.Error;
            if (workflow.TestResult != null)
                response["testResult"] = workflow.TestResult;
            return response;
        }

        private static bool ManifestIsRestored(PackageTestWorkflow workflow)
        {
            try
            {
                return File.Exists(workflow.ManifestPath) && File.ReadAllBytes(workflow.ManifestPath)
                    .SequenceEqual(Convert.FromBase64String(workflow.OriginalManifestBase64));
            }
            catch
            {
                return false;
            }
        }

        private static bool ManifestContainsPackageTestable(string manifestPath, string packageName)
        {
            return File.Exists(manifestPath) &&
                   TryParseManifest(File.ReadAllText(manifestPath), out var manifest, out _) &&
                   IsPackageTestable(manifest, packageName);
        }

        private static bool TryParseManifest(string text, out Dictionary<string, object> manifest,
            out string error)
        {
            try
            {
                manifest = MiniJson.Deserialize(text) as Dictionary<string, object>;
                error = manifest == null ? "Packages/manifest.json is not a JSON object" : null;
                return manifest != null;
            }
            catch (Exception ex)
            {
                manifest = null;
                error = $"Packages/manifest.json could not be parsed: {ex.Message}";
                return false;
            }
        }

        private static bool IsPackageTestable(Dictionary<string, object> manifest, string packageName)
        {
            return manifest.TryGetValue("testables", out var rawTestables) &&
                   rawTestables is List<object> testables &&
                   testables.Any(value => value?.ToString() == packageName);
        }

        private static bool AreAssembliesLoaded(IEnumerable<string> assemblyNames)
        {
            var requested = new HashSet<string>(assemblyNames ?? Array.Empty<string>());
            if (requested.Count == 0)
                return true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                requested.Remove(assembly.GetName().Name);
            return requested.Count == 0;
        }

        private static bool TryGetCompilationFailure(out string error)
        {
            var result = MCPResponse.ToDictionary(MCPConsoleCommands.GetCompilationErrors(
                new Dictionary<string, object>
                {
                    { "severity", "error" },
                    { "count", 20 },
                }));
            return TryBuildCompilationFailure(result, out error);
        }

        private static bool TryBuildCompilationFailure(Dictionary<string, object> result, out string error)
        {
            error = null;
            if (result == null || !result.TryGetValue("entries", out object rawEntries) ||
                rawEntries is not IEnumerable entries)
            {
                return false;
            }

            var messages = new List<string>();
            foreach (object rawEntry in entries)
            {
                var entry = MCPResponse.ToDictionary(rawEntry);
                if (entry == null || !string.Equals(GetString(entry, "severity"), "error",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string assembly = GetString(entry, "assembly", "unknown assembly");
                string file = GetString(entry, "file");
                string message = GetString(entry, "message", "Unknown compiler error");
                string location = string.IsNullOrEmpty(file) ? assembly : $"{assembly}: {file}";
                messages.Add($"{location}: {message}");
            }

            if (messages.Count == 0)
            {
                return false;
            }

            error = "Package test assemblies failed to compile: " + string.Join(" | ", messages);
            return true;
        }

        private static string SerializePrettyJson(object value, int depth)
        {
            string indent = new string(' ', depth * 2);
            string childIndent = new string(' ', (depth + 1) * 2);
            if (value is Dictionary<string, object> dictionary)
            {
                if (dictionary.Count == 0) return "{}";
                var entries = dictionary.Select(pair => childIndent + MiniJson.Serialize(pair.Key) + ": " +
                                                        SerializePrettyJson(pair.Value, depth + 1));
                return "{\n" + string.Join(",\n", entries) + "\n" + indent + "}";
            }

            if (value is IList list)
            {
                if (list.Count == 0) return "[]";
                var entries = list.Cast<object>().Select(item => childIndent + SerializePrettyJson(item, depth + 1));
                return "[\n" + string.Join(",\n", entries) + "\n" + indent + "]";
            }

            return MiniJson.Serialize(value);
        }

        private static void EnsureUpdateRegistered()
        {
            if (_updateRegistered)
                return;
            EditorApplication.update += ContinueWorkflow;
            _updateRegistered = true;
        }

        private static void UnregisterUpdate()
        {
            if (!_updateRegistered)
                return;
            EditorApplication.update -= ContinueWorkflow;
            _updateRegistered = false;
        }

        private static void TouchAndSaveWorkflow()
        {
            _workflow.UpdatedAt = DateTime.UtcNow;
            SaveWorkflow();
        }

        private static void SaveWorkflow()
        {
            if (_workflow == null)
                return;
            string path = GetWorkflowPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, MiniJson.Serialize(_workflow.ToDictionary()));
            MCPJobHistory.Record("package-test", _workflow.WorkflowId, _workflow.OwnerAgentId,
                _workflow.State, BuildResponse(_workflow));
        }

        private static PackageTestWorkflow LoadWorkflow()
        {
            try
            {
                string path = GetWorkflowPath();
                if (!File.Exists(path))
                    return null;
                var values = MiniJson.Deserialize(File.ReadAllText(path)) as Dictionary<string, object>;
                return values != null ? PackageTestWorkflow.FromDictionary(values) : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Package Tests] Failed to restore workflow: {ex.Message}");
                return null;
            }
        }

        private static void DeleteWorkflowFile()
        {
            string path = GetWorkflowPath();
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string GetManifestPath()
        {
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", "manifest.json");
        }

        private static string GetWorkflowPath()
        {
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "UnityMCP",
                "package-test-workflow.json");
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static string[] ParseStringArray(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
                return null;
            if (value is string text)
                return text.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
            if (value is List<object> list)
                return list.Select(item => item?.ToString()).Where(item => !string.IsNullOrEmpty(item)).ToArray();
            return null;
        }

        private static void AddArray(Dictionary<string, object> args, string key, string[] values)
        {
            if (values != null && values.Length > 0)
                args[key] = values.Cast<object>().ToList();
        }

        private static string GetString(Dictionary<string, object> args, string key, string defaultValue = "")
        {
            return args != null && args.TryGetValue(key, out var value) && value != null
                ? value.ToString()
                : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            return args != null && args.TryGetValue(key, out var value) && value != null
                ? Convert.ToBoolean(value)
                : defaultValue;
        }

        private sealed class PackageTestWorkflow
        {
            public string WorkflowId;
            public string State;
            public string PackageName;
            public string Mode;
            public string[] Assemblies;
            public string[] TestNames;
            public string[] Categories;
            public string[] GroupNames;
            public string ManifestPath;
            public string OriginalManifestBase64;
            public bool OriginalManifestHadUtf8Bom;
            public bool ManifestChanged;
            public string TestJobId;
            public bool TestSucceeded;
            public Dictionary<string, object> TestResult;
            public string Error;
            public DateTime StartedAt;
            public DateTime UpdatedAt;
            public string OwnerAgentId;

            public bool IsTerminal => State == "succeeded" || State == "failed";

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "workflowId", WorkflowId },
                    { "state", State },
                    { "packageName", PackageName },
                    { "mode", Mode },
                    { "assemblies", ToObjectList(Assemblies) },
                    { "testNames", ToObjectList(TestNames) },
                    { "categories", ToObjectList(Categories) },
                    { "groupNames", ToObjectList(GroupNames) },
                    { "manifestPath", ManifestPath },
                    { "originalManifestBase64", OriginalManifestBase64 },
                    { "originalManifestHadUtf8Bom", OriginalManifestHadUtf8Bom },
                    { "manifestChanged", ManifestChanged },
                    { "testJobId", TestJobId ?? "" },
                    { "testSucceeded", TestSucceeded },
                    { "testResult", TestResult },
                    { "error", Error ?? "" },
                    { "startedAt", StartedAt.ToString("O") },
                    { "updatedAt", UpdatedAt.ToString("O") },
                    { "ownerAgentId", OwnerAgentId ?? "anonymous" },
                };
            }

            public static PackageTestWorkflow FromDictionary(Dictionary<string, object> values)
            {
                return new PackageTestWorkflow
                {
                    WorkflowId = GetValue(values, "workflowId"),
                    State = GetValue(values, "state"),
                    PackageName = GetValue(values, "packageName"),
                    Mode = GetValue(values, "mode", "EditMode"),
                    Assemblies = GetArray(values, "assemblies"),
                    TestNames = GetArray(values, "testNames"),
                    Categories = GetArray(values, "categories"),
                    GroupNames = GetArray(values, "groupNames"),
                    ManifestPath = GetValue(values, "manifestPath"),
                    OriginalManifestBase64 = GetValue(values, "originalManifestBase64"),
                    OriginalManifestHadUtf8Bom = GetBoolean(values, "originalManifestHadUtf8Bom"),
                    ManifestChanged = GetBoolean(values, "manifestChanged"),
                    TestJobId = GetValue(values, "testJobId"),
                    TestSucceeded = GetBoolean(values, "testSucceeded"),
                    TestResult = values.TryGetValue("testResult", out var result)
                        ? result as Dictionary<string, object>
                        : null,
                    Error = GetValue(values, "error"),
                    StartedAt = GetDateTime(values, "startedAt"),
                    UpdatedAt = GetDateTime(values, "updatedAt"),
                    OwnerAgentId = GetValue(values, "ownerAgentId", "anonymous"),
                };
            }

            private static List<object> ToObjectList(IEnumerable<string> values)
            {
                return values?.Cast<object>().ToList() ?? new List<object>();
            }

            private static string GetValue(Dictionary<string, object> values, string key,
                string defaultValue = "")
            {
                return values.TryGetValue(key, out var value) && value != null ? value.ToString() : defaultValue;
            }

            private static bool GetBoolean(Dictionary<string, object> values, string key)
            {
                return values.TryGetValue(key, out var value) && value != null && Convert.ToBoolean(value);
            }

            private static string[] GetArray(Dictionary<string, object> values, string key)
            {
                return values.TryGetValue(key, out var value) && value is List<object> list
                    ? list.Select(item => item?.ToString()).Where(item => !string.IsNullOrEmpty(item)).ToArray()
                    : Array.Empty<string>();
            }

            private static DateTime GetDateTime(Dictionary<string, object> values, string key)
            {
                return DateTime.TryParse(GetValue(values, key), out var result) ? result : DateTime.UtcNow;
            }
        }
    }
}

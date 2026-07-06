using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Script debugging helpers exposed through MCP.
    /// </summary>
    public static class MCPDebugCommands
    {
        private const string UnsupportedManagedDebuggerMessage =
            "Unity does not expose source breakpoint, paused stack frame, local variable, or code-step control through an in-process Editor API. " +
            "A full C# debugger must live outside the Unity process, e.g. a DAP or Mono soft-debugger client handled by the MCP server.";

        public static object AttachUnity(Dictionary<string, object> args)
        {
            bool openWindow = GetBool(args, "openWindow", false);
            bool waitForAttach = GetBool(args, "waitForAttach", false);
            int timeoutMs = Math.Max(0, GetInt(args, "timeoutMs", 0));

            if (openWindow)
                OpenManagedDebuggerWindow();

            double start = EditorApplication.timeSinceStartup;
            if (waitForAttach && timeoutMs > 0)
            {
                while (!IsManagedDebuggerAttached() &&
                       (EditorApplication.timeSinceStartup - start) * 1000d < timeoutMs)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            bool attached = IsManagedDebuggerAttached();

            return new Dictionary<string, object>
            {
                { "success", attached },
                { "attached", attached },
                { "managedDebuggerEnabled", IsManagedDebuggerEnabled() },
                { "processId", Process.GetCurrentProcess().Id },
                { "projectPath", GetProjectPath() },
                { "unityVersion", Application.unityVersion },
                { "capabilities", BuildCapabilities() },
                { "requiresExternalDebugger", !attached },
                { "message", attached
                    ? "A managed debugger is already attached to Unity."
                    : "Unity is ready for an external managed debugger, but this in-process plugin cannot attach one by itself." },
            };
        }

        public static object SetBreakpoint(Dictionary<string, object> args)
        {
            string file = GetString(args, "file");
            int line = GetInt(args, "line", -1);

            return Unsupported("setBreakpoint", new Dictionary<string, object>
            {
                { "file", file },
                { "line", line },
            });
        }

        public static object Continue(Dictionary<string, object> args)
        {
            bool wasPaused = EditorApplication.isPaused;
            EditorApplication.isPaused = false;

            return new Dictionary<string, object>
            {
                { "success", true },
                { "action", "continue" },
                { "wasEditorPaused", wasPaused },
                { "isEditorPaused", EditorApplication.isPaused },
                { "managedDebuggerAttached", IsManagedDebuggerAttached() },
                { "note", "This resumes Unity Play Mode pause. It cannot resume a source breakpoint hit by an external managed debugger." },
            };
        }

        public static object Pause(Dictionary<string, object> args)
        {
            bool breakPlayMode = GetBool(args, "breakPlayMode", true);
            bool wasPaused = EditorApplication.isPaused;

            EditorApplication.isPaused = true;
            if (breakPlayMode && EditorApplication.isPlaying)
                UnityEngine.Debug.Break();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "action", "pause" },
                { "wasEditorPaused", wasPaused },
                { "isEditorPaused", EditorApplication.isPaused },
                { "isPlaying", EditorApplication.isPlaying },
                { "managedDebuggerAttached", IsManagedDebuggerAttached() },
                { "note", "This pauses Unity Play Mode. It is not a source-level managed debugger break." },
            };
        }

        public static object StepOver(Dictionary<string, object> args)
        {
            if (GetBool(args, "stepFrame", false))
                return StepEditorFrame("stepOver");

            return Unsupported("stepOver");
        }

        public static object StepInto(Dictionary<string, object> args)
        {
            if (GetBool(args, "stepFrame", false))
                return StepEditorFrame("stepInto");

            return Unsupported("stepInto");
        }

        public static object StackTrace(Dictionary<string, object> args)
        {
            int skipFrames = Math.Max(0, GetInt(args, "skipFrames", 0));
            int maxFrames = Math.Max(1, GetInt(args, "maxFrames", 50));
            var trace = new StackTrace(skipFrames, true);
            var frames = new List<Dictionary<string, object>>();

            StackFrame[] stackFrames = trace.GetFrames() ?? Array.Empty<StackFrame>();
            for (int i = 0; i < stackFrames.Length && frames.Count < maxFrames; i++)
            {
                StackFrame frame = stackFrames[i];
                MethodBase method = frame.GetMethod();
                frames.Add(new Dictionary<string, object>
                {
                    { "frameId", i },
                    { "method", method != null ? FormatMethod(method) : "" },
                    { "file", frame.GetFileName() ?? "" },
                    { "line", frame.GetFileLineNumber() },
                    { "column", frame.GetFileColumnNumber() },
                });
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "mode", "mcpCallStack" },
                { "managedDebuggerAttached", IsManagedDebuggerAttached() },
                { "frames", frames },
                { "note", "This is the MCP request call stack, not a paused managed debugger stack frame." },
            };
        }

        public static object Variables(Dictionary<string, object> args)
        {
            int frameId = GetInt(args, "frameId", -1);
            return Unsupported("variables", new Dictionary<string, object>
            {
                { "frameId", frameId },
            });
        }

        public static object Evaluate(Dictionary<string, object> args)
        {
            string expression = GetString(args, "expression");
            string code = GetString(args, "code");

            if (string.IsNullOrWhiteSpace(code))
            {
                if (string.IsNullOrWhiteSpace(expression))
                    return new Dictionary<string, object> { { "error", "expression or code is required" } };

                code = expression.Contains("return ")
                    ? expression
                    : $"return {expression};";
            }

            var result = MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", code },
            });

            return new Dictionary<string, object>
            {
                { "success", !IsErrorResult(result) },
                { "mode", "editorContext" },
                { "result", result },
                { "note", "Expression is evaluated in the Unity Editor context, not inside a paused managed stack frame." },
            };
        }

        private static object StepEditorFrame(string action)
        {
            if (!EditorApplication.isPlaying)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Unity is not in Play Mode. Frame stepping requires Play Mode." },
                    { "action", action },
                };
            }

            EditorApplication.isPaused = true;
            EditorApplication.Step();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "action", action },
                { "stepped", "editorFrame" },
                { "isEditorPaused", EditorApplication.isPaused },
                { "note", "This advances one Unity frame. It is not source-level step-over or step-into." },
            };
        }

        private static Dictionary<string, object> Unsupported(string operation,
            Dictionary<string, object> extra = null)
        {
            var result = new Dictionary<string, object>
            {
                { "success", false },
                { "operation", operation },
                { "managedDebuggerAttached", IsManagedDebuggerAttached() },
                { "error", "unsupported_in_process_debugger_operation" },
                { "message", UnsupportedManagedDebuggerMessage },
                { "requiredArchitecture", "Handle this tool in the external MCP server process or a debugger helper process, then connect to Unity's managed debugger endpoint from there." },
            };

            if (extra != null)
            {
                foreach (var kvp in extra)
                    result[kvp.Key] = kvp.Value;
            }

            return result;
        }

        private static Dictionary<string, object> BuildCapabilities()
        {
            return new Dictionary<string, object>
            {
                { "managedDebuggerStatus", true },
                { "editorPauseContinue", true },
                { "editorFrameStep", true },
                { "editorContextEvaluate", true },
                { "mcpCallStackTrace", true },
                { "sourceBreakpoints", false },
                { "sourceStepOver", false },
                { "sourceStepInto", false },
                { "pausedFrameStackTrace", false },
                { "pausedFrameVariables", false },
                { "pausedFrameEvaluate", false },
            };
        }

        private static bool IsManagedDebuggerAttached()
        {
            return GetManagedDebuggerBool("isAttached");
        }

        private static bool IsManagedDebuggerEnabled()
        {
            return GetManagedDebuggerBool("isEnabled");
        }

        private static bool GetManagedDebuggerBool(string propertyName)
        {
            Type type = GetManagedDebuggerType();
            if (type == null)
                return false;

            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (property == null)
                return false;

            try
            {
                return property.GetValue(null) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static Type GetManagedDebuggerType()
        {
            return Type.GetType("UnityEditor.Scripting.ManagedDebugger, UnityEditor.CoreModule")
                   ?? Type.GetType("UnityEditor.Scripting.ManagedDebugger, UnityEditor");
        }

        private static void OpenManagedDebuggerWindow()
        {
            Type windowType = Type.GetType("UnityEditor.ManagedDebuggerWindow, UnityEditor.CoreModule")
                              ?? Type.GetType("UnityEditor.ManagedDebuggerWindow, UnityEditor");
            if (windowType != null)
                EditorWindow.GetWindow(windowType);
        }

        private static string FormatMethod(MethodBase method)
        {
            string declaringType = method.DeclaringType != null ? method.DeclaringType.FullName : "";
            return string.IsNullOrEmpty(declaringType)
                ? method.Name
                : declaringType + "." + method.Name;
        }

        private static bool IsErrorResult(object result)
        {
            if (result is Dictionary<string, object> dict)
                return dict.ContainsKey("error");

            if (result != null)
            {
                PropertyInfo property = result.GetType().GetProperty("error",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                    return true;
            }

            return result == null;
        }

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            return dataPath.EndsWith("/Assets", StringComparison.Ordinal)
                ? dataPath.Substring(0, dataPath.Length - "/Assets".Length)
                : dataPath;
        }

        private static string GetString(Dictionary<string, object> args, string key, string defaultValue = "")
        {
            return args != null && args.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            try { return Convert.ToInt32(value); }
            catch { return defaultValue; }
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            try { return Convert.ToBoolean(value); }
            catch { return defaultValue; }
        }
    }
}

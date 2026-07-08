using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityMCP.Editor
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MCPProjectToolAttribute : Attribute
    {
        public string ToolName { get; }

        public string Description { get; set; }

        public string InputSchemaJson { get; set; }

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
        public const string DirectRoutePrefix = "project-tools/call/";

        public static object List(Dictionary<string, object> args)
        {
            var tools = GetToolDictionaries(false);

            return new Dictionary<string, object>
            {
                { "tools", tools },
                { "totalTools", tools.Count }
            };
        }

        public static List<Dictionary<string, object>> GetToolDictionaries(bool validOnly)
        {
            return DiscoverTools()
                .Where(tool => validOnly == false || string.IsNullOrEmpty(tool.ValidationError))
                .OrderBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(tool => tool.ToDictionary())
                .ToList();
        }

        public static List<string> GetDirectRoutePaths()
        {
            return DiscoverTools()
                .Where(tool => string.IsNullOrEmpty(tool.ValidationError))
                .OrderBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
                .Select(tool => GetDirectRoute(tool.ToolName))
                .ToList();
        }

        public static bool TryGetToolDictionaryForDirectRoute(string path, out Dictionary<string, object> tool)
        {
            tool = null;

            if (TryGetToolNameFromDirectRoute(path, out var toolName) == false)
                return false;

            var descriptor = FindTool(toolName);
            if (descriptor == null || string.IsNullOrEmpty(descriptor.ValidationError) == false)
                return false;

            tool = descriptor.ToDictionary();
            return true;
        }

        public static bool TryExecuteDirectRoute(string path, Dictionary<string, object> args, out object result)
        {
            result = null;

            if (TryGetToolNameFromDirectRoute(path, out var toolName) == false)
                return false;

            if (string.IsNullOrEmpty(toolName))
            {
                result = new { error = "Project tool route is missing a tool name." };
                return true;
            }

            result = ExecuteTool(toolName, args ?? new Dictionary<string, object>());
            return true;
        }

        public static string GetDirectRoute(string toolName)
        {
            return DirectRoutePrefix + (toolName ?? "").TrimStart('/');
        }

        public static object Execute(Dictionary<string, object> args)
        {
            string toolName = GetString(args, "toolName");
            if (string.IsNullOrEmpty(toolName))
                toolName = GetString(args, "name");

            if (string.IsNullOrEmpty(toolName))
                return new { error = "toolName is required" };

            var toolArgs = GetDictionary(args, "args")
                ?? GetDictionary(args, "arguments")
                ?? GetDictionary(args, "parameters")
                ?? new Dictionary<string, object>();

            return ExecuteTool(toolName, toolArgs);
        }

        private static object ExecuteTool(string toolName, Dictionary<string, object> toolArgs)
        {
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

        private static ProjectToolDescriptor FindTool(string toolName)
        {
            return DiscoverTools()
                .FirstOrDefault(tool => string.Equals(tool.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetToolNameFromDirectRoute(string path, out string toolName)
        {
            toolName = null;

            if (string.IsNullOrEmpty(path) || path.StartsWith(DirectRoutePrefix, StringComparison.Ordinal) == false)
                return false;

            var encodedToolName = path.Substring(DirectRoutePrefix.Length);
            toolName = Uri.UnescapeDataString(encodedToolName);
            return true;
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
            public Dictionary<string, object> InputSchema;

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
                descriptor.ValidationError = CombineValidationErrors(descriptor.ValidationError,
                    descriptor.TrySetInputSchema(attribute.InputSchemaJson));
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
                descriptor.ValidationError = CombineValidationErrors(descriptor.ValidationError,
                    descriptor.TrySetInputSchema(attribute.InputSchemaJson));
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
                    { "route", GetDirectRoute(ToolName) },
                    { "inputSchema", InputSchema ?? CreateDefaultInputSchema() },
                    { "valid", string.IsNullOrEmpty(ValidationError) },
                    { "validationError", ValidationError ?? "" }
                };
            }

            private string TrySetInputSchema(string inputSchemaJson)
            {
                if (string.IsNullOrEmpty(inputSchemaJson))
                {
                    InputSchema = CreateDefaultInputSchema();
                    return null;
                }

                try
                {
                    InputSchema = MiniJson.Deserialize(inputSchemaJson) as Dictionary<string, object>;
                    return InputSchema == null ? "InputSchemaJson must deserialize to a JSON object." : null;
                }
                catch (Exception ex)
                {
                    InputSchema = CreateDefaultInputSchema();
                    return $"InputSchemaJson is invalid JSON: {ex.Message}";
                }
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

            private static Dictionary<string, object> CreateDefaultInputSchema()
            {
                return new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", new Dictionary<string, object>() },
                    { "additionalProperties", true }
                };
            }

            private static string CombineValidationErrors(string first, string second)
            {
                if (string.IsNullOrEmpty(first))
                    return second;

                if (string.IsNullOrEmpty(second))
                    return first;

                return first + " " + second;
            }
        }
    }
}

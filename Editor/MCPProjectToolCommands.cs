using System;
using System.Collections;
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

        public string ShortName { get; set; }

        public string InputSchemaJson { get; set; }

        public bool ReadOnly { get; set; }

        public bool MutatesAssets { get; set; }

        public bool MutatesRuntime { get; set; }

        public bool Dangerous { get; set; }

        public bool LongRunning { get; set; }

        public bool MayReloadDomain { get; set; }

        public bool RequiresPlayMode { get; set; }

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
                return MCPResponse.Error("toolName is required", "invalid_arguments");

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
                return MCPResponse.Error($"Project tool '{toolName}' was not found.", "project_tool_not_found",
                    false, new Dictionary<string, object>
                    {
                        { "availableTools", DiscoverTools().Select(tool => tool.ToolName).OrderBy(name => name).ToList() }
                    });
            }

            if (matches.Count > 1)
            {
                return MCPResponse.Error($"Project tool '{toolName}' is registered more than once.",
                    "duplicate_project_tool", false, new Dictionary<string, object>
                    {
                        { "matches", matches.Select(tool => tool.ToDictionary()).ToList() }
                    });
            }

            var descriptor = matches[0];
            if (!string.IsNullOrEmpty(descriptor.ValidationError))
                return MCPResponse.Error(descriptor.ValidationError, "invalid_project_tool", false,
                    new Dictionary<string, object> { { "tool", descriptor.ToDictionary() } });

            if (!descriptor.TryValidateArguments(toolArgs, out var argumentError))
            {
                return MCPResponse.Error(argumentError, "invalid_arguments", false,
                    new Dictionary<string, object> { { "toolName", descriptor.ToolName } });
            }

            try
            {
                object result = descriptor.Invoke(toolArgs);
                return MCPResponse.Success(result, new Dictionary<string, object>
                {
                    { "toolName", descriptor.ToolName },
                });
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return MCPResponse.Error(inner.Message, "project_tool_exception", false,
                    new Dictionary<string, object>
                    {
                        { "stackTrace", inner.StackTrace },
                        { "toolName", descriptor.ToolName }
                    });
            }
            catch (Exception ex)
            {
                return MCPResponse.Error(ex.Message, "project_tool_exception", false,
                    new Dictionary<string, object>
                    {
                        { "stackTrace", ex.StackTrace },
                        { "toolName", descriptor.ToolName }
                    });
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
            public string ShortName;
            public string Source;
            public string ValidationError;
            public Dictionary<string, object> InputSchema;
            public bool ReadOnly;
            public bool MutatesAssets;
            public bool MutatesRuntime;
            public bool Dangerous;
            public bool LongRunning;
            public bool MayReloadDomain;
            public bool RequiresPlayMode;

            private MethodInfo method;
            private Type type;

            public static ProjectToolDescriptor FromMethod(MCPProjectToolAttribute attribute, MethodInfo method)
            {
                var descriptor = new ProjectToolDescriptor
                {
                    ToolName = attribute.ToolName,
                    ShortName = attribute.ShortName ?? "",
                    Description = attribute.Description ?? "",
                    Source = method.DeclaringType.FullName + "." + method.Name,
                    ReadOnly = attribute.ReadOnly,
                    MutatesAssets = attribute.MutatesAssets,
                    MutatesRuntime = attribute.MutatesRuntime,
                    Dangerous = attribute.Dangerous,
                    LongRunning = attribute.LongRunning,
                    MayReloadDomain = attribute.MayReloadDomain,
                    RequiresPlayMode = attribute.RequiresPlayMode,
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
                    ShortName = attribute.ShortName ?? "",
                    Description = attribute.Description ?? "",
                    Source = type.FullName,
                    ReadOnly = attribute.ReadOnly,
                    MutatesAssets = attribute.MutatesAssets,
                    MutatesRuntime = attribute.MutatesRuntime,
                    Dangerous = attribute.Dangerous,
                    LongRunning = attribute.LongRunning,
                    MayReloadDomain = attribute.MayReloadDomain,
                    RequiresPlayMode = attribute.RequiresPlayMode,
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
                    { "shortName", ShortName },
                    { "description", Description },
                    { "source", Source },
                    { "route", GetDirectRoute(ToolName) },
                    { "inputSchema", InputSchema ?? CreateDefaultInputSchema() },
                    { "readOnly", ReadOnly },
                    { "mutatesAssets", MutatesAssets },
                    { "mutatesRuntime", MutatesRuntime },
                    { "dangerous", Dangerous },
                    { "longRunning", LongRunning },
                    { "mayReloadDomain", MayReloadDomain },
                    { "requiresPlayMode", RequiresPlayMode },
                    { "enforcesInputSchema", true },
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
                    if (InputSchema == null)
                        return "InputSchemaJson must deserialize to a JSON object.";

                    return ValidateInputSchema(InputSchema);
                }
                catch (Exception ex)
                {
                    InputSchema = CreateDefaultInputSchema();
                    return $"InputSchemaJson is invalid JSON: {ex.Message}";
                }
            }

            public bool TryValidateArguments(Dictionary<string, object> args, out string error)
            {
                error = null;
                args = args ?? new Dictionary<string, object>();
                var schema = InputSchema ?? CreateDefaultInputSchema();
                var properties = GetSchemaProperties(schema);
                var required = GetRequiredProperties(schema);
                var errors = new List<string>();

                foreach (string requiredName in required)
                {
                    if (!args.ContainsKey(requiredName) || args[requiredName] == null)
                        errors.Add($"Missing required argument '{requiredName}'.");
                }

                if (IsAdditionalPropertiesFalse(schema))
                {
                    foreach (string key in args.Keys)
                    {
                        if (!properties.ContainsKey(key))
                            errors.Add($"Unknown argument '{key}'.");
                    }
                }

                foreach (var pair in args)
                {
                    if (!properties.TryGetValue(pair.Key, out var propertySchemaObj))
                        continue;

                    var propertySchema = propertySchemaObj as Dictionary<string, object>;
                    if (propertySchema == null)
                        continue;

                    if (!MatchesSchemaType(pair.Value, propertySchema, out var typeError))
                        errors.Add($"Argument '{pair.Key}' {typeError}");
                }

                if (errors.Count == 0)
                    return true;

                error = string.Join(" ", errors);
                return false;
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

            private static string ValidateInputSchema(Dictionary<string, object> schema)
            {
                if (schema.TryGetValue("type", out var type) && type != null &&
                    type.ToString() != "object")
                    return "InputSchemaJson root type must be object.";

                var properties = GetSchemaProperties(schema);
                if (properties == null)
                    return "InputSchemaJson properties must be a JSON object.";

                var required = GetRequiredProperties(schema);
                foreach (string requiredName in required)
                {
                    if (!properties.ContainsKey(requiredName))
                        return $"InputSchemaJson required property '{requiredName}' is not declared in properties.";
                }

                return null;
            }

            private static Dictionary<string, object> GetSchemaProperties(Dictionary<string, object> schema)
            {
                if (!schema.TryGetValue("properties", out var propertiesObj) || propertiesObj == null)
                    return new Dictionary<string, object>();

                return propertiesObj as Dictionary<string, object>;
            }

            private static List<string> GetRequiredProperties(Dictionary<string, object> schema)
            {
                if (!schema.TryGetValue("required", out var requiredObj) || requiredObj == null)
                    return new List<string>();

                var list = requiredObj as IList;
                if (list == null)
                    return new List<string>();

                return list.Cast<object>()
                    .Where(item => item != null)
                    .Select(item => item.ToString())
                    .Where(item => !string.IsNullOrEmpty(item))
                    .ToList();
            }

            private static bool IsAdditionalPropertiesFalse(Dictionary<string, object> schema)
            {
                return schema.TryGetValue("additionalProperties", out var value) &&
                       value is bool boolValue &&
                       boolValue == false;
            }

            private static bool MatchesSchemaType(object value, Dictionary<string, object> propertySchema,
                out string error)
            {
                error = null;
                if (!propertySchema.TryGetValue("type", out var typeObj) || typeObj == null || value == null)
                    return true;

                var allowedTypes = GetAllowedTypes(typeObj);
                if (allowedTypes.Count == 0)
                    return true;

                foreach (string allowedType in allowedTypes)
                {
                    if (MatchesType(value, allowedType))
                        return true;
                }

                error = $"must be {string.Join(" or ", allowedTypes)}.";
                return false;
            }

            private static List<string> GetAllowedTypes(object typeObj)
            {
                if (typeObj is string typeString)
                    return new List<string> { typeString };

                var list = typeObj as IList;
                if (list == null)
                    return new List<string>();

                return list.Cast<object>()
                    .Where(item => item != null)
                    .Select(item => item.ToString())
                    .Where(item => !string.IsNullOrEmpty(item))
                    .ToList();
            }

            private static bool MatchesType(object value, string type)
            {
                switch (type)
                {
                    case "string":
                        return value is string;
                    case "number":
                        return IsNumber(value);
                    case "integer":
                        return value is byte || value is sbyte || value is short || value is ushort ||
                               value is int || value is uint || value is long || value is ulong;
                    case "boolean":
                        return value is bool;
                    case "object":
                        return value is IDictionary;
                    case "array":
                        return value is IList && !(value is string);
                    case "null":
                        return value == null;
                    default:
                        return true;
                }
            }

            private static bool IsNumber(object value)
            {
                return value is byte || value is sbyte || value is short || value is ushort ||
                       value is int || value is uint || value is long || value is ulong ||
                       value is float || value is double || value is decimal;
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

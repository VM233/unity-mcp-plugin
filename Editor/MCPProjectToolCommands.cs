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
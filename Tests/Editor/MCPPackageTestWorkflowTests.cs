using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityMCP.Editor.Tests
{
    public sealed class MCPPackageTestWorkflowTests
    {
        [Test]
        public void CompilationErrors_AreReportedWhileWaitingForAssemblies()
        {
            MethodInfo method = typeof(MCPPackageTestCommands).GetMethod("TryBuildCompilationFailure",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var result = new Dictionary<string, object>
            {
                { "entries", new List<Dictionary<string, object>>
                    {
                        new()
                        {
                            { "severity", "warning" },
                            { "assembly", "Unrelated.Tests" },
                            { "message", "warning" },
                        },
                        new()
                        {
                            { "severity", "error" },
                            { "assembly", "Broken.Package.Tests" },
                            { "file", "Tests/BrokenTest.cs" },
                            { "message", "CS1002: ; expected" },
                        },
                    }
                },
            };
            object[] arguments = { result, null };

            bool failed = (bool)method.Invoke(null, arguments);

            Assert.That(failed, Is.True);
            Assert.That(arguments[1], Does.Contain("Package test assemblies failed to compile"));
            Assert.That(arguments[1], Does.Contain("Broken.Package.Tests"));
            Assert.That(arguments[1], Does.Contain("CS1002"));
            Assert.That(arguments[1], Does.Not.Contain("warning"));
        }

        [Test]
        public void NoCompilationErrors_KeepsWaitingForAssemblies()
        {
            MethodInfo method = typeof(MCPPackageTestCommands).GetMethod("TryBuildCompilationFailure",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var result = new Dictionary<string, object>
            {
                { "entries", new List<Dictionary<string, object>>() },
            };
            object[] arguments = { result, null };

            bool failed = (bool)method.Invoke(null, arguments);

            Assert.That(failed, Is.False);
            Assert.That(arguments[1], Is.Null);
        }
    }
}

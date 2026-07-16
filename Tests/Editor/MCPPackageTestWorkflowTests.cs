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

        [Test]
        public void CompiledTestAssembly_IsReadyWithoutLoadingIntoDefaultAppDomain()
        {
            MethodInfo method = typeof(MCPPackageTestCommands).GetMethod(
                "AreRequestedAssembliesAvailable", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            bool available = (bool)method.Invoke(null, new object[]
            {
                new[] { "Example.Package.Editor.Tests" },
                Array.Empty<string>(),
                new[] { "Example.Package.Editor.Tests" },
            });

            Assert.That(available, Is.True,
                "Unity Test Runner assemblies may be compiled but intentionally skipped by the default AppDomain.");
        }

        [Test]
        public void MissingRequestedTestAssembly_RemainsUnavailable()
        {
            MethodInfo method = typeof(MCPPackageTestCommands).GetMethod(
                "AreRequestedAssembliesAvailable", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            bool available = (bool)method.Invoke(null, new object[]
            {
                new[] { "Missing.Package.Tests" },
                new[] { "Unrelated.Loaded" },
                new[] { "Unrelated.Compiled" },
            });

            Assert.That(available, Is.False);
        }

        [Test]
        public void ActiveWorkflow_BlocksConcurrentManifestMutation()
        {
            FieldInfo workflowField = typeof(MCPPackageTestCommands).GetField("_workflow",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo method = typeof(MCPPackageTestCommands).GetMethod("TryGetActiveWorkflow",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(workflowField, Is.Not.Null);
            Assert.That(method, Is.Not.Null);

            object original = workflowField.GetValue(null);
            object active = Activator.CreateInstance(workflowField.FieldType, true);
            workflowField.FieldType.GetField("WorkflowId")?.SetValue(active, "workflow-123");
            workflowField.FieldType.GetField("PackageName")?.SetValue(active, "com.example.tests");
            workflowField.FieldType.GetField("State")?.SetValue(active, "running");

            try
            {
                workflowField.SetValue(null, active);
                object[] arguments = { null, null, null };

                bool hasActiveWorkflow = (bool)method.Invoke(null, arguments);

                Assert.That(hasActiveWorkflow, Is.True);
                Assert.That(arguments[0], Is.EqualTo("workflow-123"));
                Assert.That(arguments[1], Is.EqualTo("com.example.tests"));
                Assert.That(arguments[2], Is.EqualTo("running"));
            }
            finally
            {
                workflowField.SetValue(null, original);
            }
        }

        [Test]
        public void ExplicitFilters_WithNoMatchedTests_FailInsteadOfReportingSuccess()
        {
            Type jobType = typeof(MCPTestRunnerCommands).GetNestedType("TestJob",
                BindingFlags.NonPublic);
            MethodInfo finalize = typeof(MCPTestRunnerCommands).GetMethod("FinalizeJob",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null);
            Assert.That(finalize, Is.Not.Null);

            object job = Activator.CreateInstance(jobType, true);
            jobType.GetField("JobId")?.SetValue(job, "zero-match-job");
            jobType.GetField("HasExplicitFilters")?.SetValue(job, true);
            jobType.GetField("TotalTests")?.SetValue(job, 0);
            jobType.GetField("FailedCount")?.SetValue(job, 0);

            finalize.Invoke(null, new[] { job, (object)0d, false });

            Assert.That(jobType.GetField("Status")?.GetValue(job)?.ToString(), Is.EqualTo("Failed"));
            Assert.That(jobType.GetField("ErrorCode")?.GetValue(job), Is.EqualTo("no_tests_matched"));
            Assert.That(jobType.GetField("Error")?.GetValue(job)?.ToString(),
                Does.Contain("No tests matched"));
        }
    }
}

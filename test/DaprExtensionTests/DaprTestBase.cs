﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace DaprExtensionTests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using DaprExtensionTests.Logging;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Extensions.Dapr;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Xunit;
    using Xunit.Abstractions;

    // TODO: Instead of making all tests run sequentially, configure a different port number for each collection
    [Collection("Sequential")]
    public abstract class DaprTestBase : IDisposable, IAsyncLifetime
    {
        static readonly HttpClient HttpClient = new HttpClient()
        {
            Timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10),
        };

        readonly IHost functionsHost;
        readonly DaprRuntimeEmulator daprRuntime;

        readonly TestLogProvider logProvider;
        readonly TestFunctionTypeLocator typeLocator;
        readonly TestNameResolver nameResolver;

        public DaprTestBase(ITestOutputHelper output)
        {
            this.logProvider = new TestLogProvider(output);
            this.typeLocator = new TestFunctionTypeLocator();
            this.nameResolver = new TestNameResolver();

            // Use 3501 to avoid conflicting with the real Dapr port number
            int daprPort = 3501;
            this.nameResolver.AddSetting("DAPR_HTTP_PORT", daprPort.ToString());

            this.functionsHost = new HostBuilder()
                .ConfigureLogging(loggingBuilder => loggingBuilder.AddProvider(this.logProvider))
                .ConfigureWebJobs(webJobsBuilder => webJobsBuilder.AddDapr())
                .ConfigureServices(
                    collection =>
                    {
                        collection.AddSingleton<INameResolver>(this.nameResolver);
                        collection.AddSingleton<ITypeLocator>(this.typeLocator);
                    })
                .Build();
            this.daprRuntime = new DaprRuntimeEmulator(daprPort);
        }

        public virtual void Dispose()
        {
            this.functionsHost.Dispose();
            this.daprRuntime.Dispose();
        }

        internal void AddFunctions(Type functionType) => this.typeLocator.AddFunctionType(functionType);

        internal IEnumerable<string> GetExtensionLogs()
        {
            return this.GetLogs("Host.Triggers.Dapr");
        }

        internal IEnumerable<string> GetFunctionLogs(string functionName)
        {
            return this.GetLogs($"Function.{functionName}.User");
        }

        internal IEnumerable<string> GetLogs(string category)
        {
            bool loggerExists = this.logProvider.TryGetLogs(category, out IEnumerable<LogEntry> logs);
            Assert.True(loggerExists, $"No logger was found for '{category}'.");

            return logs.Select(entry => entry.Message).ToArray();
        }

        internal async Task<HttpResponseMessage> SendRequestAsync(
            HttpMethod method,
            string url,
            object? jsonContent = null)
        {
            using var request = new HttpRequestMessage(method, url);

            if (jsonContent != null)
            {
                string json = JsonConvert.SerializeObject(jsonContent);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return await HttpClient.SendAsync(request);
        }

        internal Task CallFunctionAsync(string functionName, string parameterName, object? argument)
        {
            return this.CallFunctionAsync(
                functionName,
                new Dictionary<string, object?>()
                {
                    { parameterName, argument },
                });
        }

        internal Task CallFunctionAsync(string name, IDictionary<string, object?>? args = null)
        {
            IJobHost jobHost = this.functionsHost.Services.GetService<IJobHost>();
            return jobHost.CallAsync(name, args);
        }

        internal SavedHttpRequest[] GetDaprRequests() => this.daprRuntime.GetReceivedRequests();

        Task IAsyncLifetime.InitializeAsync()
        {
            return Task.WhenAll(
                this.daprRuntime.StartAsync(),
                this.functionsHost.StartAsync());
        }

        Task IAsyncLifetime.DisposeAsync()
        {
            return Task.WhenAll(
                this.daprRuntime.StopAsync(),
                this.functionsHost.StopAsync());
        }

        class TestFunctionTypeLocator : ITypeLocator
        {
            readonly List<Type> functionTypes = new List<Type>();

            public void AddFunctionType(Type functionType) => this.functionTypes.Add(functionType);

            IReadOnlyList<Type> ITypeLocator.GetTypes() => this.functionTypes.AsReadOnly();
        }

        public class TestNameResolver : INameResolver
        {
            readonly Dictionary<string, string> testSettings = 
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void AddSetting(string name, string value) => 
                this.testSettings.Add(name, value);

            string? INameResolver.Resolve(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                if (this.testSettings.TryGetValue(name, out string? value))
                {
                    return value;
                }

                return Environment.GetEnvironmentVariable(name);
            }
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Elastic.Clients.Elasticsearch.Tests.Utils;
using Json.Schema;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Components.ConformanceTests;

public abstract class ConformanceTestsBase<TService, TOptions>
    where TService : class
    where TOptions : class, new()
{
    protected static readonly EvaluationOptions DefaultEvaluationOptions = new() { RequireFormatValidation = true, OutputFormat = OutputFormat.List };

    protected abstract ServiceLifetime ServiceLifetime { get; }

    protected abstract string ActivitySourceName { get; }

    protected string JsonSchemaPath => Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json");

    protected virtual string ValidJsonConfig { get; } = string.Empty;

    protected virtual (string json, string error)[] InvalidJsonToErrorMessage => Array.Empty<(string json, string error)>();

    protected abstract string[] RequiredLogCategories { get; }

    protected virtual string[] NotAcceptableLogCategories => Array.Empty<string>();

    protected virtual bool CanCreateClientWithoutConnectingToServer => true;

    protected virtual bool CanConnectToServer => false;

    protected virtual bool SupportsNamedConfig => true;
    protected virtual string? ConfigurationSectionName => null;

    protected virtual bool SupportsKeyedRegistrations => false;

    protected bool MetricsAreSupported => CheckIfImplemented(SetMetrics);

    // every Component has to support health checks, this property is a temporary workaround
    protected bool HealthChecksAreSupported => CheckIfImplemented(SetHealthCheck);

    protected virtual void DisableRetries(TOptions options) { }

    protected bool TracingIsSupported => CheckIfImplemented(SetTracing);

    protected virtual bool CheckOptionClassSealed => true;

    /// <summary>
    /// Calls the actual Component
    /// </summary>
    protected abstract void RegisterComponent(HostApplicationBuilder builder, Action<TOptions>? configure = null, string? key = null);

    /// <summary>
    /// Populates the Configuration with everything that is required by the Component
    /// </summary>
    /// <param name="configuration"></param>
    protected abstract void PopulateConfiguration(ConfigurationManager configuration, string? key = null);

    /// <summary>
    /// Do anything that is going to trigger the <see cref="Activity"/> and <see cref="ILogger"/> creation. Example: try to create a DB.
    /// </summary>
    protected abstract void TriggerActivity(TService service);

    /// <summary>
    /// Sets the health checks to given value
    /// </summary>
    protected abstract void SetHealthCheck(TOptions options, bool enabled);

    /// <summary>
    /// Sets the tracing to given value
    /// </summary>
    protected abstract void SetTracing(TOptions options, bool enabled);

    /// <summary>
    /// Sets the metrics to given value
    /// </summary>
    protected abstract void SetMetrics(TOptions options, bool enabled);

    [ConditionalFact]
    public void OptionsTypeIsSealed()
    {
        if (typeof(TOptions) == typeof(object))
        {
            throw new SkipTestException("Not implemented yet");
        }

        if (!CheckOptionClassSealed)
        {
            throw new SkipTestException("Opt-out of test");
        }

        Assert.True(typeof(TOptions).IsSealed);
    }

    [ConditionalTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void HealthChecksRegistersHealthCheckService(bool enabled)
    {
        SkipIfHealthChecksAreNotSupported();

        using IHost host = CreateHostWithComponent(options => SetHealthCheck(options, enabled));

        HealthCheckService? healthCheckService = host.Services.GetService<HealthCheckService>();

        Assert.Equal(enabled, healthCheckService is not null);
    }

    [ConditionalFact]
    public async Task EachKeyedComponentRegistersItsOwnHealthCheck()
    {
        SkipIfHealthChecksAreNotSupported();
        SkipIfKeyedRegistrationIsNotSupported();

        const string key1 = "key1", key2 = "key2";

        using IHost host = CreateHostWithMultipleKeyedComponents(key1, key2);

        HealthCheckService healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        List<string> registeredNames = new();
        await healthCheckService.CheckHealthAsync(healthCheckRegistration =>
        {
            registeredNames.Add(healthCheckRegistration.Name);
            return false;
        }).ConfigureAwait(false);

        Assert.Equal(2, registeredNames.Count);
        Assert.All(registeredNames, name => Assert.True(name.Contains(key1) || name.Contains(key2), $"{name} did not contain the key."));
    }

    [ConditionalTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TracingRegistersTraceProvider(bool enabled)
    {
        SkipIfTracingIsNotSupported();
        SkipIfRequiredServerConnectionCanNotBeEstablished();

        using IHost host = CreateHostWithComponent(options => SetTracing(options, enabled));

        TracerProvider? tracer = host.Services.GetService<TracerProvider>();

        Assert.Equal(enabled, tracer is not null);
    }

    [ConditionalTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void MetricsRegistersMeterProvider(bool enabled)
    {
        SkipIfMetricsAreNotSupported();

        using IHost host = CreateHostWithComponent(options => SetMetrics(options, enabled));

        MeterProvider? meter = host.Services.GetService<MeterProvider>();

        Assert.Equal(enabled, meter is not null);
    }

    [ConditionalTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceLifetimeIsAsExpected(bool useKey)
    {
        SkipIfRequiredServerConnectionCanNotBeEstablished();
        SkipIfKeyedRegistrationIsNotSupported(useKey);

        TService? serviceFromFirstScope, serviceFromSecondScope, secondServiceFromSecondScope;
        string? key = useKey ? "key" : null;

        using IHost host = CreateHostWithComponent(key: key);

        using (IServiceScope scope1 = host.Services.CreateScope())
        {
            serviceFromFirstScope = Resolve(scope1.ServiceProvider, key);
        }

        using (IServiceScope scope2 = host.Services.CreateScope())
        {
            serviceFromSecondScope = Resolve(scope2.ServiceProvider, key);

            secondServiceFromSecondScope = Resolve(scope2.ServiceProvider, key);
        }

        Assert.NotNull(serviceFromFirstScope);
        Assert.NotNull(serviceFromSecondScope);
        Assert.NotNull(secondServiceFromSecondScope);

        switch (ServiceLifetime)
        {
            case ServiceLifetime.Singleton:
                Assert.Same(serviceFromFirstScope, serviceFromSecondScope);
                Assert.Same(serviceFromSecondScope, secondServiceFromSecondScope);
                break;
            case ServiceLifetime.Scoped:
                Assert.NotSame(serviceFromFirstScope, serviceFromSecondScope);
                Assert.Same(serviceFromSecondScope, secondServiceFromSecondScope);
                break;
            case ServiceLifetime.Transient:
                Assert.NotSame(serviceFromFirstScope, serviceFromSecondScope);
                Assert.NotSame(serviceFromSecondScope, secondServiceFromSecondScope);
                break;
        }

        static TService? Resolve(IServiceProvider serviceProvider, string? key)
            => string.IsNullOrEmpty(key)
                ? serviceProvider.GetService<TService>()
                : serviceProvider.GetKeyedService<TService>(key);
    }

    [ConditionalFact]
    public void CanRegisterMultipleInstancesUsingDifferentKeys()
    {
        SkipIfKeyedRegistrationIsNotSupported();
        SkipIfRequiredServerConnectionCanNotBeEstablished();

        const string key1 = "key1", key2 = "key2";

        using IHost host = CreateHostWithMultipleKeyedComponents(key1, key2);

        TService serviceForKey1 = host.Services.GetRequiredKeyedService<TService>(key1);
        TService serviceForKey2 = host.Services.GetRequiredKeyedService<TService>(key2);

        Assert.NotSame(serviceForKey1, serviceForKey2);
    }

    [ConditionalFact]
    public void WhenKeyedRegistrationIsUsedThenItsImpossibleToResolveWithoutKey()
    {
        SkipIfKeyedRegistrationIsNotSupported();
        SkipIfRequiredServerConnectionCanNotBeEstablished();

        const string key = "key";

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(settings: null);

        PopulateConfiguration(builder.Configuration, key);
        RegisterComponent(builder, key: key);

        using IHost host = builder.Build();

        Assert.NotNull(host.Services.GetKeyedService<TService>(key));
        Assert.Null(host.Services.GetService<TService>());
        Assert.Throws<InvalidOperationException>(host.Services.GetRequiredService<TService>);
    }

    [ConditionalTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void LoggerFactoryIsUsedByRegisteredClient(bool registerAfterLoggerFactory, bool useKey)
    {
        SkipIfRequiredServerConnectionCanNotBeEstablished();
        SkipIfKeyedRegistrationIsNotSupported(useKey);

        string? key = useKey ? "key" : null;
        HostApplicationBuilder builder = CreateHostBuilder(key: key);

        if (registerAfterLoggerFactory)
        {
            builder.Services.AddSingleton<ILoggerFactory, TestLoggerFactory>();
            RegisterComponent(builder, key: key);
        }
        else
        {
            // the Component should be lazily created when it's requested for the first time!
            RegisterComponent(builder, key: key);
            builder.Services.AddSingleton<ILoggerFactory, TestLoggerFactory>();
        }

        using IHost host = builder.Build();

        TService service = key is null
            ? host.Services.GetRequiredService<TService>()
            : host.Services.GetRequiredKeyedService<TService>(key);
        TestLoggerFactory loggerFactory = (TestLoggerFactory)host.Services.GetRequiredService<ILoggerFactory>();

        try
        {
            TriggerActivity(service);
        }
        catch (Exception) { }

        foreach (string logCategory in RequiredLogCategories)
        {
            Assert.Contains(logCategory, loggerFactory.Categories);
        }

        foreach (string logCategory in NotAcceptableLogCategories)
        {
            Assert.DoesNotContain(logCategory, loggerFactory.Categories);
        }
    }

    [ConditionalTheory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task HealthCheckReportsExpectedStatus(string? key)
    {
        SkipIfHealthChecksAreNotSupported();

        // DisableRetries so the test doesn't take so long retrying when the server isn't available.
        using IHost host = CreateHostWithComponent(configureComponent: DisableRetries, key: key);

        HealthCheckService healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        HealthReport healthReport = await healthCheckService.CheckHealthAsync().ConfigureAwait(false);

        HealthStatus expected = CanConnectToServer ? HealthStatus.Healthy : HealthStatus.Unhealthy;

        Assert.Equal(expected, healthReport.Status);
        Assert.NotEmpty(healthReport.Entries);
        Assert.Contains(healthReport.Entries, entry => entry.Value.Status == expected);
    }

    [Fact]
    public void ConfigurationSchemaValidJsonConfigTest()
    {
        var schema = JsonSchema.FromFile(JsonSchemaPath);
        var config = JsonNode.Parse(ValidJsonConfig);

        var results = schema.Evaluate(config);

        Assert.True(results.IsValid);
    }

    [Fact]
    public void ConfigurationSchemaInvalidJsonConfigTest()
    {
        var schema = JsonSchema.FromFile(JsonSchemaPath);

        foreach ((string json, string error) in InvalidJsonToErrorMessage)
        {
            var config = JsonNode.Parse(json);
            var results = schema.Evaluate(config, DefaultEvaluationOptions);
            var detail = results.Details.FirstOrDefault(x => x.HasErrors);

            Assert.NotNull(detail);
            Assert.Equal(error, detail.Errors!.First().Value);
        }
    }

    /// <summary>
    /// Ensures that when the connection information is missing, an exception isn't thrown before the host
    /// is built, so any exception can be logged with ILogger.
    /// </summary>
    [ConditionalTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionInformationIsDelayValidated(bool useKey)
    {
        SetupConnectionInformationIsDelayValidated();

        var builder = Host.CreateEmptyApplicationBuilder(null);

        string? key = useKey ? "key" : null;
        RegisterComponent(builder, key: key);

        using var host = builder.Build();

        Assert.Throws<InvalidOperationException>(() =>
            key is null
                ? host.Services.GetRequiredService<TService>()
                : host.Services.GetRequiredKeyedService<TService>(key));
    }

    [ConditionalFact]
    public void FavorsNamedConfigurationOverTopLevelConfigurationWhenBothProvided_DisableTracing()
    {
        SkipIfNamedConfigNotSupported();
        SkipIfTracingIsNotSupported();

        var key = "target-service";
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"{ConfigurationSectionName}:DisableTracing", "false"),
            new KeyValuePair<string, string?>($"{ConfigurationSectionName}:{key}:DisableTracing", "true"),
        ]);

        RegisterComponent(builder, key: key);

        using var host = builder.Build();

        // Trace provider is not configured because DisableTracing is set to true in the named configuration
        Assert.Null(host.Services.GetService<TracerProvider>());
    }

    [ConditionalFact]
    public void FavorsNamedConfigurationOverTopLevelConfigurationWhenBothProvided_DisableHealthChecks()
    {
        SkipIfNamedConfigNotSupported();
        SkipIfHealthChecksAreNotSupported();

        var key = "target-service";
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"{ConfigurationSectionName}:DisableHealthChecks", "false"),
            new KeyValuePair<string, string?>($"{ConfigurationSectionName}:{key}:DisableHealthChecks", "true"),
        ]);

        RegisterComponent(builder, key: key);

        using var host = builder.Build();

        // HealthChecksService is not configured because DisableHealthChecks is set to true in the named configuration
        Assert.Null(host.Services.GetService<HealthCheckService>());
    }

    protected virtual void SetupConnectionInformationIsDelayValidated() { }

    // This method can have side effects (setting AppContext switch, enabling activity source by name).
    // That is why it needs to be executed in a standalone process.
    // We use RemoteExecutor for that, but it does not support abstract classes
    // (it can not determine the type to instantiate), so that is why this "test"
    // is here and derived types call it
    protected void ActivitySourceTest(string? key)
    {
        HostApplicationBuilder builder = CreateHostBuilder(key: key);
        RegisterComponent(builder, options => SetTracing(options, true), key);

        List<Activity> exportedActivities = new();
        builder.Services.AddOpenTelemetry().WithTracing(builder => builder.AddInMemoryExporter(exportedActivities));

        using (IHost host = builder.Build())
        {
            // We start the host to make it build TracerProvider.
            // If we don't, nothing gets reported!
            host.Start();

            TService service = key is null
                ? host.Services.GetRequiredService<TService>()
                : host.Services.GetRequiredKeyedService<TService>(key);

            Assert.Empty(exportedActivities);

            try
            {
                TriggerActivity(service);
            }
            catch (Exception) when (!CanConnectToServer)
            {
            }

            Assert.NotEmpty(exportedActivities);
            Assert.Contains(exportedActivities, activity => activity.Source.Name == ActivitySourceName);
        }
    }

    protected IHost CreateHostWithComponent(Action<TOptions>? configureComponent = null, HostApplicationBuilderSettings? hostSettings = null, string? key = null)
    {
        HostApplicationBuilder builder = CreateHostBuilder(hostSettings, key);

        RegisterComponent(builder, configureComponent, key);

        return builder.Build();
    }

    protected IHost CreateHostWithMultipleKeyedComponents(params string[] keys)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(settings: null);

        foreach (var key in keys)
        {
            PopulateConfiguration(builder.Configuration, key);
            RegisterComponent(builder, key: key);
        }

        return builder.Build();
    }

    protected void SkipIfHealthChecksAreNotSupported()
    {
        if (!HealthChecksAreSupported)
        {
            throw new SkipTestException("Health checks aren't supported.");
        }
    }

    protected void SkipIfKeyedRegistrationIsNotSupported(bool useKey = true)
    {
        if (useKey && !SupportsKeyedRegistrations)
        {
            throw new SkipTestException("Does not support Keyed Services");
        }
    }

    protected void SkipIfTracingIsNotSupported()
    {
        if (!TracingIsSupported)
        {
            throw new SkipTestException("Tracing is not supported.");
        }
    }

    protected void SkipIfMetricsAreNotSupported()
    {
        if (!MetricsAreSupported)
        {
            throw new SkipTestException("Metrics are not supported.");
        }
    }

    protected void SkipIfRequiredServerConnectionCanNotBeEstablished()
    {
        if (!CanCreateClientWithoutConnectingToServer && !CanConnectToServer)
        {
            throw new SkipTestException("Unable to connect to the server.");
        }
    }

    protected void SkipIfCanNotConnectToServer()
    {
        if (!CanConnectToServer)
        {
            throw new SkipTestException("Unable to connect to the server.");
        }
    }

    protected void SkipIfNamedConfigNotSupported()
    {
        if (!SupportsNamedConfig || ConfigurationSectionName is null)
        {
            throw new SkipTestException("Named configuration is not supported.");
        }
    }

    public static string CreateConfigKey(string prefix, string? key, string suffix)
        => string.IsNullOrEmpty(key) ? $"{prefix}:{suffix}" : $"{prefix}:{key}:{suffix}";

    protected HostApplicationBuilder CreateHostBuilder(HostApplicationBuilderSettings? hostSettings = null, string? key = null)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(hostSettings);

        PopulateConfiguration(builder.Configuration, key);

        return builder;
    }

    private static bool CheckIfImplemented(Action<TOptions, bool> action)
    {
        try
        {
            action(new TOptions(), true);

            return true;
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }
}

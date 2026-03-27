// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Utils;

public static class EnvironmentVariableEvaluator
{
    public static async ValueTask<Dictionary<string, string>> GetEnvironmentVariablesAsync(IResource resource,
        DistributedApplicationOperation applicationOperation = DistributedApplicationOperation.Run,
        IServiceProvider? serviceProvider = null, string? containerHostName = null)
    {
        var executionContext = new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(applicationOperation)
        {
            ServiceProvider = serviceProvider
        });

        var result = await ExecutionConfigurationBuilder
            .Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance)
            .ConfigureAwait(false);

        if (result.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(result.Exception);
        }

        return result.EnvironmentVariables.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}

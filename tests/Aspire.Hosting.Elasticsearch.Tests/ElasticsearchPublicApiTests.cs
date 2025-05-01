// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.Elasticsearch.Tests;

public class ElasticsearchPublicApiTests
{
    [Fact]
    public void AddElasticsearchShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "elasticsearch";

        var action = () => builder.AddElasticsearch(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<ElasticsearchResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<ElasticsearchResource> builder = null!;
        const string source = "/usr/share/elasticsearch/data";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void CtorElasticsearchResourceShouldThrowWhenPasswordIsNull()
    {
        const string name = "elasticsearch";
        ParameterResource password = null!;

        var action = () => new ElasticsearchResource(name, password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(password), exception.ParamName);
    }
}

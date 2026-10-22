var builder = DistributedApplication.CreateBuilder(args);

// Every compute resource below is published as a container app in this environment.
builder.AddAzureContainerAppEnvironment("env");

var apiService = builder.AddProject<Projects.SampleAspireProject_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.SampleAspireProject_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();

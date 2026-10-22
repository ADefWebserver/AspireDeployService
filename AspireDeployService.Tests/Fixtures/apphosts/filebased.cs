#:sdk Aspire.AppHost.Sdk@13.6.1
#:package Aspire.Hosting.Azure.AppContainers@13.6.1
#:project ./Api/Api.csproj

var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureContainerAppEnvironment("env");
builder.Build().Run();

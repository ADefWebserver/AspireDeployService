// Copied from BlazorDataDeploymentWebsite.Tests/Worker/ErrorClassifierTests.cs (Azure SDK cases dropped) (implementation plan D2).
using AspireDeployService.Core.Errors;
using AspireDeployService.Core.Processes;

namespace AspireDeployService.Tests.Copied;

/// <summary>ARM error text patterns. The Azure SDK status-mapping tests are dropped with RequestFailedException (D10).</summary>
public class ErrorClassifierTests
{
    [Theory]
    [InlineData("ERROR: deployment failed: AuthorizationFailed: The client 'abc' with object id 'x' does not have authorization to perform action 'Microsoft.Resources/deployments/write'", ErrorCategory.Permissions)]
    [InlineData("The client does not have authorization to perform action", ErrorCategory.Permissions)]
    [InlineData("AADSTS700213: No matching federated identity record found for presented assertion subject", ErrorCategory.Permissions)]
    [InlineData("MissingSubscriptionRegistration: The subscription is not registered to use namespace 'Microsoft.App'.", ErrorCategory.Provider)]
    [InlineData("QuotaExceeded: Operation could not be completed as it results in exceeding approved Total Regional Cores quota.", ErrorCategory.Quota)]
    [InlineData("MaxNumberOfRegionalEnvironmentsInSubExceeded: The subscription has reached the maximum number of environments", ErrorCategory.Quota)]
    [InlineData("StorageAccountAlreadyTaken: The storage account named bdostorage is already taken.", ErrorCategory.Naming)]
    [InlineData("NameNotAvailable: The registry name acrbdo is already in use.", ErrorCategory.Naming)]
    [InlineData("ServerNameAlreadyExists: The specified server name is already in use.", ErrorCategory.Naming)]
    [InlineData("RequestDisallowedByPolicy: Resource 'x' was disallowed by policy.", ErrorCategory.Policy)]
    [InlineData("context deadline exceeded", ErrorCategory.Timeout)]
    public void Classifies_output_text(string output, ErrorCategory expected)
    {
        var result = ErrorClassifier.Classify(new ProcessFailedException("azd provision", 1, output), output);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void Policy_beats_the_AuthorizationFailed_text_that_often_accompanies_it()
    {
        const string text = "AuthorizationFailed ... RequestDisallowedByPolicy: Policy identifiers: '[{\"policyAssignment\":{\"name\":\"Deny public IPs\",\"id\":\"/providers/x\"},\"policyDefinition\":{\"name\":\"def-1\"}}]'";
        var result = ErrorClassifier.ClassifyText(text);

        Assert.NotNull(result);
        Assert.Equal(ErrorCategory.Policy, result.Category);
        Assert.Equal("Deny public IPs", result.PolicyName);
        Assert.Contains("Deny public IPs", result.Summary);
    }

    [Fact]
    public void Policy_name_falls_back_to_the_definition_name()
    {
        Assert.Equal("Allowed locations", ErrorClassifier.ExtractPolicyName("{\"policyDefinition\":{\"name\":\"Allowed locations\"}}"));
    }

    [Fact]
    public void Summary_is_the_matching_line()
    {
        const string output = "line one\nERROR: QuotaExceeded: not enough cores\nline three";
        var result = ErrorClassifier.Classify(new ProcessFailedException("azd", 1, output), output);
        Assert.Equal("ERROR: QuotaExceeded: not enough cores", result.Summary);
    }

    [Fact]
    public void Archive_errors_are_Source()
    {
        Assert.Equal(ErrorCategory.Source, ErrorClassifier.Classify(new InvalidDataException("zip slip")).Category);
        Assert.Equal(ErrorCategory.Source,
            ErrorClassifier.Classify(new DeploymentFailureException(ErrorCategory.Source, "unsafe archive")).Category);
    }

    [Fact]
    public void Known_categories_are_kept()
    {
        var result = ErrorClassifier.Classify(new DeploymentFailureException(ErrorCategory.Quota, "Not enough cores."));
        Assert.Equal(ErrorCategory.Quota, result.Category);
        Assert.Equal("Not enough cores.", result.Summary);
    }

    [Fact]
    public void Timeouts_are_Timeout()
    {
        Assert.Equal(ErrorCategory.Timeout, ErrorClassifier.Classify(new TimeoutException("azd did not finish within 40 minutes.")).Category);
        Assert.Equal(ErrorCategory.Timeout,
            ErrorClassifier.Classify(new TaskCanceledException("HTTP timeout", new TimeoutException())).Category);
    }

    [Fact]
    public void Anything_else_is_Unknown_with_a_bounded_summary()
    {
        var result = ErrorClassifier.Classify(new InvalidOperationException(new string('x', 2000)));
        Assert.Equal(ErrorCategory.Unknown, result.Category);
        Assert.True(result.Summary.Length <= 501);
    }
}

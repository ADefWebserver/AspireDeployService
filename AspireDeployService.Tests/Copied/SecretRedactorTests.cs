// Copied from BlazorDataDeploymentWebsite.Tests/Worker/SecretRedactorTests.cs (Log_writer_redacts_before_storing dropped) (implementation plan D2).
using AspireDeployService.Core.Logging;

namespace AspireDeployService.Tests.Copied;

/// <summary>Parent plan §16 (log redaction): no credential may reach the DeploymentLogs table.</summary>
public class SecretRedactorTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJhcGk6Ly9BenVyZUFEVG9rZW5FeGNoYW5nZSJ9.c2lnbmF0dXJlLXZhbHVlLWhlcmU";

    [Theory]
    [InlineData("token: " + Jwt, Jwt)]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=st1;AccountKey=abc123+/def==;EndpointSuffix=core.windows.net", "abc123+/def==")]
    [InlineData("Server=tcp:x.database.windows.net;User ID=sa;Password=P@ssw0rd!;", "P@ssw0rd!")]
    [InlineData("Server=x;Uid=sa;Pwd=hunter2hunter2", "hunter2hunter2")]
    [InlineData("https://st1.blob.core.windows.net/c/b?sv=2024-11-04&se=2026-10-15&sp=r&sig=AbCdEf%2Bsecret%3D", "AbCdEf%2Bsecret%3D")]
    [InlineData("BlobEndpoint=https://st1.blob.core.windows.net/;SharedAccessSignature=sv=2024&sig=zzzSECRETzzz", "zzzSECRETzzz")]
    [InlineData("$ azd auth login --client-id x --client-secret Sup3r~Secret.Value", "Sup3r~Secret.Value")]
    [InlineData("$ az login --service-principal --password=Sup3r~Secret.Value", "Sup3r~Secret.Value")]
    [InlineData("$ az login --federated-token abc.def.ghiSECRET", "abc.def.ghiSECRET")]
    [InlineData("{\"client_secret\": \"s3cr3t-value\", \"tenant\": \"t\"}", "s3cr3t-value")]
    [InlineData("{\"accessToken\":\"opaque-access-token-123\"}", "opaque-access-token-123")]
    [InlineData("client_secret=s3cr3t-value&grant_type=client_credentials", "s3cr3t-value")]
    [InlineData("Authorization: Bearer opaqueTokenValue123", "opaqueTokenValue123")]
    [InlineData("AZURE_OIDC_TOKEN=some-assertion-value", "some-assertion-value")]
    [InlineData("AZURE_CLIENT_SECRET: another-secret-value", "another-secret-value")]
    [InlineData("key: -----BEGIN PRIVATE KEY-----MIIEvQIBADANBgkqhkiG9w0BAQEFAASC-----END PRIVATE KEY-----", "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC")]
    public void Redacts_known_secret_patterns(string line, string secret)
    {
        var redacted = new SecretRedactor().Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("REDACTED", redacted);
    }

    [Fact]
    public void Redacts_registered_runtime_secrets_anywhere()
    {
        var redactor = new SecretRedactor();
        const string assertion = "opaque-federated-assertion-0123456789";
        redactor.AddSecret(assertion);

        var redacted = redactor.Redact($"debug: exchanging {assertion} for a token");

        Assert.DoesNotContain(assertion, redacted);
        Assert.Contains(SecretRedactor.Mask, redacted);
    }

    [Fact]
    public void Ignores_too_short_registered_values()
    {
        var redactor = new SecretRedactor();
        redactor.AddSecret("abc");
        Assert.Equal("abc def", redactor.Redact("abc def"));
    }

    [Theory]
    [InlineData("Provisioning Azure resources in rg-bdo (eastus)")]
    [InlineData("$ az acr import --name acrcustomer01 --source ghcr.io/blazor-data-orchestrator/bdo-web@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --image bdo-web:1.2.0")]
    [InlineData("SUCCESS: Your application was provisioned in Azure in 7 minutes.")]
    [InlineData("AZURE_CONTAINER_REGISTRY_ENDPOINT=\"acrbdo.azurecr.io\"")]
    public void Leaves_ordinary_output_alone(string line) =>
        Assert.Equal(line, new SecretRedactor().Redact(line));

    [Fact]
    public void Scope_suppresses_a_multi_line_pem_block()
    {
        var scope = new SecretRedactor().CreateScope();
        string?[] output =
        [
            scope.Redact("writing certificate:"),
            scope.Redact("-----BEGIN PRIVATE KEY-----"),
            scope.Redact("MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7"),
            scope.Redact("qhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7MIIEvQIBADANBgk"),
            scope.Redact("-----END PRIVATE KEY-----"),
            scope.Redact("done")
        ];

        Assert.Equal("writing certificate:", output[0]);
        Assert.Equal("[REDACTED-PEM]", output[1]);
        Assert.Null(output[2]);
        Assert.Null(output[3]);
        Assert.Null(output[4]);
        Assert.Equal("done", output[5]);
    }
}

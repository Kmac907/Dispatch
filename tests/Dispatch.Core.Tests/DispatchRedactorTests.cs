using Dispatch.Core.Redaction;

namespace Dispatch.Core.Tests;

public sealed class DispatchRedactorTests
{
    [Fact]
    public void RedactRemovesSecretLookingValuesFromText()
    {
        var text = "password=hunter2 token=abc123 sig=leaked SharedAccessSignature=shared-secret";

        var redacted = DispatchRedactor.Redact(text);

        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("leaked", redacted);
        Assert.DoesNotContain("shared-secret", redacted);
        Assert.Contains("password=[redacted]", redacted);
        Assert.Contains("token=[redacted]", redacted);
        Assert.Contains("sig=[redacted]", redacted);
        Assert.Contains("SharedAccessSignature=[redacted]", redacted);
    }

    [Fact]
    public void RedactJsonRemovesSecretLookingValuesFromNestedStrings()
    {
        var json = """
        {
          "message": "token=abc123",
          "nested": {
            "items": [
              "sig=leaked",
              "safe"
            ]
          }
        }
        """;

        var redacted = DispatchRedactor.RedactJson(json);

        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("leaked", redacted);
        Assert.Contains("token=[redacted]", redacted);
        Assert.Contains("sig=[redacted]", redacted);
        Assert.Contains("safe", redacted);
    }
}

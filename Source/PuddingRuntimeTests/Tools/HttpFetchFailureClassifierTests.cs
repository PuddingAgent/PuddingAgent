using System.Security.Authentication;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HttpFetchFailureClassifierTests
{
    [TestMethod]
    public void Forbidden_Maps_To_AccessDenied_Without_Retry()
    {
        var failure = HttpFetchFailureClassifier.ClassifyStatus(403, "Forbidden");

        Assert.AreEqual(HttpFetchFailureKind.AccessDenied, failure.Kind);
        Assert.IsFalse(failure.Retryable);
        Assert.AreEqual("HTTP 403 Forbidden", failure.Summary);
        StringAssert.Contains(failure.Hint, "deepseek_fetch");
    }

    [TestMethod]
    public void Missing_Page_Maps_To_NotFound()
    {
        var failure = HttpFetchFailureClassifier.ClassifyStatus(404, "Not Found");

        Assert.AreEqual(HttpFetchFailureKind.NotFound, failure.Kind);
        Assert.IsFalse(failure.Retryable);
    }

    [TestMethod]
    public void Throttling_Maps_To_RateLimited_With_Retry()
    {
        var failure = HttpFetchFailureClassifier.ClassifyStatus(429, "Too Many Requests");

        Assert.AreEqual(HttpFetchFailureKind.RateLimited, failure.Kind);
        Assert.IsTrue(failure.Retryable);
    }

    [TestMethod]
    public void Server_Error_Maps_To_ServerError_With_Retry()
    {
        var failure = HttpFetchFailureClassifier.ClassifyStatus(503, "Service Unavailable");

        Assert.AreEqual(HttpFetchFailureKind.ServerError, failure.Kind);
        Assert.IsTrue(failure.Retryable);
    }

    [TestMethod]
    public void Tls_Handshake_Failure_Surfaces_The_Inner_Cause()
    {
        var exception = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));

        var failure = HttpFetchFailureClassifier.ClassifyException(exception);

        Assert.AreEqual(HttpFetchFailureKind.TlsHandshake, failure.Kind);
        Assert.IsFalse(failure.Retryable);
        // The outer message ("see inner exception") carries no information; the real cause must reach the agent.
        StringAssert.Contains(failure.Summary, "remote certificate");
        Assert.IsFalse(failure.Summary.Contains("see inner exception", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(failure.Hint, "raw.githubusercontent.com");
    }

    [TestMethod]
    public void Timeout_Is_Retryable()
    {
        var exception = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

        var failure = HttpFetchFailureClassifier.ClassifyException(exception);

        Assert.AreEqual(HttpFetchFailureKind.Timeout, failure.Kind);
        Assert.IsTrue(failure.Retryable);
    }

    [TestMethod]
    public void Unknown_Host_Maps_To_DnsFailure_Without_Retry()
    {
        var exception = new HttpRequestException("No such host is known. (docs.example.invalid:443)");

        var failure = HttpFetchFailureClassifier.ClassifyException(exception);

        Assert.AreEqual(HttpFetchFailureKind.DnsFailure, failure.Kind);
        Assert.IsFalse(failure.Retryable);
    }

    [TestMethod]
    public void Long_Inner_Message_Is_Truncated()
    {
        var exception = new HttpRequestException("outer", new InvalidOperationException(new string('x', 500)));

        var failure = HttpFetchFailureClassifier.ClassifyException(exception);

        Assert.IsTrue(failure.Summary.Length < 300, $"summary too long: {failure.Summary.Length}");
        StringAssert.Contains(failure.Summary, "...");
    }

    [TestMethod]
    public void Error_Message_Keeps_Both_The_Fact_And_The_Decision()
    {
        var failure = HttpFetchFailureClassifier.ClassifyStatus(403, "Forbidden");

        var message = failure.ToErrorMessage();

        StringAssert.Contains(message, failure.Summary);
        StringAssert.Contains(message, failure.Hint);
        StringAssert.Contains(message, " | ");
    }
}

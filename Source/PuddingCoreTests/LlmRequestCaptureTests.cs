using System.Net.Http;
using System.Text;
using PuddingCode.Observability;

namespace PuddingCoreTests;

/// <summary>
/// 捕获器在开关打开时绝不能改变报文语义，也不能因为请求没有 body 而炸掉：
/// 它只在发送前读一次字节，然后把同一份字节放回去。
/// 这两条不变量一旦破了，开开关就等于改线上行为。
/// </summary>
[TestClass]
public sealed class LlmRequestCaptureTests
{
    [TestMethod]
    public async Task WriteAsync_WithoutContent_DoesNotThrow()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat");

        await LlmRequestCapture.WriteAsync(request, "model-A", CancellationToken.None);

        Assert.IsNull(request.Content, "无 body 的请求不该被塞进空 content");
    }

    [TestMethod]
    public async Task WriteAsync_PreservesContentTypeAndBytes()
    {
        const string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"你好🙂\"}]}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var before = await request.Content.ReadAsByteArrayAsync();

        await LlmRequestCapture.WriteAsync(request, "model-A", CancellationToken.None);

        Assert.IsNotNull(request.Content);
        Assert.AreEqual("application/json", request.Content.Headers.ContentType?.MediaType);
        var after = await request.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(before, after, "放回去的必须还是同一份字节");
    }
}

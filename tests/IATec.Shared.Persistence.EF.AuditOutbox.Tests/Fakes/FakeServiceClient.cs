using IATec.Shared.HttpClient.Dto;
using IATec.Shared.HttpClient.Service;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IServiceClient"/> whose <see cref="PostAsync(string, HttpContent)"/>
/// outcome is fully controlled by the test: it returns a configured response or throws a configured
/// exception (to simulate an open circuit / timeout / transport error).
/// </summary>
internal sealed class FakeServiceClient : IServiceClient
{
    private readonly Func<BaseResponseDto> _postResult;

    private FakeServiceClient(Func<BaseResponseDto> postResult) => _postResult = postResult;

    /// <summary>Number of times <see cref="PostAsync(string, HttpContent)"/> was invoked.</summary>
    public int PostCallCount { get; private set; }

    /// <summary>The last URL passed to <see cref="PostAsync(string, HttpContent)"/>.</summary>
    public string? LastUrl { get; private set; }

    public static FakeServiceClient Returning(BaseResponseDto response) =>
        new(() => response);

    public static FakeServiceClient Throwing(Exception exception) =>
        new(() => throw exception);

    public Task<BaseResponseDto> PostAsync(string url, HttpContent content)
    {
        PostCallCount++;
        LastUrl = url;
        return Task.FromResult(_postResult());
    }

    public Task<ResponseDto<T>> PostAsync<T>(string url, HttpContent content) where T : class =>
        throw new NotSupportedException();

    public Task<BaseResponseDto> PutAsync(string url, HttpContent content) =>
        throw new NotSupportedException();

    public Task<ResponseDto<T>> PutAsync<T>(string url, HttpContent content) where T : class =>
        throw new NotSupportedException();

    public Task<BaseResponseDto> DeleteAsync(string url) =>
        throw new NotSupportedException();

    public Task<ResponseDto<T>> DeleteAsync<T>(string url) where T : class =>
        throw new NotSupportedException();

    public Task<ResponseDto<T>> GetAsync<T>(string url) where T : class =>
        throw new NotSupportedException();

    public Task<ResponseDto<string>> GetStringAsync(string url) =>
        throw new NotSupportedException();
}

/// <summary>
/// Test double for <see cref="IServiceClientFactory"/> that always returns the supplied
/// <see cref="FakeServiceClient"/>.
/// </summary>
internal sealed class FakeServiceClientFactory : IServiceClientFactory
{
    private readonly IServiceClient _client;

    public FakeServiceClientFactory(IServiceClient client) => _client = client;

    public IServiceClient Create(string clientName) => _client;
}

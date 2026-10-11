using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;

namespace Migurdex.Core.Infrastructure.Http;

public class RustHttpDefaultOptionsHandler : DelegatingHandler
{
    private readonly BrowserEmulation? _defaultEmulation;
    private readonly bool              _skipCertVerify;
    private readonly bool              _skipDefaultHeaders;

    public RustHttpDefaultOptionsHandler(HttpMessageHandler innerHandler,
        bool                                                skipCertVerify     = true,
        BrowserEmulation?                                   defaultEmulation   = null,
        bool                                                skipDefaultHeaders = false)
        : base(innerHandler)
    {
        _skipCertVerify     = skipCertVerify;
        _defaultEmulation   = defaultEmulation;
        _skipDefaultHeaders = skipDefaultHeaders;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                     cancellationToken)
    {
        if (!request.Options.TryGetValue(RustHttpOptions.SkipCertVerifyKey, out _))
        {
            request.Options.Set(RustHttpOptions.SkipCertVerifyKey, _skipCertVerify);
        }

        if (_skipDefaultHeaders && !request.Options.TryGetValue(RustHttpOptions.NoDefaultHeadersKey, out _))
        {
            request.Options.Set(RustHttpOptions.NoDefaultHeadersKey, true);
        }

        if (_defaultEmulation.HasValue && !request.Options.TryGetValue(RustHttpOptions.EmulationKey, out _))
        {
            request.WithEmulation(_defaultEmulation.Value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

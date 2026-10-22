namespace AiProxy.Core.Providers;

/// <summary>Consults several detectors in order of authority and decides what the request speaks.</summary>
/// <remarks>
/// The path beats the body, and the body beats the headers. A path and a body that name
/// different providers are a conflict, and a conflict is a refusal rather than a guess:
/// such a request carries a credential of unknown provenance, and the proxy must not
/// send it to a host it chose. A header that disagrees with the body is not a conflict,
/// because a header is a hint and the body is the shape the caller actually sent.
/// </remarks>
public sealed class CompositeDialectDetector : IDialectDetector
{
    private readonly IReadOnlyList<IDialectDetector> _detectors;

    /// <summary>Composes the detectors, in any order; the authority comes from the signal each one reports.</summary>
    public CompositeDialectDetector(IEnumerable<IDialectDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);

        _detectors = detectors.ToArray();
    }

    /// <summary>Detects the dialect from every signal the composed detectors read.</summary>
    Detection? IDialectDetector.Detect(DetectionRequest request) => Detect(request);

    /// <summary>Detects the dialect from every signal the composed detectors read.</summary>
    public Detection Detect(DetectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Detection? pathSignal = null;
        Detection? bodySignal = null;
        Detection? headerSignal = null;

        foreach (var detector in _detectors)
        {
            var signal = detector.Detect(request);
            switch (signal?.Source)
            {
                case DialectSource.Path when pathSignal is null:
                    pathSignal = signal;
                    break;
                case DialectSource.Body when bodySignal is null:
                    bodySignal = signal;
                    break;
                case DialectSource.Header when headerSignal is null:
                    headerSignal = signal;
                    break;
                default:
                    break;
            }
        }

        if (pathSignal is not null && bodySignal is not null && pathSignal.Dialect != bodySignal.Dialect)
        {
            return Detection.Conflicted(
                $"path says {pathSignal.Dialect} ({request.Path}) but body says {bodySignal.Dialect}; refusing to guess");
        }

        if (pathSignal is not null)
        {
            return pathSignal;
        }

        if (bodySignal is not null)
        {
            var agrees = headerSignal is not null && headerSignal.Dialect == bodySignal.Dialect;
            return Detection.Resolved(
                bodySignal.Dialect,
                DialectSource.Body,
                agrees
                    ? $"{bodySignal.Reason}; headers agree"
                    : bodySignal.Reason);
        }

        if (headerSignal is not null)
        {
            return headerSignal;
        }

        return Detection.Unresolved($"no dialect signal in path {request.Path}, headers or body");
    }
}

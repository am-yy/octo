using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Octo.Services.Common;

/// <summary>
/// Keeps Subsonic credentials out of the log. Subsonic clients sign in through the query string,
/// so every request line ASP.NET writes ("Request starting ... /rest/star?u=...&amp;t=...&amp;s=...")
/// carries a token and salt that replay as that user, and <c>p=</c> and <c>apiKey=</c> are worse.
///
/// The redaction wraps the logger factory rather than any one log statement, so it holds for
/// ASP.NET's request lines, HttpClient's lines and Octo's own, at whatever level an operator
/// turns them up to. Only the values are masked; the lines keep their shape.
/// </summary>
public static class LogRedaction
{
    public const string Mask = "***";

    // t and s are the token login, p the password (plain or enc:), apiKey the OpenSubsonic key,
    // token what several clients call theirs, api_key Last.fm's key and client AcoustID's.
    // Subsonic reads names case-insensitively, so this does. sk is a Last.fm session key,
    // which scrobbles as that listener, and api_sig is signed with the Last.fm shared secret.
    // user is the AcoustID user's own API key, sent with every submission. Subsonic's u is
    // not it, and stays readable.
    private static readonly Regex SecretParameter = new(
        @"(?<=[?&;](?:t|s|p|apikey|token|api_token|api_key|client|user|sk|api_sig)=)[^&#\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The text with every secret query parameter's value replaced by <see cref="Mask"/>.
    /// Returns the same instance when there is nothing to mask.</summary>
    public static string Redact(string text) =>
        text.Contains('=') ? SecretParameter.Replace(text, Mask) : text;

    /// <summary>Wraps the logger factory so everything logged through it is redacted.</summary>
    public static ILoggingBuilder AddCredentialRedaction(this ILoggingBuilder logging)
    {
        logging.Services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new RedactingLoggerFactory(
            new LoggerFactory(
                sp.GetServices<ILoggerProvider>(),
                sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
                sp.GetService<IOptions<LoggerFactoryOptions>>(),
                sp.GetService<IExternalScopeProvider>()))));
        return logging;
    }

    /// <summary>A structured value as a sink will write it. Strings, and anything whose text is a
    /// URL or query (Uri, QueryString, PathString), come back masked; numbers and dates as they were.</summary>
    internal static object? RedactValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return Redact(text);
            case Enum or DateTime or DateTimeOffset or TimeSpan or Guid or decimal:
                return value;
        }
        if (value.GetType().IsPrimitive) return value;
        var shown = value.ToString();
        if (shown is null) return value;
        var redacted = Redact(shown);
        return ReferenceEquals(redacted, shown) ? value : redacted;
    }
}

internal sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}

internal sealed class RedactingLogger(ILogger inner) : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        inner.BeginScope(new RedactedLogState<TState>(state, null, static (s, _) => s.ToString() ?? ""));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel)) return;
        inner.Log(logLevel, eventId, new RedactedLogState<TState>(state, exception, formatter), exception,
            static (s, _) => s.ToString());
    }
}

/// <summary>
/// A log entry's state as sinks see it: the formatted message and every structured value masked.
/// Both are worked out on first use, so a sink that never asks for the values pays nothing for them.
/// </summary>
internal sealed class RedactedLogState<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    private string? _message;
    private KeyValuePair<string, object?>[]? _values;

    private KeyValuePair<string, object?>[] Values => _values ??= state is IEnumerable<KeyValuePair<string, object?>> pairs
        ? pairs.Select(p => new KeyValuePair<string, object?>(p.Key, LogRedaction.RedactValue(p.Value))).ToArray()
        : [];

    public int Count => Values.Length;

    public KeyValuePair<string, object?> this[int index] => Values[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)Values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => _message ??= LogRedaction.Redact(formatter(state, exception));
}

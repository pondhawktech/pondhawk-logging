using Serilog;

namespace Pondhawk.Logging;

/// <summary>
/// Creates category-scoped <see cref="ILogger"/> instances. This is the single seam an application
/// depends on to obtain loggers, independent of which provider (if any) is wired underneath —
/// canonical Serilog, the switch-aware Watch provider, or a custom implementation.
/// </summary>
public interface ILoggerSource
{
    /// <summary>Creates a logger whose category (<c>SourceContext</c>) is the concise full name of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type whose name becomes the logger category.</typeparam>
    ILogger CreateLogger<T>();

    /// <summary>Creates a logger whose category is the concise full name of <paramref name="source"/>.</summary>
    /// <param name="source">The type whose name becomes the logger category.</param>
    ILogger CreateLogger(Type source);

    /// <summary>Creates a logger for the given category (<c>SourceContext</c>) string.</summary>
    /// <param name="category">The logger category.</param>
    ILogger CreateLogger(string category);
}

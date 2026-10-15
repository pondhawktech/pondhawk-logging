// The parts of Fabrica.Watch the migration rules match, with its namespaces, names and signatures. The rules
// recognise Fabrica.Watch by name, so this stands in for any real copy.
#nullable enable
using System;
using System.Runtime.CompilerServices;
using Fabrica.Watch.Sink;

namespace Fabrica.Watch
{
    public enum Level { Trace, Debug, Info, Warning, Error, Quiet }

    public interface ILogger : IDisposable
    {
        string Category { get; }
        bool IsTraceEnabled { get; }
        bool IsDebugEnabled { get; }
        bool IsInfoEnabled { get; }
        bool IsWarningEnabled { get; }
        bool IsErrorEnabled { get; }
        string GetCurrentScope();
        void SetCurrentScope(string name);
    }

    public static class LoggerExtensions
    {
        public static void Trace(this ILogger logger, object? message) { }
        public static void Trace(this ILogger logger, Func<string> expression) { }
        public static void Trace(this ILogger logger, Exception ex, object? message = null) { }
        public static void Trace(this ILogger logger, string template, params object?[] args) { }
        public static void Trace(this ILogger logger, Exception ex, string template, params object?[] args) { }

        public static void Debug(this ILogger logger, object? message) { }
        public static void Debug(this ILogger logger, Func<string> expression) { }
        public static void Debug(this ILogger logger, Exception ex, object? message = null) { }
        public static void Debug(this ILogger logger, string template, params object?[] args) { }
        public static void Debug(this ILogger logger, Exception ex, string template, params object?[] args) { }

        public static void Info(this ILogger logger, object? message) { }
        public static void Info(this ILogger logger, Func<string> expression) { }
        public static void Info(this ILogger logger, Exception ex, object? message = null) { }
        public static void Info(this ILogger logger, string template, params object?[] args) { }
        public static void Info(this ILogger logger, Exception ex, string template, params object?[] args) { }

        public static void Warning(this ILogger logger, object? message) { }
        public static void Warning(this ILogger logger, Func<string> expression) { }
        public static void Warning(this ILogger logger, Exception ex, object? message = null) { }
        public static void Warning(this ILogger logger, string template, params object?[] args) { }
        public static void Warning(this ILogger logger, Exception ex, string template, params object?[] args) { }
        public static void WarningWithContext(this ILogger logger, Exception ex, object context, object? message = null) { }

        public static void Error(this ILogger logger, object? message) { }
        public static void Error(this ILogger logger, Func<string> expression) { }
        public static void Error(this ILogger logger, Exception ex, object? message = null) { }
        public static void Error(this ILogger logger, string template, params object?[] args) { }
        public static void Error(this ILogger logger, Exception ex, string template, params object?[] args) { }
        public static void ErrorWithContext(this ILogger logger, Exception ex, object context, object? message = null) { }

        public static void EnterMethod(this ILogger logger, [CallerMemberName] string name = "") { }
        public static void LeaveMethod(this ILogger logger, [CallerMemberName] string name = "") { }
        public static void EnterScope(this ILogger logger, string name = "") { }
        public static void LeaveScope(this ILogger logger, string name = "") { }

        public static void Inspect<T>(this ILogger logger, object name, T? value) { }
        public static void LogSql(this ILogger logger, string title, string? sql) { }
        public static void LogXml(this ILogger logger, string title, string? xml, bool pretty = true) { }
        public static void LogJson(this ILogger logger, string title, string? json, bool pretty = true) { }
        public static void LogYaml(this ILogger logger, string title, string? yaml) { }
        public static void LogObject(this ILogger logger, string title, object? source) { }
    }

    public struct LoggerRequest
    {
        public string Category { get; set; }
    }

    public interface IWatchFactory
    {
        ILogger GetLogger(string category, bool retroOn = true);
        ILogger GetLogger<T>(bool retroOn = true);
        ILogger GetLogger(Type type, bool retroOn = true);
        ILogger GetLogger(LoggerRequest request, bool retroOn = true);
    }

    public static class WatchFactoryLocator
    {
        public static void SetFactory(IWatchFactory factory) => Factory = factory;
        public static IWatchFactory Factory { get; private set; } = null!;
    }

    public static class WatchExtensions
    {
        public static ILogger GetLogger(this object target) => WatchFactoryLocator.Factory.GetLogger(target.GetType());
        public static ILogger EnterMethod<T>(this T _, [CallerMemberName] string name = "") => WatchFactoryLocator.Factory.GetLogger(typeof(T).FullName!);
        public static ILogger EnterMethodSlim(this object _, string category, [CallerMemberName] string name = "") => WatchFactoryLocator.Factory.GetLogger(category);
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class SensitiveAttribute : Attribute { }
}

namespace Fabrica.Watch.Sink
{
    public enum PayloadType { None, Json, Xml, Sql, Text, Yaml }
}

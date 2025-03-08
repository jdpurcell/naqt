using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace naqt;

public static class GeneralExtensionMethods {
	public static T? TryParse<T>(this string value, IFormatProvider? provider = null)
		where T : struct, IParsable<T>
	{
		return T.TryParse(value, provider, out T result) ? result : null;
	}

	public static IEnumerable<string> ExceptEmpty(this IEnumerable<string> collection) {
		return collection.Where(n => !String.IsNullOrEmpty(n));
	}

	public static string[] SplitLines(this string value) {
		return value.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
	}

	public static IList<T> ThrowIfCountIsNot<T>(this IList<T> list, int expectedCount) {
		return list.Count == expectedCount ? list :
			throw new Exception("Item count differs from expectation.");
	}

	public static TResult With<T, TResult>(this T item, Func<T, TResult> func) {
		return func(item);
	}

	public static XElement RequiredElement(this XContainer container, string name) {
		return container.Element(name) ??
			throw new Exception($"Element \"{name}\" not found.");
	}
}

public class CliParser {
	private readonly string[] _args;
	private int _iArg;

	public CliParser(string[] args) {
		_args = args;
	}

	public bool HasAnyArg() => HasArg();
	public bool HasSwitchArg() => HasArg(ArgType.Switch);
	public bool HasValueArg() => HasArg(ArgType.Value);

	public string GetAnyArg() => GetArg();
	public string GetSwitchArg() => GetArg(ArgType.Switch);
	public string GetValueArg() => GetArg(ArgType.Value);

	public void ExpectEnd() {
		if (HasArg()) {
			throw new InvalidArgumentException();
		}
	}

	private bool HasArg(ArgType argType = default) {
		return _iArg < _args.Length &&
			argType switch {
				ArgType.Switch => _args[_iArg].StartsWith('-'),
				ArgType.Value => !_args[_iArg].StartsWith('-'),
				_ => true
			};
	}

	private string GetArg(ArgType argType = default) {
		return HasArg(argType) ? _args[_iArg++] : throw new InvalidArgumentException();
	}

	private enum ArgType {
		Any,
		Switch,
		Value
	}
}

public class Context {
	public required ILogger Logger { get; init; }
}

public class ConsoleLogger : ILogger {
	public void Write(string message) {
		Console.WriteLine(message);
	}
}

public class DisposeAction : IDisposable {
	private Action? _action;

	public DisposeAction(Action action) {
		_action = action;
	}

	public void Dispose() {
		Action? action = Interlocked.Exchange(ref _action, null);
		action?.Invoke();
	}
}

public static class AsyncFile {
	private const int DefaultBufferSize = 65536;

	public static FileStream Create(string path) =>
		new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, DefaultBufferSize, FileOptions.Asynchronous);

	public static FileStream OpenRead(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.Asynchronous);
}

public interface ICommandOptions;

public interface ICommand {
	Task RunAsync(CancellationToken cancellationToken = default);
}

public interface ILogger {
	void Write(string message);
}

public class InvalidArgumentException : Exception;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SharpCompress.Archives.SevenZip;

namespace naqt;

public static class Constants {
	public const int FileBufferSize = 65536;

	public static readonly string TrustedMirror = "https://download.qt.io";
}

public static class Helper {
	public static void ExtractSevenZip(
		string archivePath,
		string destDirectory,
		object? createDirectorySync = null,
		CancellationToken cancellationToken = default)
	{
		using SevenZipArchive archive = SevenZipArchive.Open(archivePath);
		HashSet<string> seenDirectories = [];

		createDirectorySync ??= new();
		void CreateDirectory(string directory) {
			if (seenDirectories.Add(directory)) {
				lock (createDirectorySync) {
					Directory.CreateDirectory(directory);
				}
			}
		}

		destDirectory = Path.GetFullPath(destDirectory);
		CreateDirectory(destDirectory);

		SharpCompress.Readers.IReader entries = archive.ExtractAllEntries();
		while (entries.MoveToNextEntry()) {
			cancellationToken.ThrowIfCancellationRequested();

			SevenZipArchiveEntry entry = (SevenZipArchiveEntry)entries.Entry;
			if (String.IsNullOrEmpty(entry.Key)) {
				throw new Exception("Entry has an empty key.");
			}

			string entryDestPath = Path.GetFullPath(Path.Combine(destDirectory, entry.Key));
			if (entry.IsDirectory) {
				if (!entryDestPath.StartsWith(destDirectory, StringComparison.Ordinal) ||
					(entryDestPath.Length > destDirectory.Length &&
					 entryDestPath[destDirectory.Length] != Path.DirectorySeparatorChar))
				{
					throw new Exception("Extracted directory would exist outside of the destination.");
				}
				CreateDirectory(entryDestPath);
			}
			else {
				if (!seenDirectories.Contains(Path.GetDirectoryName(entryDestPath) ?? "")) {
					throw new Exception("File entry is missing a corresponding directory entry.");
				}
				int attrib = entry.ExtendedAttrib ?? 0;
				bool hasUnixAttributes = (attrib & 0x8000) != 0;
				bool isSymbolicLink = hasUnixAttributes && (attrib & 0x20000000) != 0;
				if (!OperatingSystem.IsWindows() && isSymbolicLink) {
					using MemoryStream stream = new();
					entries.WriteEntryTo(stream);
					string targetPath = stream.ReadAllText();
					File.CreateSymbolicLink(entryDestPath, targetPath);
				}
				else {
					using (FileStream stream = File.Create(entryDestPath, Constants.FileBufferSize)) {
						entries.WriteEntryTo(stream);
					}
					if (!OperatingSystem.IsWindows() && hasUnixAttributes) {
						UnixFileMode fileMode = (UnixFileMode)((attrib >> 16) & 0x1FF);
						if (fileMode != UnixFileMode.None) {
							new FileInfo(entryDestPath).UnixFileMode = fileMode;
						}
					}
				}
			}
		}
	}
}

public static class GeneralExtensionMethods {
	public static T? TryParse<T>(this string value, IFormatProvider? provider = null)
		where T : struct, IParsable<T>
	{
		return T.TryParse(value, provider, out T result) ? result : null;
	}

	public static IEnumerable<string> ExceptEmpty(this IEnumerable<string> collection) {
		return collection.Where(n => n.Length != 0);
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

	public static string ReadAllText(this MemoryStream stream) {
		return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
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
	public static FileStream Create(string path) =>
		new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, Constants.FileBufferSize, FileOptions.Asynchronous);

	public static FileStream OpenRead(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.Read, Constants.FileBufferSize, FileOptions.Asynchronous);
}

public interface ICommandOptions;

public interface ICommand {
	Task RunAsync(CancellationToken cancellationToken = default);
}

public interface ILogger {
	void Write(string message);
}

public class InvalidArgumentException : Exception;

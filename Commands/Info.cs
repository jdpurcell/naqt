using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace naqt;

public class InfoOptions : ICommandOptions {
	public InfoOptions(CliParser cli) {
		cli.ExpectEnd();
	}
}

public class InfoCommand : ICommand {
	private Context Context { get; }

	public InfoCommand(InfoOptions options, Context context) {
		Context = context;
	}

	public Task RunAsync(CancellationToken cancellationToken = default) {
		string buildNumber = typeof(InfoCommand).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
		string osName =
			OperatingSystem.IsWindows() ? "Windows" :
			OperatingSystem.IsMacOS() ? "macOS" :
			OperatingSystem.IsLinux() ? "Linux" :
			"Unknown";
		string processArch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
		Context.Logger.Write($"naqt {buildNumber}, {RuntimeInformation.FrameworkDescription}, {osName} {processArch}");
		return Task.CompletedTask;
	}
}

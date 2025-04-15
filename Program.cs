using System;
using System.Threading;
using System.Threading.Tasks;
using static System.Console;

namespace naqt;

internal static class Program {
	public static async Task<int> Main(string[] args) {
		using CancellationTokenSource cts = new();

		if (args.Length == 0) {
			PrintHelp();
			return 0;
		}

		CancelKeyPress += (s, e) => {
			WriteLine("Cancelling...");
			e.Cancel = true;
			cts.Cancel();
		};

		CliParser cli = new(args);
		ICommandOptions commandOptions;
		try {
			commandOptions = cli.GetValueArg() switch {
				"install-qt" => new InstallQtOptions(cli),
				"list-qt" => new ListQtOptions(cli),
				_ => throw new InvalidArgumentException()
			};
		}
		catch (InvalidArgumentException) {
			PrintHelp();
			return 1;
		}

		Context context = new() {
			Logger = new ConsoleLogger()
		};
		ICommand command = commandOptions switch {
			InstallQtOptions options => new InstallQtCommand(options, context),
			ListQtOptions options => new ListQtCommand(options, context),
			_ => throw new InvalidOperationException()
		};

		try {
			await command.RunAsync(cts.Token);
		}
		catch (OperationCanceledException) {
			WriteLine("Operation was cancelled by user.");
			return 1;
		}

		return 0;
	}

	private static void PrintHelp() {
		WriteLine("naqt");
		WriteLine();
		WriteLine("Commands");
		WriteLine("   install-qt");
		WriteLine("      <host> <target> <version> [<arch>]");
		WriteLine("      [--outputdir <directory>]");
		WriteLine("      [--modules <module> [<module>...]]");
		WriteLine("      [--archives <archive> [<archive>...]]");
		WriteLine("      [--extensions <extension> [<extension>...]]");
		WriteLine("      [--autodesktop]");
		WriteLine("      [--mirror <baseurl>]");
		WriteLine("      [--nohash]");
		WriteLine();
		WriteLine("   list-qt");
		WriteLine("      <host> <target> <version> [<arch>]");
		WriteLine("      [--mirror <baseurl>]");
		WriteLine("      [--nohash]");
	}
}

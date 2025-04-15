using System.Threading;
using System.Threading.Tasks;

namespace naqt;

public class ListQtOptions : ICommandOptions {
	public QtHost Host { get; set; }
	public QtTarget Target { get; set; }
	public QtVersion Version { get; set; }
	public QtArch? Arch { get; set; }
	public string? Mirror { get; set; }
	public bool NoHash { get; set; }

	public ListQtOptions(CliParser cli) {
		Host = new QtHost(cli.GetValueArg());
		Target = new QtTarget(cli.GetValueArg());
		Version = new QtVersion(cli.GetValueArg());
		Arch = cli.HasValueArg() ? new QtArch(cli.GetValueArg()) : null;
		while (cli.HasSwitchArg()) {
			switch (cli.GetSwitchArg()) {
				case "--mirror":
					Mirror = cli.GetValueArg();
					break;
				case "--nohash":
					NoHash = true;
					break;
				default:
					throw new InvalidArgumentException();
			}
		}
		cli.ExpectEnd();
	}
}

public class ListQtCommand : ICommand {
	private ListQtOptions Options { get; }
	private Context Context { get; }

	private ILogger Logger => Context.Logger;
	private QtHost Host => Options.Host;
	private QtTarget Target => Options.Target;
	private QtVersion Version => Options.Version;
	private QtArch? Arch => Options.Arch;

	public ListQtCommand(ListQtOptions options, Context context) {
		Options = options;
		Context = context;
	}

	public async Task RunAsync(CancellationToken cancellationToken = default) {
		QtUrl updateDirectoryUrl = QtHelper.GetUpdateDirectoryUrl(Host, Target, Version, Arch, customMirror: Options.Mirror);
		QtUpdate update = await QtHelper.FetchUpdate(updateDirectoryUrl, Options.NoHash, cancellationToken);
		if (Arch is null) {
			foreach (QtArch arch in update.GetArchitectures()) {
				Logger.Write(arch.Value);
			}
		}
		else {
			foreach (QtModule module in update.GetModules(Arch)) {
				Logger.Write(module.Name);
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;

namespace naqt;

public class InstallQtOptions : ICommandOptions {
	public QtHost Host { get; set; }
	public QtTarget Target { get; set; }
	public QtVersion Version { get; set; }
	public QtArch? Arch { get; set; }
	public string OutputDir { get; set; } = "";
	public List<string> Modules { get; set; } = [];
	public List<string> Extensions { get; set; } = [];
	public List<string> Archives { get; set; } = [];
	public bool AutoDesktop { get; set; }
	public string? Mirror { get; set; }
	public bool NoHash { get; set; }

	public InstallQtOptions(CliParser cli) {
		Host = new QtHost(cli.GetValueArg());
		Target = new QtTarget(cli.GetValueArg());
		Version = new QtVersion(cli.GetValueArg());
		Arch = cli.HasValueArg() ? new QtArch(cli.GetValueArg()) : null;
		List<string> GetListValues() {
			List<string> values = [];
			while (cli.HasValueArg()) {
				values.Add(cli.GetValueArg());
			}
			if (values.Count == 0) {
				throw new InvalidArgumentException();
			}
			return values;
		}
		while (cli.HasSwitchArg()) {
			switch (cli.GetSwitchArg()) {
				case "--outputdir":
					OutputDir = cli.GetValueArg();
					break;
				case "--modules":
					Modules = GetListValues();
					break;
				case "--extensions":
					Extensions = GetListValues();
					break;
				case "--archives":
					Archives = GetListValues();
					break;
				case "--autodesktop":
					AutoDesktop = true;
					break;
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

public class InstallQtCommand : ICommand {
	private InstallQtOptions Options { get; }
	private Context Context { get; }

	private ILogger Logger => Context.Logger;
	private QtHost Host => Options.Host;
	private QtTarget Target => Options.Target;
	private QtVersion Version => Options.Version;
	private QtArch Arch => Options.Arch ?? QtHelper.GetDefaultArch(Host, Target, Version);
	private QtHost? DesktopHost { get; set; }
	private QtArch? DesktopArch { get; set; }

	public InstallQtCommand(InstallQtOptions options, Context context) {
		Options = options;
		Context = context;
	}

	public async Task RunAsync(CancellationToken cancellationToken = default) {
		long runStartTimestamp = Stopwatch.GetTimestamp();

		QtHost? preferredDesktopHost = null;
		if (QtHelper.UsesAllOsHost(Target, Version) && Host.Value != "all_os") {
			preferredDesktopHost = Host;
			Options.Host = new QtHost("all_os");
		}

		if (Version.IsAtLeast(6, 8, 0) && Options.Modules.Intersect(QtHelper.KnownExtensions).Any()) {
			Options.Extensions.AddRange(Options.Modules.Intersect(QtHelper.KnownExtensions));
			Options.Modules = Options.Modules.Except(QtHelper.KnownExtensions).ToList();
		}
		else if (Version.IsUnder(6, 8, 0) && Options.Extensions.Any()) {
			throw new Exception("Extensions don't exist in this Qt version.");
		}

		Dictionary<QtUrl, QtUpdate> qtUpdateByDirectoryUrl = new();
		async Task<Update> FetchUpdate(QtHost host, QtTarget target, QtArch arch) {
			QtUrl updateDirectoryUrl = QtHelper.GetUpdateDirectoryUrl(host, target, Version, arch, customMirror: Options.Mirror);
			QtUpdate update = qtUpdateByDirectoryUrl.GetValueOrDefault(updateDirectoryUrl) ??
				await QtHelper.FetchUpdate(updateDirectoryUrl, Options.NoHash, cancellationToken);
			qtUpdateByDirectoryUrl[updateDirectoryUrl] = update;
			return new Update(
				updateDirectoryUrl,
				update.GetBasePackage(arch),
				update.GetModules(arch).ToDictionary(m => m.Name)
			);
		}

		Logger.Write($"Selected configuration: {Host} {Target} {Version} {Arch}");
		Update primaryUpdate = await FetchUpdate(Host, Target, Arch);
		Update? desktopUpdate = null;
		AutoDesktopConfiguration? desktopConfig =
			QtHelper.GetAutoDesktopConfiguration(Host, Target, Version, Arch, preferredDesktopHost);
		if (desktopConfig is not null) {
			DesktopHost = preferredDesktopHost ?? desktopConfig.Host;
			DesktopArch = desktopConfig.Arch;
			if (Options.AutoDesktop) {
				Logger.Write($"Desktop configuration: {DesktopHost} desktop {Version} {DesktopArch}");
				desktopUpdate = await FetchUpdate(DesktopHost, new QtTarget("desktop"), DesktopArch);
			}
		}

		string downloadDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		List<Download> downloads = [];
		HashSet<string> locatedModules = new();
		HashSet<string> locatedExtensions = new();
		void AddDownload(QtUpdate.Package package, QtUrl updateDirectoryUrl) {
			downloads.AddRange(
				from archive in package.Archives
				let shouldSkip =
					Options.Archives.Count != 0 &&
					!archive.MatchesShortName("qtbase") &&
					!Options.Archives.Any(archive.MatchesShortName)
				where !shouldSkip
				select new Download(archive, package, updateDirectoryUrl)
			);
		}
		async Task BuildDownloadStrategy(Update update, QtHost host, QtTarget target, QtArch arch) {
			AddDownload(update.BasePackage, update.UpdateDirectoryUrl);
			foreach (string moduleName in Options.Modules) {
				QtModule? module = update.ModulesByName.GetValueOrDefault(moduleName);
				if (module is null) {
					continue;
				}
				AddDownload(module.Package, update.UpdateDirectoryUrl);
				locatedModules.Add(moduleName);
			}
			foreach (string extensionName in Options.Extensions) {
				QtUrl extUpdateDirectoryUrl = QtHelper.GetExtensionUpdateDirectoryUrl(host, target, Version, arch, extensionName, customMirror: Options.Mirror);
				QtUpdate extUpdate = await QtHelper.FetchUpdate(extUpdateDirectoryUrl, Options.NoHash, cancellationToken, allowNotFound: true);
				if (ReferenceEquals(extUpdate, QtHelper.UpdateNotFound)) {
					continue;
				}
				foreach (QtUpdate.Package extensionPackage in extUpdate.Packages.Where(p => p.Name.EndsWithOrdinal($".{arch.Value}"))) {
					AddDownload(extensionPackage, extUpdateDirectoryUrl);
				}
				locatedExtensions.Add(extensionName);
			}
		}
		await BuildDownloadStrategy(primaryUpdate, Host, Target, Arch);
		if (desktopUpdate is not null) {
			await BuildDownloadStrategy(desktopUpdate, DesktopHost!, new QtTarget("desktop"), DesktopArch!);
		}

		// As long as each module or extension was found for either the target or
		// host it is considered successful; only error if not found entirely
		if (Options.Modules.Except(locatedModules).Any()) {
			throw new Exception($"Modules not found: {String.Join(", ", Options.Modules.Except(locatedModules))}");
		}
		if (Options.Extensions.Except(locatedExtensions).Any()) {
			throw new Exception($"Extensions not found: {String.Join(", ", Options.Extensions.Except(locatedExtensions))}");
		}

		Download FindQtbaseDownload(QtUpdate.Package package) =>
			downloads.Single(d => d.Package == package && d.Archive.MatchesShortName("qtbase"));
		Download baseDownload = FindQtbaseDownload(primaryUpdate.BasePackage);
		Download? desktopBaseDownload = desktopUpdate?.BasePackage.With(FindQtbaseDownload);

		object createDirectorySync = new();
		HashSet<string> directoriesPendingDeletion = [downloadDirectory];
		void CleanupDirectories() {
			foreach (string directory in directoriesPendingDeletion) {
				try {
					if (Directory.Exists(directory)) {
						Directory.Delete(directory, true);
					}
				}
				catch {
					Logger.Write($"Warning: Failed to delete directory {directory}");
				}
			}
		}
		using DisposeAction disposeAction = new(CleanupDirectories);

		await Parallel.ForEachAsync(downloads,
			new ParallelOptions {
				MaxDegreeOfParallelism = Constants.DownloadConcurrency,
				CancellationToken = cancellationToken
			},
			async (download, ct) => {
				QtUrl remoteUrl = download.GetUrl();
				string localPath = download.GetLocalPath(downloadDirectory);
				byte[] hash = Options.NoHash ? Network.DummySha256Hash : await Network.GetPublishedSha256ForFileAsync(remoteUrl, ct);
				lock (createDirectorySync) {
					Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
				}
				await using (FileStream destStream = AsyncFile.Create(localPath)) {
					await Network.GetToStreamWithSha256ValidationAsync(remoteUrl, destStream, hash, ct);
				}
				Logger.Write($"Downloaded {download.Archive.FileName}");
			});

		string versionDirectoryName = Version.ToString();
		string GetArchDirectoryName(Download download) {
			string[] baseComponents = download.Archive.GetTargetDirectoryComponents();
			if (baseComponents.Length >= 2) {
				return baseComponents[1];
			}
			string archivePath = download.GetLocalPath(downloadDirectory);
			using SevenZipArchive archive = SevenZipArchive.Open(archivePath);
			IEnumerable<string> candidateNames =
				from entry in archive.Entries
				where entry.IsDirectory &&
					  entry.Key is not null
				let allComponents = (string[])[..baseComponents, ..entry.Key!.Split('/')]
				where allComponents.Length == 3 &&
					  allComponents[0] == versionDirectoryName &&
					  allComponents[^1] == "bin"
				select allComponents[1];
			return candidateNames.Take(2).ToList().With(
				names => names.Count switch {
					1 => names[0],
					_ => throw new Exception("Unable to locate bin directory in archive.")
				});
		}
		string archDirectoryName = GetArchDirectoryName(baseDownload);
		string? desktopArchDirectoryName = desktopBaseDownload?.With(GetArchDirectoryName);

		string outputDirectory = Path.GetFullPath(Options.OutputDir.Length != 0 ? Options.OutputDir : "Qt");
		string GetInstallDirectory(string leafName) =>
			Path.Combine(outputDirectory, versionDirectoryName, leafName);
		string installDirectory = GetInstallDirectory(archDirectoryName);
		string? desktopInstallDirectory = desktopArchDirectoryName?.With(GetInstallDirectory);
		if (Directory.Exists(installDirectory) || (desktopInstallDirectory?.With(Directory.Exists) ?? false)) {
			throw new Exception("Install directory already exists.");
		}

		// Temporarily flag these for deletion in case there's an error before we finish
		directoriesPendingDeletion.Add(installDirectory);
		desktopInstallDirectory?.With(directoriesPendingDeletion.Add);

		Parallel.ForEach(downloads,
			new ParallelOptions {
				MaxDegreeOfParallelism = Constants.ExtractConcurrency,
				CancellationToken = cancellationToken
			},
			download => {
				string archivePath = download.GetLocalPath(downloadDirectory);
				string[] targetDirectoryComponents = download.Archive.GetTargetDirectoryComponents();
				string targetDirectory = targetDirectoryComponents.Length != 0 ?
					Path.Join([outputDirectory, ..targetDirectoryComponents]) :
					outputDirectory;
				Helper.ExtractSevenZip(archivePath, targetDirectory, createDirectorySync, cancellationToken);
				File.Delete(archivePath);
				Logger.Write($"Extracted {download.Archive.FileName}");
			});

		if (desktopConfig is null) {
			// Straightforward patch of this self-sufficient installation
			PatchInstall(new AutonomousInstallation(installDirectory));
		}
		else {
			// This installation requires a separate host installation
			if (desktopInstallDirectory is null) {
				// Host installation wasn't simultaneous; just patch this installation
				// and do our best to infer its host location
				string inferredDesktopInstallDirectory = GetInstallDirectory(
					QtHelper.InferArchDirectoryName(DesktopHost!, new QtTarget("desktop"), Version, DesktopArch!)
				);
				PatchInstall(new CrossCompileInstallation(installDirectory, inferredDesktopInstallDirectory));
			}
			else {
				// Patch both installations which we just completed together
				PatchInstall(new CrossCompileInstallation(installDirectory, desktopInstallDirectory));
				PatchInstall(new AutonomousInstallation(desktopInstallDirectory));
			}
		}

		directoriesPendingDeletion.Remove(installDirectory);
		desktopInstallDirectory?.With(directoriesPendingDeletion.Remove);

		TimeSpan runElapsedTime = Stopwatch.GetElapsedTime(runStartTimestamp);
		Logger.Write($"Finished in {runElapsedTime.TotalSeconds:F3} seconds");
	}

	private void PatchInstall(IInstallation installation) {
		void PatchConfigFile(string confPath, (string Prefix, string UpdatedRemainder)[] updates) {
			string confContent = File.ReadAllText(confPath);
			bool changedConf = false;
			foreach (string line in confContent.SplitLines()) {
				foreach (var update in updates.Where(u => line.StartsWithOrdinal(u.Prefix))) {
					confContent = confContent.Replace(line, update.Prefix + update.UpdatedRemainder);
					changedConf = true;
				}
			}
			if (changedConf) {
				File.WriteAllText(confPath, confContent);
				Logger.Write($"Patched {confPath}");
			}
		}

		string installDirectory = installation.InstallDirectory;

		// Patch qconfig.pri
		PatchConfigFile(
			Path.Combine(installDirectory, "mkspecs", "qconfig.pri"),
			[
				("QT_EDITION =", " OpenSource"),
				("QT_LICHECK =", ""),
			]
		);

		// Create qt.conf
		string qtConfPath = Path.Combine(installDirectory, "bin", "qt.conf");
		File.WriteAllLines(qtConfPath, ["[Paths]", "Prefix=.."]);
		Logger.Write($"Created {qtConfPath}");

		if (installation is AutonomousInstallation) {
			if ((DesktopHost ?? Host).IsWindows) {
				// Create qtenv2.bat
				string qtEnv2Path = Path.Combine(installDirectory, "bin", "qtenv2.bat");
				File.WriteAllLines(qtEnv2Path, [
					"@echo off",
					"echo Setting up environment for Qt usage...",
					$"set PATH={Path.Combine(installDirectory, "bin")};%PATH%",
					$"cd /D {installDirectory}",
					"echo Remember to call vcvarsall.bat to complete environment setup!"
				]);
				Logger.Write($"Created {qtEnv2Path}");
			}

			// Patch pkgconfig files
			string pkgConfigDirectory = Path.Combine(installDirectory, "lib", "pkgconfig");
			IEnumerable<string> pkgConfigPaths = !Directory.Exists(pkgConfigDirectory) ? [] :
				Directory.EnumerateFiles(pkgConfigDirectory, "*.pc");
			foreach (string pkgConfigPath in pkgConfigPaths) {
				PatchConfigFile(pkgConfigPath, [("prefix=", installDirectory)]);
			}
		}
		else if (installation is CrossCompileInstallation crossCompileInstallation) {
			string desktopInstallDirectory = crossCompileInstallation.DesktopInstallDirectory;

			if (Version.IsAtLeast(6, 0, 0)) {
				// Patch target_qt.conf
				PatchConfigFile(
					Path.Combine(installDirectory, "bin", "target_qt.conf"),
					[
						("HostData=", $"../{Path.GetFileName(installDirectory)}"),
						("HostPrefix=", $"../../{Path.GetFileName(desktopInstallDirectory)}"),
						("HostLibraryExecutables=", DesktopHost!.IsWindows ? "./bin" : "./libexec")
					]
				);
			}

			// Patch qmake/qtpaths scripts
			IEnumerable<string> scriptPaths =
				from name in new[] { "qmake", "qtpaths", $"qmake{Version.Major}", $"qtpaths{Version.Major}" }
				from extension in new[] { "", ".bat" }
				select Path.Combine(installDirectory, "bin", name + extension);
			string[] placeholders = [
				"/Users/qt/work/install/",
				"/home/qt/work/install/"
			];
			foreach (string scriptPath in scriptPaths) {
				if (!File.Exists(scriptPath)) {
					continue;
				}
				string scriptContent = File.ReadAllText(scriptPath);
				bool changedScript = false;
				string correctValue = Path.Combine(desktopInstallDirectory, ".")[..^1]; // Add trailing slash
				foreach (string placeholder in placeholders) {
					string originalInstance = scriptContent;
					scriptContent = scriptContent.Replace(placeholder, correctValue)
						.Replace(placeholder.Replace('/', '\\'), correctValue);
					changedScript |= !ReferenceEquals(scriptContent, originalInstance);
				}
				if (changedScript) {
					File.WriteAllText(scriptPath, scriptContent);
					Logger.Write($"Patched {scriptPath}");
				}
			}

			if (Target.Value == "wasm" && Arch.Value == "wasm_singlethread" && !OperatingSystem.IsWindows()) {
				// Add execute permissions - special case for when the 7z archives are built on Windows and
				// therefore lack Unix permission attributes but are intended for cross-platform use.
				List<string[]> executableRelativePaths = [
					["bin", "qmake"],
					["bin", "qmake6"],
					["bin", "qtpaths"],
					["bin", "qtpaths6"],
					["bin", "qt-cmake"],
					["bin", "qt-configure-module"],
					["libexec", "qt-cmake-private"],
					["libexec", "qt-cmake-standalone-test"]
				];
				foreach (string executablePath in executableRelativePaths.Select(p => Path.Combine([installDirectory, ..p]))) {
					new FileInfo(executablePath).UnixFileMode |=
						UnixFileMode.UserExecute |
						UnixFileMode.GroupExecute |
						UnixFileMode.OtherExecute;
					Logger.Write($"Permitted {executablePath}");
				}
			}
		}
	}

	private record Update(
		QtUrl UpdateDirectoryUrl,
		QtUpdate.Package BasePackage,
		Dictionary<string, QtModule> ModulesByName
	);

	private record Download(QtUpdate.Archive Archive, QtUpdate.Package Package, QtUrl UpdateDirectoryUrl) {
		public QtUrl GetUrl() =>
			UpdateDirectoryUrl with { Path = UpdateDirectoryUrl.Path + $"{Package.Name}/{Archive.FileName}" };

		public string GetLocalPath(string directory) =>
			Path.Combine(directory, Package.GetNameWithoutVersion(), Archive.Identifier);
	}

	private interface IInstallation {
		string InstallDirectory { get; }
	}

	private record AutonomousInstallation(string InstallDirectory)
		: IInstallation;

	private record CrossCompileInstallation(string InstallDirectory, string DesktopInstallDirectory)
		: IInstallation;
}

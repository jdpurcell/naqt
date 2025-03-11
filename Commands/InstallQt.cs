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
	public List<string> Archives { get; set; } = [];
	public bool AutoDesktop { get; set; }
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
				case "--archives":
					Archives = GetListValues();
					break;
				case "--autodesktop":
					AutoDesktop = true;
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

	// This should probably be a command line option, but only doing it for wasm for now to
	// mimic a behavior in aqtinstall (which I'm not even sure is intentional).
	private bool DesktopInstallsSameModules => Target.Value == "wasm";

	public InstallQtCommand(InstallQtOptions options, Context context) {
		Options = options;
		Context = context;
	}

	public async Task RunAsync(CancellationToken cancellationToken = default) {
		long runStartTimestamp = Stopwatch.GetTimestamp();

		Logger.Write($"Selected configuration: {Host} {Target} {Version} {Arch}");
		string updateDirectoryUrl = QtHelper.GetUpdateDirectoryUrl(Host, Target, Version, Arch);
		QtUpdate update = await QtHelper.FetchUpdate(updateDirectoryUrl, Options.NoHash, cancellationToken);
		QtUpdate.Package basePackage = update.GetBasePackage(Arch);
		Dictionary<string, QtModule> modulesByName = update.GetModules(Arch).ToDictionary(m => m.Name);

		string? desktopUpdateDirectoryUrl = null;
		QtUpdate.Package? desktopBasePackage = null;
		Dictionary<string, QtModule>? desktopModulesByName = null;
		if (Options.AutoDesktop) {
			AutoDesktopConfiguration? desktopConfig = QtHelper.GetAutoDesktopConfiguration(Host, Target, Version, Arch);
			if (desktopConfig is not null) {
				DesktopHost = desktopConfig.Host;
				DesktopArch = desktopConfig.Arch;
				Logger.Write($"Desktop configuration: {DesktopHost} desktop {Version} {DesktopArch}");
				QtUpdate desktopUpdate;
				if (Host == DesktopHost && Target.Value == "desktop") {
					desktopUpdateDirectoryUrl = updateDirectoryUrl;
					desktopUpdate = update;
				}
				else {
					desktopUpdateDirectoryUrl = QtHelper.GetUpdateDirectoryUrl(DesktopHost, new QtTarget("desktop"), Version, DesktopArch);
					desktopUpdate = await QtHelper.FetchUpdate(desktopUpdateDirectoryUrl, Options.NoHash, cancellationToken);
				}
				desktopBasePackage = desktopUpdate.GetBasePackage(DesktopArch);
				desktopModulesByName = desktopUpdate.GetModules(DesktopArch).ToDictionary(m => m.Name);
			}
		}

		string downloadDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
		List<Download> downloads = [];
		void AddDownload(QtUpdate.Package package, bool forAutoDesktop) {
			downloads.AddRange(
				from archive in package.Archives
				let shouldSkip = Options.Archives.Count != 0 && ReferenceEquals(package, basePackage) &&
					!archive.MatchesShortName("qtbase") && !Options.Archives.Any(archive.MatchesShortName)
				where !shouldSkip
				select new Download(archive, package, forAutoDesktop ? desktopUpdateDirectoryUrl! : updateDirectoryUrl)
			);
		}
		AddDownload(basePackage, false);
		foreach (string moduleName in Options.Modules) {
			QtModule module = modulesByName.GetValueOrDefault(moduleName) ??
				throw new Exception($"Module {moduleName} not found.");
			AddDownload(module.Package, false);
		}
		if (DesktopArch is not null) {
			AddDownload(desktopBasePackage!, true);
			if (DesktopInstallsSameModules) {
				foreach (string moduleName in Options.Modules) {
					QtModule module = desktopModulesByName!.GetValueOrDefault(moduleName) ??
						throw new Exception($"Module {moduleName} not found.");
					AddDownload(module.Package, true);
				}
			}
		}
		Download FindQtbaseDownload(QtUpdate.Package package) =>
			downloads.Single(d => d.Package == package && d.Archive.MatchesShortName("qtbase"));
		Download baseDownload = FindQtbaseDownload(basePackage);
		Download? desktopBaseDownload = desktopBasePackage?.With(FindQtbaseDownload);

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
				string remoteUrl = download.GetUrl();
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

		if (desktopInstallDirectory is null) {
			PatchInstall(installDirectory);
		}
		else {
			PatchInstall(installDirectory, desktopInstallDirectory);
			PatchInstall(desktopInstallDirectory);
		}

		directoriesPendingDeletion.Remove(installDirectory);
		desktopInstallDirectory?.With(directoriesPendingDeletion.Remove);

		TimeSpan runElapsedTime = Stopwatch.GetElapsedTime(runStartTimestamp);
		Logger.Write($"Finished in {runElapsedTime.TotalSeconds:F3} seconds");
	}

	private void PatchInstall(string installDirectory, string? desktopInstallDirectory = null) {
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

		if (desktopInstallDirectory is null) {
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

			// Return early; remaining code is for a cross-compilation install
			return;
		}

		if (Version.ToVersion() >= new Version(6, 0, 0)) {
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
	}

	private record Download(QtUpdate.Archive Archive, QtUpdate.Package Package, string UpdateDirectoryUrl) {
		public string GetUrl() =>
			$"{UpdateDirectoryUrl}{Package.Name}/{Archive.FileName}";

		public string GetLocalPath(string directory) =>
			Path.Combine(directory, Package.GetNameWithoutVersion(), Archive.Identifier);
	}
}

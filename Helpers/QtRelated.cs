using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace naqt;

public record QtHost(string Value) {
	public override string ToString() => Value;

	public string ToUrlComponent() {
		return Value switch {
			"windows" => "windows_x86",
			"windows_arm64" => "windows_arm64",
			"linux" => "linux_x64",
			"linux_arm64" => "linux_arm64",
			"mac" => "mac_x64",
			"all_os" => "all_os",
			_ => throw new ArgumentException("Host value is not recognized.")
		};
	}

	public bool IsWindows =>
		Value.StartsWithOrdinal("windows");

	public bool IsLinux =>
		Value.StartsWithOrdinal("linux");

	public bool IsMacOS =>
		Value.StartsWithOrdinal("mac");
}

public record QtTarget(string Value) {
	public override string ToString() => Value;

	public string ToUrlComponent(QtVersion version) {
		return Value switch {
			"desktop" => "desktop",
			"wasm" => version.IsAtLeast(6, 7, 0) ? "wasm" : "desktop",
			"android" => "android",
			"ios" => "ios",
			_ => throw new ArgumentException("Target value is not recognized.")
		};
	}
}

public record QtArch(string Value) {
	public override string ToString() => Value;
}

public record QtModule(string Name, QtUpdate.Package Package) {
	public override string ToString() => Name;
}

public record QtVersion(int Major, int Minor, int Revision) {
	public QtVersion(string value)
		: this(Parse(value)) { }

	public override string ToString() =>
		$"{Major}.{Minor}.{Revision}";

	public string ToStringNoDots() =>
		$"{Major}{Minor}{Revision}";

	public Version ToVersion() =>
		new Version(Major, Minor, Revision);

	public bool IsAtLeast(int major, int minor, int revision) =>
		ToVersion() >= new Version(major, minor, revision);

	public bool IsUnder(int major, int minor, int revision) =>
		ToVersion() < new Version(major, minor, revision);

	public string ToUrlComponent(string variant) {
		string dirForVersion = $"qt{Major}_{ToStringNoDots()}";
		string dirForVersionAndVariant = variant.Length != 0 ? $"{dirForVersion}_{variant}" : dirForVersion;
		return
			IsAtLeast(6, 8, 0) ? $"{dirForVersion}/{dirForVersionAndVariant}" :
			dirForVersionAndVariant;
	}

	private static QtVersion Parse(string value) =>
		value.Split('.')
			.Select(n => n.TryParse<int>() ?? throw new Exception("Invalid version number."))
			.ToList()
			.ThrowIfCountIsNot(3)
			.With(n => new QtVersion(n[0], n[1], n[2]));
}

public class QtUpdate {
	public required Package[] Packages { get; set; }

	public class Package {
		public required string Name { get; set; }
		public required Archive[] Archives { get; set; }
	}

	public class Archive {
		public required string Identifier { get; set; }
		public required string FileName { get; set; }
		public required string TargetDirectoryArgument { get; set; }
	}
}

public static class QtHelper {
	public static readonly string[] KnownExtensions = ["qtwebengine", "qtpdf"];

	public static readonly QtUpdate UpdateNotFound = new() { Packages = [] };

	public static string GetUpdateDirectoryUrl(QtHost host, QtTarget target, QtVersion version, QtArch? arch = null) {
		string hostComponent = host.ToUrlComponent();
		string targetComponent = target.ToUrlComponent(version);
		string versionVariant = GetUrlVersionVariant(target, version, arch ?? new QtArch("unspecified"));
		if (versionVariant == "unspecified") {
			throw new ArgumentException("Listing architectures for this target is not supported.");
		}
		string versionComponent = version.ToUrlComponent(versionVariant);
		return $"{Constants.TrustedMirror}/online/qtsdkrepository/{hostComponent}/{targetComponent}/{versionComponent}/";
	}

	public static string GetExtensionUpdateDirectoryUrl(QtHost host, QtTarget target, QtVersion version, QtArch arch, string extensionName) {
		string hostComponent = host.ToUrlComponent();
		string versionComponent = version.ToStringNoDots();
		string archComponent = GetExtensionArch(host, target, version, arch);
		return $"{Constants.TrustedMirror}/online/qtsdkrepository/{hostComponent}/extensions/{extensionName}/{versionComponent}/{archComponent}/";
	}

	public static async Task<QtUpdate> FetchUpdate(
		string updateDirectoryUrl, bool noHash = false, CancellationToken cancellationToken = default, bool allowNotFound = false
	) {
		string url = $"{updateDirectoryUrl}Updates.xml";
		try {
			byte[] expectedHash = noHash ? Network.DummySha256Hash : await Network.GetPublishedSha256ForFileAsync(url, cancellationToken);
			string updateXml = await Network.GetAsUtf8StringWithSha256ValidationAsync(url, expectedHash, cancellationToken);
			return ParseUpdate(updateXml);
		}
		catch (HttpRequestException ex) when (allowNotFound && ex.StatusCode == HttpStatusCode.NotFound) {
			return UpdateNotFound;
		}
	}

	private static QtUpdate ParseUpdate(string xmlContents) {
		return ParseUpdate(XDocument.Parse(xmlContents));
	}

	private static QtUpdate ParseUpdate(XDocument doc) {
		return new QtUpdate {
			Packages = [..
				from package in doc.Root!.Elements("PackageUpdate")
				let fullVersion = package.RequiredElement("Version").Value
				let targetDirectoryArgumentByIdentifier = new Dictionary<string, string>(
					from operation in package.Element("Operations")?.Elements("Operation").Where(n => n.Attribute("name")?.Value == "Extract") ?? []
					let args = operation.Elements("Argument").Select(n => n.Value).ToList().ThrowIfCountIsNot(2)
					select KeyValuePair.Create(args[1], args[0])
				)
				select new QtUpdate.Package {
					Name = package.RequiredElement("Name").Value,
					Archives = [..
						from identifier in package.Element("DownloadableArchives")?.Value.Split(',').Select(n => n.Trim()).ExceptEmpty() ?? []
						select new QtUpdate.Archive {
							Identifier = identifier,
							FileName = fullVersion + identifier,
							TargetDirectoryArgument = targetDirectoryArgumentByIdentifier.GetValueOrDefault(identifier) ?? ""
						}
					]
				}
			]
		};
	}

	public static QtArch GetDefaultArch(QtHost host, QtTarget target, QtVersion version) {
		string? archValue = null;
		if (host.Value == "mac" && target.Value == "ios") {
			archValue = "ios";
		}
		else if (target.Value == "desktop") {
			archValue = host.Value switch {
				"windows" =>
					version.IsAtLeast(6, 8, 0) ? "win64_msvc2022_64" :
					"win64_msvc2019_64",
				"windows_arm64" =>
					version.IsAtLeast(6, 8, 0) ? "win64_msvc2022_arm64" :
					null,
				"linux" =>
					version.IsAtLeast(6, 7, 0) ? "linux_gcc_64" :
					"gcc_64",
				"linux_arm64" =>
					version.IsAtLeast(6, 7, 0) ? "linux_gcc_arm64" :
					null,
				"mac" =>
					"clang_64",
				_ => null
			};
		}
		return archValue != null ? new QtArch(archValue) :
			throw new ArgumentException("Unable to determine a default architecture for this host.");
	}

	public static string GetUrlVersionVariant(QtTarget target, QtVersion version, QtArch arch) {
		if (target.Value == "wasm") {
			return version.IsAtLeast(6, 5, 0) ? arch.Value : "wasm";
		}
		if (target.Value == "android") {
			return arch.Value == "unspecified" ? arch.Value :
				arch.Value.StripPrefix("android_") ?? "";
		}
		return "";
	}

	public static bool UsesAllOsHost(QtTarget target, QtVersion version) {
		return target.Value switch {
			"wasm" when version.IsAtLeast(6, 7, 0) => true,
			"android" when version.IsAtLeast(6, 7, 0) => true,
			_ => false
		};
	}

	public static string GetExtensionArch(QtHost host, QtTarget target, QtVersion version, QtArch arch) {
		string? extensionArch = null;
		if (target.Value == "android") {
			extensionArch = arch.Value.StripPrefix("android_")?.With(a => $"qt{version.Major}_{version.ToStringNoDots()}_" + a);
		}
		else if (target.Value is "ios" or "wasm") {
			extensionArch = arch.Value;
		}
		else if (host.IsWindows && target.Value == "desktop") {
			extensionArch = arch.Value.StripPrefix("win64_")?.Replace("_arm64_cross_compiled", "_arm64");
		}
		else if (target.Value == "desktop") {
			extensionArch = (host.Value, arch.Value) switch {
				("mac", "clang_64") => "clang_64",
				("linux", "linux_gcc_64") => "x86_64",
				("linux_arm64", "linux_gcc_arm64") => "arm64",
				_ => null
			};
		}
		return extensionArch ?? throw new Exception("Unrecognized architecture for extensions.");
	}

	public static QtHost DetectMachineHost() {
		string hostValue;
		if (OperatingSystem.IsWindows()) {
			hostValue = RuntimeInformation.OSArchitecture switch {
				Architecture.X64 => "windows",
				Architecture.Arm64 => "windows_arm64",
				_ => throw new NotSupportedException()
			};
		}
		else if (OperatingSystem.IsLinux()) {
			hostValue = RuntimeInformation.OSArchitecture switch {
				Architecture.X64 => "linux",
				Architecture.Arm64 => "linux_arm64",
				_ => throw new NotSupportedException()
			};
		}
		else if (OperatingSystem.IsMacOS()) {
			hostValue = "mac";
		}
		else {
			throw new NotSupportedException();
		}
		return new QtHost(hostValue);
	}

	public static AutoDesktopConfiguration? GetAutoDesktopConfiguration(QtHost host, QtTarget target,
		QtVersion version, QtArch arch, QtHost? preferredDesktopHost = null)
	{
		QtHost desktopHost = host.Value == "all_os" ? preferredDesktopHost ?? DetectMachineHost() : host;
		QtArch? desktopArch = null;
		QtArch GetDefaultDesktopArch() => GetDefaultArch(desktopHost, new QtTarget("desktop"), version);
		if (target.Value == "desktop" && desktopHost.Value == "windows") {
			string armSuffix = version.IsAtLeast(6, 8, 0) ? "_arm64_cross_compiled" : "_arm64";
			if (arch.Value.EndsWithOrdinal(armSuffix)) {
				desktopArch = new QtArch($"{arch.Value[..^armSuffix.Length]}_64");
			}
		}
		else if (target.Value == "ios" && desktopHost.Value == "mac") {
			desktopArch = GetDefaultDesktopArch();
		}
		else if (target.Value is "wasm" or "android") {
			desktopArch = desktopHost.Value == "windows" ? new QtArch("win64_mingw") :
				GetDefaultDesktopArch();
		}
		return desktopArch != null ? new AutoDesktopConfiguration(desktopHost, desktopArch) : null;
	}

	public static string InferArchDirectoryName(QtHost host, QtTarget target, QtVersion version, QtArch arch) {
		string? directoryName = null;
		if (target.Value == "desktop") {
			if (host.IsWindows) {
				if (arch.Value.StartsWithOrdinal("win64_msvc")) {
					directoryName = arch.Value.StripPrefix("win64_")!.Replace("_cross_compiled", "");
				}
				else if (arch.Value.StartsWithOrdinal("win32_msvc")) {
					directoryName = arch.Value.StripPrefix("win32_");
				}
				else if (arch.Value.StartsWithOrdinal("win64_mingw")) {
					directoryName = arch.Value.StripPrefix("win64_") + "_64";
				}
				else if (arch.Value.StartsWithOrdinal("win32_mingw")) {
					directoryName = arch.Value.StripPrefix("win32_") + "_32";
				}
				else if (arch.Value.StartsWithOrdinal("win64_llvm_")) {
					directoryName = "llvm-" + arch.Value.StripPrefix("win64_llvm_") + "_64";
				}
			}
			else if (host.IsLinux && arch.Value.StartsWithOrdinal("linux_")) {
				directoryName = arch.Value.StripPrefix("linux_");
			}
			else if (host.IsMacOS && arch.Value == "clang_64" && version.IsAtLeast(6, 1, 2)) {
				directoryName = "macos";
			}
		}
		return directoryName ?? arch.Value;
	}
}

public record AutoDesktopConfiguration(QtHost Host, QtArch Arch);

public static class QtExtensionMethods {
	public static QtUpdate.Package[] GetBasePackages(this QtUpdate update) {
		return [..
			from package in update.Packages
			where package.Archives.Any(a => a.Identifier.StartsWithOrdinal("qtbase-"))
			let nameSegments = package.Name.Split('.')
			where nameSegments.Length == 4
			select package
		];
	}

	public static QtUpdate.Package GetBasePackage(this QtUpdate update, QtArch arch) {
		List<QtUpdate.Package> packages = GetBasePackages(update)
			.Where(p => p.Name.EndsWithOrdinal($".{arch.Value}")).Take(2).ToList();
		return packages.Count switch {
			1 => packages[0],
			0 => throw new Exception($"No base package found for \"{arch.Value}\"."),
			_ => throw new Exception($"Multiple base packages found for \"{arch.Value}\".")
		};
	}

	public static QtArch[] GetArchitectures(this QtUpdate update) {
		return [..
			from package in GetBasePackages(update)
			let lastDelimiter = package.Name.LastIndexOf('.')
			select new QtArch(package.Name.Substring(lastDelimiter + 1))
		];
	}

	public static QtModule[] GetModules(this QtUpdate update, QtArch arch) {
		string[] groupNames = ["addons"];
		return [..
			from package in update.Packages
			let nameSegments = package.Name.Split('.')
			where nameSegments.Length >= 5 &&
				  nameSegments[^1] == arch.Value
			let hasGroupName = nameSegments.Length >= 6 &&
				groupNames.Contains(nameSegments[3])
			let startSegment = hasGroupName ? 4 : 3
			select new QtModule(String.Join('.', nameSegments[startSegment..^1]), package)
		];
	}

	public static string GetNameWithoutVersion(this QtUpdate.Package package) {
		return String.Join('.', package.Name.Split('.')[3..]);
	}

	public static bool MatchesShortName(this QtUpdate.Archive archive, string shortName) {
		return archive.Identifier.StartsWithOrdinal($"{shortName}-");
	}

	public static string[] GetTargetDirectoryComponents(this QtUpdate.Archive archive) {
		if (String.IsNullOrWhiteSpace(archive.TargetDirectoryArgument)) {
			return [];
		}
		string[] components = archive.TargetDirectoryArgument.Split('/');
		if (components.ElementAtOrDefault(0) != "@TargetDir@") {
			throw new Exception("Extract operation must be rooted at target directory.");
		}
		return components[1..];
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
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
			_ => throw new ArgumentException("Host value is not recognized.")
		};
	}

	public bool IsWindows =>
		Value.StartsWith("windows", StringComparison.Ordinal);
}

public record QtTarget(string Value) {
	public override string ToString() => Value;

	public string ToUrlComponent() {
		return Value switch {
			"desktop" => "desktop",
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

	public string ToUrlComponent(string variant = "") {
		string dirForVersion = $"qt{Major}_{ToStringNoDots()}";
		string dirForVersionAndVariant = variant.Length != 0 ? $"{dirForVersion}_{variant}" : dirForVersion;
		return
			ToVersion() >= new Version(6, 8, 0) ? $"{dirForVersion}/{dirForVersionAndVariant}" :
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
	public static string GetUpdateDirectoryUrl(QtHost host, QtTarget target, QtVersion version) {
		return $"{Constants.TrustedMirror}/online/qtsdkrepository/{host.ToUrlComponent()}/{target.ToUrlComponent()}/{version.ToUrlComponent()}/";
	}

	public static async Task<QtUpdate> FetchUpdate(string updateDirectoryUrl, bool noHash = false, CancellationToken cancellationToken = default) {
		string url = $"{updateDirectoryUrl}Updates.xml";
		byte[] expectedHash = noHash ? Network.DummySha256Hash : await Network.GetPublishedSha256ForFileAsync(url, cancellationToken);
		string updateXml = await Network.GetAsUtf8StringWithSha256ValidationAsync(url, expectedHash, cancellationToken);
		return ParseUpdate(updateXml);
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

	public static QtArch GetDefaultArch(QtHost host, QtVersion version) {
		Version ver = version.ToVersion();
		string archValue = host.Value switch {
			"windows" =>
				ver >= new Version(6, 8, 0) ? "win64_msvc2022_64" :
				"win64_msvc2019_64",
			"windows_arm64" =>
				"win64_msvc2022_arm64",
			"linux" =>
				ver >= new Version(6, 7, 0) ? "linux_gcc_64" :
				"gcc_64",
			"linux_arm64" =>
				"linux_gcc_arm64",
			"mac" =>
				"clang_64",
			_ => throw new ArgumentException("Host value is not recognized.")
		};
		return new QtArch(archValue);
	}

	public static QtArch? GetHostArchForCrossCompilation(QtHost host, QtTarget target, QtVersion version, QtArch arch) {
		if (host.Value == "windows" && target.Value == "desktop") {
			string armSuffix = version.ToVersion() >= new Version(6, 8, 0) ? "_arm64_cross_compiled" : "_arm64";
			if (arch.Value.EndsWith(armSuffix, StringComparison.Ordinal)) {
				return new QtArch($"{arch.Value[..^armSuffix.Length]}_64");
			}
		}
		return null;
	}
}

public static class QtExtensionMethods {
	public static QtUpdate.Package[] GetBasePackages(this QtUpdate update) {
		return [..
			from package in update.Packages
			where package.Archives.Any(a => a.Identifier.StartsWith("qtbase-", StringComparison.Ordinal))
			let nameSegments = package.Name.Split('.')
			where nameSegments.Length == 4
			select package
		];
	}

	public static QtUpdate.Package GetBasePackage(this QtUpdate update, QtArch arch) {
		List<QtUpdate.Package> packages = GetBasePackages(update)
			.Where(p => p.Name.EndsWith($".{arch.Value}")).Take(2).ToList();
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
				groupNames.Any(n => n.Equals(nameSegments[3], StringComparison.OrdinalIgnoreCase))
			let startSegment = hasGroupName ? 4 : 3
			select new QtModule(String.Join('.', nameSegments[startSegment..^1]), package)
		];
	}

	public static string GetNameWithoutVersion(this QtUpdate.Package package) {
		return String.Join('.', package.Name.Split('.')[3..]);
	}

	public static bool MatchesShortName(this QtUpdate.Archive archive, string shortName) {
		return archive.Identifier.StartsWith($"{shortName}-", StringComparison.Ordinal);
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

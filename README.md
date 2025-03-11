# naqt
Command line tool for downloading and installing prebuilt Qt binaries.

This tool is unofficial and not affiliated with, endorsed by, or supported by Qt Group nor the Qt Project.

## Example
```
git clone --recurse-submodules https://github.com/jdpurcell/naqt
cd naqt
dotnet run -- install-qt windows desktop 6.8.2 win64_msvc2022_64
```

## Usage
```
Commands
   install-qt
      <host> <target> <version> [<arch>]
      [--outputdir <directory>]
      [--modules <module> [<module>...]]
      [--archives <archive> [<archive>...]]
      [--autodesktop]
      [--nohash]

   list-qt
      <host> <target> <version> [<arch>]
      [--nohash]
```

## Limitations
Does not support installation of modules that were moved to extensions in Qt 6.8 such as `qtwebengine` and `qtpdf`. Also doesn't support installation of Qt tools, source code, documentation, or examples. If you need any of these features, use the excellent [aqtinstall](https://github.com/miurahr/aqtinstall) instead.

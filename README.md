# naqt
Command line tool for downloading and installing prebuilt Qt binaries.

This tool is unofficial and not affiliated with, endorsed by, or supported by Qt Group nor the Qt Project.

## Example
```
git clone https://github.com/jdpurcell/naqt
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
The only supported target is currently `desktop`. Others such as `android`, `ios`, and `wasm` are not implemented.

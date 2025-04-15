# naqt
Command line tool for downloading and installing prebuilt Qt binaries.

This tool is unofficial and not affiliated with, endorsed by, or supported by Qt Group nor the Qt Project.

## Example
Assuming you have the dotnet runtime, download [prebuilt binaries](https://github.com/jdpurcell/naqt/releases/download/latest/naqt.zip) and run:
```
dotnet naqt.dll install-qt windows desktop 6.8.3 win64_msvc2022_64
```
Or if you have the full dotnet SDK and prefer to run from source:
```
git clone --recurse-submodules https://github.com/jdpurcell/naqt
cd naqt
dotnet run -- install-qt windows desktop 6.8.3 win64_msvc2022_64
```

## Usage
```
Commands
   install-qt
      <host> <target> <version> [<arch>]
      [--outputdir <directory>]
      [--modules <module> [<module>...]]
      [--extensions <extension> [<extension>...]]
      [--archives <archive> [<archive>...]]
      [--autodesktop]
      [--mirror <baseurl>]
      [--nohash]

   list-qt
      <host> <target> <version> [<arch>]
      [--mirror <baseurl>]
      [--nohash]
```

The syntax is mostly compatible with `aqtinstall` so you can use [this site](https://ddalcino.github.io/aqt-list-server/) to help, although note that short argument names (e.g. `-m`) aren't supported.

## GitHub Actions
Already using `jurplel/install-qt-action`? Just change it to use [my fork](https://github.com/jdpurcell/install-qt-action) on the `v5` branch and set the `use-naqt` option to `true`. You can also set `setup-python` to `false` since this tool doesn't need it. Example:
```yaml
- name: Install Qt
  uses: jdpurcell/install-qt-action@v5
  with:
    use-naqt: true
    setup-python: false
    # .. other options ..
```

## Limitations
* The full version number (major.minor.patch, no wildcards) must be specified.
* Cannot install Qt tools, source code, documentation, or examples.
* When cross-compiling (e.g. WASM, Android, iOS), it's recommended to perform installation in a single step with `--autodesktop`. `install-qt-action` enables this option by default. It is possible, however, to perform the host and target installations in separate steps, but do not pass `--autodesktop` in this case.

If you need any of these features, use the excellent [aqtinstall](https://github.com/miurahr/aqtinstall) instead.

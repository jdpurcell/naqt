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
git clone https://github.com/jdpurcell/naqt
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
      [--dryrun]

   list-qt
      <host> <target> <version> [<arch>]
      [--mirror <baseurl>]
      [--nohash]
```

You can use [this site](https://jdpurcell.github.io/naqt-command-builder/) to help build the install command.

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

You can use [this site](https://jdpurcell.github.io/naqt-command-builder/) to help build the install script.

## Limitations
* The full version number (major.minor.patch, no wildcards) must be specified.
* Cannot install Qt tools, source code, documentation, or examples.
* When cross-compiling (e.g. WASM, Android, iOS), it's recommended to perform installation in a single step with `--autodesktop`. `install-qt-action` enables this option by default. It is possible, however, to perform the host and target installations in separate steps, but do not pass `--autodesktop` in this case.

If you need any of these features, use the excellent [aqtinstall](https://github.com/miurahr/aqtinstall) instead.

## Syntax vs aqtinstall
* For WASM, `naqt` uses target `wasm` for all versions, whereas `aqt` uses target `desktop` for versions < 6.7.
* `naqt` has an `--extensions` switch although its use is optional; it detects known extensions (`qtpdf`, `qtwebengine`) requested via `--modules`, similar to `aqt`.
* `naqt` switch `--mirror` is `aqt` switch `--base`.
* `naqt` switch `--nohash` is `aqt` switch `--UNSAFE-ignore-hash`.
* `naqt` switch `--dryrun` is `aqt` switch `--dry-run`.

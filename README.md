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

The syntax is mostly compatible with `aqtinstall` so you can use [this site](https://ddalcino.github.io/aqt-list-server/) to help, although note that short argument names (e.g. `-m`) aren't supported.

## GitHub Actions
Already using `jurplel/install-qt-action`? Just change it to use my fork on the `v5` branch and set the `use-naqt` option to `true`. You can also set `setup-python` to `false` since this tool doesn't need it. Example:
```yaml
- name: Install Qt
  uses: jdpurcell/install-qt-action@v5
  with:
    use-naqt: true
    setup-python: false
    # .. other options ..
```

## Limitations
Does not support installation of modules that were moved to extensions in Qt 6.8 such as `qtwebengine` and `qtpdf`. Also doesn't support installation of Qt tools, source code, documentation, or examples. If you need any of these features, use the excellent [aqtinstall](https://github.com/miurahr/aqtinstall) instead.

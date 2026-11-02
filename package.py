"""Package the published Windows game and complete source after validation."""

from pathlib import Path
import zipfile


ROOT = Path(__file__).resolve().parent
PREVIEWS = ("smoke-test.png", "palette-preview.png", "blocks-preview.png")
OPTIONAL_PREVIEWS = ("engine-preview.png", "gameplay-preview.png")
REPORTS = (
    "benchmark-results.json",
    "engine-smoke-results.json",
    "circuit-benchmark-results.json",
    "gameplay-smoke-results.json",
)
EXCLUDED_SOURCE_DIRS = {"bin", "obj", "dist"}


def require_files(paths):
    missing = [str(path.relative_to(ROOT)) for path in paths if not path.is_file()]
    if missing:
        raise SystemExit("Required files missing: " + ", ".join(missing))


def write_archive(destination, entries):
    """Validate a temporary ZIP before replacing an earlier distribution."""
    temporary = destination.with_suffix(".zip.tmp")
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name, source in sorted(entries.items()):
                archive.write(source, name)
        with zipfile.ZipFile(temporary) as archive:
            bad_file = archive.testzip()
            if bad_file is not None:
                raise RuntimeError(f"ZIP verification failed: {bad_file}")
            if len(archive.namelist()) != len(entries):
                raise RuntimeError("ZIP entry count does not match the manifest")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    print(f"Verified {destination} ({destination.stat().st_size:,} bytes, {len(entries)} files)")


def main():
    published = ROOT / "dist" / "windows"
    licenses = sorted(path for path in (ROOT / "licenses").rglob("*") if path.is_file())
    sources = sorted(
        path for path in ROOT.rglob("*.cs")
        if not EXCLUDED_SOURCE_DIRS.intersection(path.relative_to(ROOT).parts[:-1])
    ) + sorted(ROOT.glob("*.csproj"))
    docs = [ROOT / name for name in ("README.md", "ENGINE-NOTES.md", ".gitignore", "package.py", *PREVIEWS)]
    reports = [ROOT / name for name in REPORTS if (ROOT / name).is_file()]
    optional_previews = [ROOT / name for name in OPTIONAL_PREVIEWS if (ROOT / name).is_file()]
    require_files([
        published / "BlockCircuit.exe",
        published / "BlockCircuit.dll",
        published / "BlockCircuit.runtimeconfig.json",
        published / "raylib.dll",
        ROOT / "licenses" / "raylib.txt",
        ROOT / "licenses" / "Raylib-cs.txt",
        *docs,
    ])
    if not sources or not list(ROOT.glob("*.csproj")):
        raise SystemExit("C# sources and a project file are required")

    windows = {
        path.relative_to(published).as_posix(): path
        for path in published.rglob("*")
        if path.is_file()
    }
    # Always include the current documentation, even when publish leaves an older copy.
    windows["README.md"] = ROOT / "README.md"
    windows["ENGINE-NOTES.md"] = ROOT / "ENGINE-NOTES.md"
    windows.update({path.name: path for path in reports})
    windows.update({path.relative_to(ROOT).as_posix(): path for path in licenses})
    source = {
        "BlockCircuit/" + path.relative_to(ROOT).as_posix(): path
        for path in [*sources, *docs, *licenses, *reports, *optional_previews]
    }

    write_archive(ROOT.parent / "BlockCircuit-windows-x64.zip", windows)
    write_archive(ROOT.parent / "BlockCircuit-source.zip", source)


if __name__ == "__main__":
    main()

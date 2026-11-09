"""Read Unity archive metadata without extracting or executing vendor content."""
import argparse
import collections
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
import tarfile


def inspect(path):
    before = path.stat()
    entries, texts = {}, {}
    with tarfile.open(path, mode='r|gz') as archive:
        for member in archive:
            parts = PurePosixPath(member.name).parts
            if not member.isfile() or len(parts) != 2 or not re.fullmatch('[0-9a-f]{32}', parts[0]):
                continue
            if parts[1] == 'pathname':
                lines = archive.extractfile(member).read().decode(errors='replace').splitlines()
                if not lines:
                    raise ValueError('Empty archive pathname')
                name = lines[0].strip('\x00')
                vendor_path = PurePosixPath(name)
                if not vendor_path.parts or vendor_path.parts[0] != 'Assets' or '..' in vendor_path.parts:
                    raise ValueError('Unsafe archive pathname')
                entries[parts[0]] = name
            elif parts[1] == 'asset' and member.size < 128000:
                data = archive.extractfile(member).read()
                if b'\x00' not in data:
                    texts[parts[0]] = data.decode(errors='replace')
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError('Archive changed while reading: ' + str(path))
    # This index is metadata, not redistribution of vendor shader/source contents.
    relevant = {}
    for guid, text in texts.items():
        name = entries.get(guid, '')
        if name.lower().endswith(('.shader', '.txt', '.md')):
            relevant[name] = {'sha256': hashlib.sha256(text.encode()).hexdigest(),
                              'pipeline': 'URP' if 'UniversalPipeline' in text else 'HDRP' if 'HDRenderPipeline' in text else 'unspecified',
                              'mentions_license': 'license' in text.lower() or 'licence' in text.lower()}
    return {'source': str(path), 'bytes': before.st_size, 'mtime_ns': before.st_mtime_ns,
            'entries': entries, 'extensions': dict(collections.Counter(PurePosixPath(n).suffix.lower() for n in entries.values())),
            'text_metadata': relevant, 'validation': 'archive_stream_read_only_not_unity_import_or_license_approval'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output')
    parser.add_argument('archives', nargs='+')
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    for i, value in enumerate(args.archives):
        path = Path(value)
        destination = output / (path.stem + '.json')
        if destination.exists():
            raise FileExistsError(destination)
        result = inspect(path)
        destination.write_text(json.dumps(result, ensure_ascii=False, indent=2))
        print(path.name, len(result['entries']), result['extensions'], flush=True)


if __name__ == '__main__':
    main()

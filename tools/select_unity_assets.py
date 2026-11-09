"""Extract a dependency-closed selection without executing vendor scripts.

Scenes require an explicit opt-in. Existing GUIDs can be reused without overwriting
their converted materials or project adaptations. Import is an explicit Editor operation.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import tarfile


BLOCKED = {'.cs', '.dll', '.exe', '.unity', '.unitypackage', '.psd', '.psb'}
SERIALIZED = {'.prefab', '.mat', '.controller', '.anim', '.asset', '.shadergraph', '.shadersubgraph', '.shader'}


def select(index_path, seed_names, output, *, allow_scenes=False, reuse=None):
    index = json.loads(index_path.read_text())
    entries = index['entries']
    if any(not re.fullmatch('[0-9a-f]{32}', guid) for guid in entries):
        raise ValueError('Invalid asset GUID')
    for name in entries.values():
        path = PurePosixPath(name)
        if not path.parts or path.parts[0] != 'Assets' or '..' in path.parts:
            raise ValueError('Unsafe vendor path')
    seeds = [g for g, n in entries.items() if n in seed_names]
    if len(seeds) != len(set(seed_names)):
        raise ValueError('Seed missing or ambiguous')
    blocked = BLOCKED - {'.unity'} if allow_scenes else BLOCKED
    reused = reuse or {}
    for guid, target in reused.items():
        if not re.fullmatch('[0-9a-f]{32}', guid) or not target.startswith('Assets/LocalThirdParty/') or '..' in PurePosixPath(target).parts:
            raise ValueError('Unsafe reused asset')
    stage = output / 'stage'
    stage.mkdir(parents=True, exist_ok=False)
    source = Path(index['source'])
    before = source.stat()
    with tarfile.open(source, mode='r|gz') as archive:
        for member in archive:
            parts = PurePosixPath(member.name).parts
            if not member.isfile() or len(parts) != 2 or parts[0] not in entries or parts[1] not in ('asset', 'asset.meta'):
                continue
            if PurePosixPath(entries[parts[0]]).suffix.lower() in blocked:
                continue
            folder = stage / parts[0]
            folder.mkdir(exist_ok=True)
            with (folder / parts[1]).open('wb') as dest:
                import shutil
                shutil.copyfileobj(archive.extractfile(member), dest)
    after = source.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError('Source changed while reading')
    encoded = {g: bytes.fromhex(''.join(g[i:i+2][::-1] for i in range(0, 32, 2))) for g in entries}
    chosen, excluded, queue = set(), set(), list(seeds)
    while queue:
        guid = queue.pop()
        if guid in chosen:
            continue
        suffix = PurePosixPath(entries[guid]).suffix.lower()
        if suffix in blocked:
            excluded.add(entries[guid])
            continue
        folder = stage / guid
        if not (folder / 'asset').is_file():
            continue
        chosen.add(guid)
        data = (folder / 'asset.meta').read_bytes() if (folder / 'asset.meta').exists() else b''
        if suffix in SERIALIZED or (allow_scenes and suffix == '.unity'):
            data += (folder / 'asset').read_bytes()
        refs = {g.decode().lower() for g in re.findall(rb'guid:\s*([0-9a-fA-F]{32})', data)}
        if b'\x00' in data:
            refs.update(g for g, code in encoded.items() if code in data)
        queue.extend(g for g in refs if g in entries and g not in chosen)
    files = []
    with tarfile.open(output / 'selection.unitypackage', mode='w:gz') as archive:
        for guid in sorted(chosen):
            original = PurePosixPath(entries[guid])
            if original.parts[0] != 'Assets' or '..' in original.parts:
                raise ValueError('Unsafe vendor path')
            target = str(PurePosixPath('Assets/LocalThirdParty') / PurePosixPath(*original.parts[1:]))
            if guid in reused:
                files.append({'guid': guid, 'source_path': str(original), 'path': reused[guid], 'reused': True})
                continue
            if (Path('unity') / target).exists():
                raise FileExistsError(target)
            payload = target.encode()
            info = tarfile.TarInfo(guid + '/pathname'); info.size = len(payload)
            archive.addfile(info, io.BytesIO(payload))
            folder = stage / guid
            for leaf in ('asset', 'asset.meta'):
                if (folder / leaf).is_file():
                    archive.add(folder / leaf, arcname=guid + '/' + leaf)
            data = (folder / 'asset').read_bytes()
            files.append({'guid': guid, 'source_path': str(original), 'path': target,
                           'bytes': len(data), 'source_sha256': hashlib.sha256(data).hexdigest(), 'reused': False})
    report = {'source': str(source), 'source_bytes': before.st_size, 'seeds': seed_names,
              'files': files, 'excluded': sorted(excluded), 'license': None,
              'license_status': 'unverified_user_download', 'in_game_approved': False}
    (output / 'selection.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    print(len(seeds), 'seeds;', len(files), 'dependencies;', sum(not f.get('reused') for f in files), 'new;', sum(f.get('bytes', 0) for f in files), 'new bytes; excluded', sorted(excluded))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('index'); parser.add_argument('seeds'); parser.add_argument('output')
    parser.add_argument('--allow-scenes', action='store_true')
    parser.add_argument('--reuse', help='JSON GUID-to-existing-path map exported by the Editor')
    args = parser.parse_args()
    select(Path(args.index), json.loads(Path(args.seeds).read_text()), Path(args.output),
           allow_scenes=args.allow_scenes, reuse=json.loads(Path(args.reuse).read_text()) if args.reuse else None)

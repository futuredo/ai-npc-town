"""Synthetic archives only: no commercial assets, external disk writes or Unity needed."""
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

from index_unity_packages import inspect
from select_unity_assets import select


class SelectionTests(unittest.TestCase):
    def archive(self, root, paths, data):
        source = root / 'source.unitypackage'
        with tarfile.open(source, 'w:gz') as archive:
            for guid, name in paths.items():
                for leaf, content in [('pathname', name.encode()), ('asset', data[guid]), ('asset.meta', ('guid: ' + guid).encode())]:
                    info = tarfile.TarInfo(guid + '/' + leaf); info.size = len(content)
                    archive.addfile(info, io.BytesIO(content))
        return source

    def test_text_and_binary_closure_excludes_vendor_code(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            prefab, material, texture, script = [str(i) * 32 for i in range(1, 5)]
            packed = bytes.fromhex(''.join(texture[i:i+2][::-1] for i in range(0, 32, 2)))
            paths = {prefab: 'Assets/Test.prefab', material: 'Assets/Test.mat', texture: 'Assets/Test.png', script: 'Assets/Vendor.cs'}
            data = {prefab: ('guid: ' + material + '\nguid: ' + script).encode(), material: b'\x00' + packed, texture: b'texture fixture', script: b'not executed'}
            source = self.archive(root, paths, data)
            index = root / 'index.json'; index.write_text(json.dumps(inspect(source)))
            output = root / 'selection'
            select(index, ['Assets/Test.prefab'], output)
            report = json.loads((output / 'selection.json').read_text())
            self.assertEqual({prefab, material, texture}, {f['guid'] for f in report['files']})
            self.assertEqual(['Assets/Vendor.cs'], report['excluded'])
            with tarfile.open(output / 'selection.unitypackage') as archive:
                names = [archive.extractfile(m).read().decode() for m in archive if m.name.endswith('/pathname')]
            self.assertTrue(all(n.startswith('Assets/LocalThirdParty/') for n in names))
            self.assertFalse((output / 'stage' / script).exists())
            with self.assertRaises(FileExistsError): select(index, ['Assets/Test.prefab'], output)

    def test_unsafe_paths_and_guid_are_rejected_before_staging(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value); guid = 'a' * 32
            source = self.archive(root, {guid: 'Assets/../outside.cs'}, {guid: b''})
            with self.assertRaises(ValueError): inspect(source)
            index = root / 'index.json'; index.write_text(json.dumps({'entries': {'../escape': 'Assets/Test.prefab'}}))
            with self.assertRaises(ValueError): select(index, ['Assets/Test.prefab'], root / 'output')
            self.assertFalse((root / 'output').exists())

    def test_explicit_scene_opt_in_reuses_material_without_overwriting(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value); scene, material = 'a' * 32, 'b' * 32
            source = self.archive(root, {scene: 'Assets/Ready.unity', material: 'Assets/Ready.mat'},
                                  {scene: ('guid: ' + material).encode(), material: b'original Standard material'})
            index = root / 'index.json'; index.write_text(json.dumps(inspect(source)))
            select(index, ['Assets/Ready.unity'], root / 'blocked')
            self.assertEqual([], json.loads((root / 'blocked/selection.json').read_text())['files'])
            select(index, ['Assets/Ready.unity'], root / 'ready', allow_scenes=True,
                   reuse={material: 'Assets/LocalThirdParty/Ready.mat'})
            report = json.loads((root / 'ready/selection.json').read_text())
            self.assertTrue(next(f for f in report['files'] if f['guid'] == material)['reused'])
            with tarfile.open(root / 'ready/selection.unitypackage') as archive:
                self.assertFalse(any(m.name.startswith(material + '/') for m in archive))
                self.assertTrue(any(m.name == scene + '/asset' for m in archive))


if __name__ == '__main__': unittest.main()

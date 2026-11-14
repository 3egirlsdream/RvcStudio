"""Exercise a server-generated encrypted fixture through the actual engine, without opening audio devices.

Create the fixture with RvcStoreTests and RVC_STORE_INTEROP_ROOT / RVC_STORE_INTEROP_MODELS.
Run with the bundled runtime: runtime/python.exe -X utf8 -I packaging/Test-StoreEngine.py <fixture-dir>
"""
import copy
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def main():
    import os
    os.chdir(ROOT)
    from engine.store_crypto import load, verify
    import numpy as np
    from engine.realtime_engine import RealtimeEngine
    from infer.rtrvc import RVC

    fixture = Path(sys.argv[1]).resolve()
    license = json.loads((fixture / 'license.json').read_text(encoding='utf-8-sig'))
    public = (fixture / 'public.pem').read_text()
    manifest = verify(license['manifest'], public)
    snapshot = json.loads((fixture / 'snapshot.json').read_text(encoding='utf-8-sig'))
    account = verify(snapshot, public)['accountId']
    payload = dict(model_id=manifest['modelId'], revision=manifest['revision'], account_id=account,
                   package_path=str(fixture / 'content.rvcm'), content_key=license['contentKey'],
                   manifest=license['manifest'], grant=license['grant'], snapshot=snapshot)
    before = sorted(str(p) for p in fixture.rglob('*') if p.suffix in ('.pth', '.index'))

    def rejected(candidate):
        try:
            load(candidate, public)
        except (ValueError, PermissionError, KeyError):
            return
        raise AssertionError('Invalid authorization/package was accepted')

    candidate = copy.deepcopy(payload); candidate['account_id'] = 'different-account'; rejected(candidate)
    candidate = copy.deepcopy(payload); candidate['snapshot'] = json.loads((fixture / 'nonmember.json').read_text()); rejected(candidate)
    candidate = copy.deepcopy(payload); candidate['manifest']['signature'] = 'AAAA'; rejected(candidate)
    with tempfile.TemporaryDirectory(prefix='rvc-store-damaged-') as directory:
        broken = Path(directory) / 'content.rvcm'
        with (fixture / 'content.rvcm').open('rb') as source, broken.open('wb') as target:
            while block := source.read(4194304): target.write(block)
        with broken.open('r+b') as stream:
            stream.seek(-1, 2); value = stream.read(1); stream.seek(-1, 2); stream.write(bytes([value[0] ^ 1]))
        candidate = copy.deepcopy(payload); candidate['package_path'] = str(broken); rejected(candidate)

    engine = RealtimeEngine()
    try:
        engine.store_public_key = public
        metadata = engine.load_store_model(payload)  # Accepts the old, signed offline membership snapshot.
        assert metadata['version'] == manifest['version']
        engine.gui_config.index_rate = 0.5
        engine.gui_config.pitch = 0
        engine.rvc = RVC(0, 0.0, engine.gui_config.pth_path, '', 0.5, engine.config,
                         store_data=engine._store_data)
        engine.samplerate = engine.rvc.tgt_sr
        engine.channels = 1
        engine._prepare_buffers()
        assert engine.stream is None
        for _ in range(3):
            phase = np.arange(engine.block_frame, dtype=np.float32) / engine.samplerate
            audio = (0.04 * np.sin(2 * np.pi * 220 * phase)).reshape(-1, 1).astype(np.float32)
            output = np.zeros_like(audio)
            engine._audio_callback(audio, output, engine.block_frame, None, None)
            assert np.isfinite(output).all()
            assert not engine.last_error, engine.last_error
        engine.stop()
        assert engine._store_data is None and engine.rvc is None
        engine.load_store_model(payload)
        engine.unload_store_model()
        assert engine._store_data is None and engine.gui_config.model_source == 'local'
    finally:
        engine.close()
    assert before == sorted(str(p) for p in fixture.rglob('*') if p.suffix in ('.pth', '.index'))
    print('PASS: .NET ciphertext/signatures -> in-memory torch + FAISS -> realtime processing; '
          'offline snapshot, account isolation, downgrade, tamper rejection, unload; no plaintext files/audio stream.')


if __name__ == '__main__':
    main()

import base64
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

spec = importlib.util.spec_from_file_location("publisher", Path(__file__).with_name("Publish-HuggingFaceRelease.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class FakeApi:
    def __init__(self):
        self.files = {}
        self.uploads = []
        self.fail = None

    def model_info(self, *_args, **_kwargs):
        return SimpleNamespace(siblings=[SimpleNamespace(rfilename=name, size=len(data), lfs=None,
            blob_id=hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()) for name, data in self.files.items()])

    def upload_file(self, **kwargs):
        path = kwargs["path_in_repo"]
        if path == self.fail:
            raise RuntimeError("injected upload failure")
        self.uploads.append(path)
        self.files[path] = Path(kwargs["path_or_fileobj"]).read_bytes()


class PublishingTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="rvc-publishing-test-")
        self.root = Path(self.directory.name)
        self.installer = self.root / "RVC-Studio-NVIDIA-Setup.exe"
        self.package = self.root / "RVC-Studio-Update.zip"
        self.installer.write_bytes(b"installer")
        self.package.write_bytes(b"package")
        self.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        self.public = self.key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
        self.api = FakeApi()
        self.make_manifest()

    def tearDown(self):
        self.directory.cleanup()

    def make_manifest(self, version="1.2.0"):
        def artifact(path):
            data = path.read_bytes()
            return dict(url=f"https://huggingface.co/test/repo/resolve/main/releases/v{version}/{path.name}?download=true", size=len(data), sha256=hashlib.sha256(data).hexdigest())
        payload = json.dumps(dict(version=version, package=artifact(self.package), installer=artifact(self.installer))).encode()
        data = json.dumps(dict(payload=base64.b64encode(payload).decode(), signature=base64.b64encode(self.key.sign(payload, padding.PKCS1v15(), hashes.SHA256())).decode())).encode()
        (self.root / "stable.json").write_bytes(data)
        return data

    def publish(self, activate=False, current=None):
        return publisher.publish(self.api, "test/repo", "1.2.0", self.installer, self.root, "test-token", activate, self.public, current)

    def test_upload_does_not_activate_until_explicit_final_step(self):
        self.publish()
        self.assertNotIn("updates/stable.json", self.api.files)
        self.assertEqual(3, len(self.api.uploads))
        self.publish(activate=True)
        self.assertEqual("updates/stable.json", self.api.uploads[-1])
        self.assertEqual(4, len(self.api.uploads))

    def test_upload_failure_never_activates(self):
        self.api.fail = "releases/v1.2.0/RVC-Studio-Update.zip"
        with self.assertRaises(RuntimeError):
            self.publish()
        self.assertNotIn("updates/stable.json", self.api.files)

    def test_missing_package_prevents_activation(self):
        with self.assertRaisesRegex(RuntimeError, "missing release"):
            self.publish(activate=True)
        self.assertEqual([], self.api.uploads)

    def test_release_files_are_immutable(self):
        self.publish()
        self.package.write_bytes(b"changed")
        self.make_manifest()
        with self.assertRaisesRegex(RuntimeError, "hash differs"):
            self.publish()
        self.assertNotIn("updates/stable.json", self.api.files)

    def test_bad_signature_and_artifact_corruption_are_rejected_before_upload(self):
        data = json.loads((self.root / "stable.json").read_text())
        data["payload"] = base64.b64encode(b"{}").decode()
        (self.root / "stable.json").write_text(json.dumps(data))
        with self.assertRaises(Exception):
            self.publish()
        self.assertEqual([], self.api.uploads)
        self.make_manifest()
        self.installer.write_bytes(b"corrupted")
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            self.publish()
        self.assertEqual([], self.api.uploads)

    def test_cannot_downgrade_active_version(self):
        current = self.make_manifest("1.3.0")
        self.make_manifest()
        with self.assertRaisesRegex(RuntimeError, "downgrade"):
            self.publish(activate=True, current=current)
        self.assertEqual([], self.api.uploads)


if __name__ == "__main__":
    unittest.main()

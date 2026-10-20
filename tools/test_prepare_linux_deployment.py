"""Exercise release assembly without publishing or connecting to external services."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest


class LinuxDeploymentTests(unittest.TestCase):
    def test_archive_is_independent_of_output_name_and_source_metadata(self):
        script = Path(__file__).with_name("prepare-linux-deployment.py")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root / "repo"
            (repo / "tools").mkdir(parents=True)
            shutil.copyfile(script, repo / "tools" / script.name)
            inputs = {
                "profiles/all/profile.yaml": "name: all\n",
                "packets/example.yaml": "structures: {}\n",
                "deploy/docker/Dockerfile.published": "FROM scratch\n",
                "deploy/docker/compose.external-db.yaml": "services: {}\n",
            }
            for name, content in inputs.items():
                destination = repo / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_text(content, encoding="utf-8")
            subprocess.run(["git", "init", "--quiet", str(repo)], check=True)
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run([
                "git", "-c", "user.name=Release Test", "-c", "user.email=test@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture",
            ], cwd=repo, check=True)
            published = root / "published"
            (published / "profiles/all").mkdir(parents=True)
            (published / "packets").mkdir()
            elf = bytearray(64)
            elf[:4] = b"\x7fELF"
            elf[18:20] = (62).to_bytes(2, "little")
            (published / "BubbleFighter.Fantasy.Host").write_bytes(elf)
            (published / "BubbleFighter.Fantasy.Host.dll").write_bytes(b"fake assembly")
            (published / "profiles/all/profile.yaml").write_text("old profile")
            (published / "packets/example.yaml").write_text("old packets")
            (published / ".env").write_text("excluded fixture")
            (published / "profiles/all/local.patch.yaml").write_text("excluded fixture")
            archives = []
            for index in range(2):
                output = root / ("release-a" if index == 0 else "release-b")
                result = subprocess.run([
                    sys.executable, str(repo / "tools" / script.name),
                    "--published", str(published), "--output", str(output),
                ], check=True, capture_output=True, text=True)
                archive = output.with_suffix(".tar.gz")
                archives.append(archive.read_bytes())
                digest = hashlib.sha256(archives[-1]).hexdigest()
                self.assertIn("SHA256: " + digest, result.stdout)
                self.assertEqual((output / "server/profiles/all/profile.yaml").read_text(), inputs["profiles/all/profile.yaml"])
                manifest = json.loads((output / "DEPLOYMENT-MANIFEST.json").read_text())
                self.assertEqual(set(manifest["files"]), {
                    "server/BubbleFighter.Fantasy.Host", "server/BubbleFighter.Fantasy.Host.dll",
                    "server/profiles/all/profile.yaml", "server/profiles/all/deployment.patch.yaml",
                    "server/packets/example.yaml", "Dockerfile", "compose.yaml", "SOURCE_COMMIT.txt",
                })
                with tarfile.open(archive) as package:
                    members = package.getmembers()
                    self.assertEqual([m.name for m in members], sorted(m.name for m in members))
                    self.assertEqual({m.name for m in members}, set(manifest["files"]) | {"DEPLOYMENT-MANIFEST.json"})
                    for member in members:
                        self.assertEqual((member.mtime, member.uid, member.gid, member.uname, member.gname), (0, 0, 0, "", ""))
                        self.assertEqual(member.mode, 0o755 if member.name == "server/BubbleFighter.Fantasy.Host" else 0o644)
                        self.assertEqual(package.extractfile(member).read(), (output / member.name).read_bytes())
                # Exercise changed source metadata, not just two runs during the same second.
                for source in list(published.rglob("*")) + list((repo / "profiles").rglob("*")):
                    if source.is_file():
                        os.utime(source, (1700000000, 1700000000))
                        os.chmod(source, 0o600)
            self.assertEqual(archives[0], archives[1])


if __name__ == "__main__":
    unittest.main()

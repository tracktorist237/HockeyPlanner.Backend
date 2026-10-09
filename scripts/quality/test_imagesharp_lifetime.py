"""Real Linux IPC/flock/process lifetime regressions; harmless workers only.

Windows local runs delegate to installed WSL; CI runs native Linux. No skip or
emulated flock path here. No Docker/VPS commands, private license or real config.
"""
import os
from pathlib import Path
import select
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/staging"))
from transaction import admission, BARRIER

WORKER = '''import os,socket,signal,sys,subprocess
s=socket.socket(socket.AF_UNIX);s.connect(sys.argv[1])
fd=int(sys.argv[2])
if sys.argv[3]=='closed-fd': os.close(fd)
if sys.argv[3]=='descendant':
    p=subprocess.Popen([sys.executable,__file__,sys.argv[1],str(fd),'leaf'],pass_fds=(fd,))
if sys.argv[3]=='escaped':
    p=subprocess.Popen([sys.executable,__file__,sys.argv[1],str(fd),'closed-fd'],pass_fds=(fd,),
                       start_new_session=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
def stop(signum,frame):
    s.sendall(b'DONE\\n');s.close();sys.exit(0)
signal.signal(signal.SIGTERM,stop)
s.sendall((str(os.getpid())+' READY\\n').encode())
s.recv(1)
s.sendall(b'DONE\\n');s.close()
'''
OWNER = '''import os,fcntl,sys
sys.path.insert(0,sys.argv[1]);from transaction import supervised,Cancelled
lock=sys.argv[2];fd=os.open(lock,os.O_RDWR);fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
from pathlib import Path
try:
    with supervised(fd,Path(lock),'a'*40) as tx:
        tx.uncertain=True
        tx.run([sys.executable,sys.argv[3],sys.argv[4],str(fd),sys.argv[5]],timeout=5 if sys.argv[5]=='timeout' else 30)
        tx.uncertain=False
except BaseException: sys.exit(42)
finally: os.close(fd)
'''


class ProcessLifetimeTests(unittest.TestCase):
    def scenario(self, mode):
        if os.name == "nt":
            path = "/mnt/" + ROOT.drive[0].lower() + ROOT.as_posix()[2:] + "/scripts/quality/test_imagesharp_lifetime.py"
            result = subprocess.run(["wsl", "-d", "Ubuntu-24.04", "--", "python3", path, "--scenario", mode],
                                    capture_output=True, text=True, timeout=45)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("PASS real Linux", result.stdout)
            return
        import fcntl
        if mode == "filesystem":
            from check_imagesharp_build import safe_host_path
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); license_file = root / "private.lic"; license_file.write_text("synthetic only")
                directory = root / "runtime"; directory.mkdir()
                (directory / "unrelated").write_text("safe")
                safe_host_path(directory, license_file)
                os.link(license_file, directory / "hardlink")
                with self.assertRaises(ValueError):
                    safe_host_path(directory, license_file)
                (directory / "hardlink").unlink()
                (directory / "symlink").symlink_to(license_file)
                with self.assertRaises(ValueError):
                    safe_host_path(directory, license_file)
                (directory / "symlink").unlink()
                nested = directory
                for _ in range(65):
                    nested = nested / "d"; nested.mkdir()
                with self.assertRaises(ValueError):
                    safe_host_path(directory, license_file)
            return
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            lock = root / "deploy.lock"; lock.write_text("x"); lock.chmod(0o600)
            worker = root / "worker.py"; worker.write_text(WORKER)
            owner = root / "owner.py"; owner.write_text(OWNER)
            endpoint = str(root / "ipc")
            server = socket.socket(socket.AF_UNIX); server.bind(endpoint); server.listen(); server.settimeout(15)
            self.addCleanup(server.close)
            process = subprocess.Popen([sys.executable, str(owner), str(ROOT / "scripts/staging"),
                                        str(lock), str(worker), endpoint, mode], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            connections, pidfds = [], []
            try:
                for _ in range(2 if mode in ("descendant", "escaped") else 1):
                    connection, _ = server.accept(); connection.settimeout(15)
                    connections.append(connection)
                    ready = connection.makefile("rb", buffering=0).readline().decode().split()
                    self.assertEqual(ready[1], "READY")
                    pidfds.append(os.pidfd_open(int(ready[0])))
                if mode == "normal":
                    with lock.open("r+") as second:
                        with self.assertRaises(BlockingIOError):
                            fcntl.flock(second, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        connections[0].sendall(b'X')
                        process.communicate(timeout=15)
                        self.assertEqual(process.returncode, 0)
                        self.assertFalse((root / BARRIER).exists())
                        self.assertFalse(list(root.glob("transaction-*")))
                        fcntl.flock(second, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        admission(lock)
                    return
                # Worker has reached an IPC barrier and remains capable of mutation.
                if mode == "escaped":
                    connections[0].sendall(b'X')  # Leader succeeds; escaped orphan is still active.
                elif mode != "timeout":
                    process.send_signal(signal.SIGTERM if mode in ("cancel", "descendant") else signal.SIGKILL)
                process.communicate(timeout=15)
                self.assertNotEqual(process.returncode, 0)
                self.assertTrue((root / BARRIER).exists())
                self.assertTrue(list(root.glob("transaction-*")))  # No unsafe private cleanup.
                with lock.open("r+") as second:
                    if mode not in ("cancel", "descendant", "escaped", "timeout", "closed-fd"):
                        with self.assertRaises(BlockingIOError):
                            fcntl.flock(second, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    # Even if a descendant closes the FD or daemon work loses the
                    # CLI, admission stays closed until explicit confirmed recovery.
                    with self.assertRaises(ValueError):
                        admission(lock)
                    if mode not in ("cancel", "descendant", "escaped", "timeout"):
                        for connection in connections:
                            connection.sendall(b'X')
                    for fd in pidfds:
                        poll = select.poll(); poll.register(fd, select.POLLIN)
                        self.assertTrue(poll.poll(15000), "Worker did not definitely exit")
                    fcntl.flock(second, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    with self.assertRaises(ValueError):
                        admission(lock)
                    # Synthetic operator recovery AFTER all workers definitely exit.
                    for directory in root.glob("transaction-*"):
                        shutil.rmtree(directory)
                    (root / BARRIER).unlink()
                    admission(lock)
            finally:
                if process.poll() is None:
                    process.kill(); process.communicate(timeout=15)
                for connection in connections:
                    connection.close()
                for fd in pidfds:
                    os.close(fd)

    def test_sigkill_owner_cannot_admit_writer_while_child_active(self):
        self.scenario("kill")

    def test_controlled_cancellation_reaps_child_and_requires_recovery(self):
        self.scenario("cancel")

    def test_descendant_process_group_is_terminated_on_cancellation(self):
        self.scenario("descendant")

    def test_timeout_terminates_worker_and_retains_barrier(self):
        self.scenario("timeout")

    def test_barrier_blocks_writer_when_orphan_closes_inherited_fd(self):
        self.scenario("closed-fd")

    def test_normal_completion_cleans_then_readmits_writer(self):
        self.scenario("normal")

    def test_real_linux_directory_aliases_are_blocked_without_reading_contents(self):
        self.scenario("filesystem")

    def test_successful_leader_cannot_leave_escaped_fd_closing_mutator(self):
        self.scenario("escaped")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--scenario":
        ProcessLifetimeTests().scenario(sys.argv[2])
        print("PASS real Linux " + sys.argv[2])
    else:
        unittest.main()

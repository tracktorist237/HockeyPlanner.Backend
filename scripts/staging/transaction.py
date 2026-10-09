"""POSIX command supervision plus durable admission barrier for uncertain outcomes.

flock alone cannot cover an asynchronous Docker daemon after CLI/owner death.
Every cooperating writer must check the barrier AFTER acquiring the host lock.
Only a completely verified transaction removes it; recovery is operator-only.
"""
from contextlib import contextmanager
from contextvars import ContextVar
import ctypes
import json
import os
from pathlib import Path
import shutil
import signal
import select
import subprocess
import tempfile
import sys
import time

CURRENT = ContextVar("staging_transaction", default=None)
BARRIER = "recovery-required"


def admission(lock_path):
    if os.path.lexists(lock_path.parent / BARRIER):
        raise ValueError("Operator recovery required")


def sync_directory(path):
    if os.name == "posix":
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


class Cancelled(BaseException):
    pass


class Transaction:
    def __init__(self, fd, lock_path, expected):
        admission(lock_path)
        self.fd, self.uncertain = fd, False
        self.subreaper = None
        self.private = Path(tempfile.mkdtemp(prefix="transaction-", dir=lock_path.parent))
        os.chmod(self.private, 0o700)
        self.barrier = lock_path.parent / BARRIER
        # Durable BEFORE any command capable of mutation. O_EXCL also rejects aliases.
        marker = os.open(self.barrier, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        try:
            with os.fdopen(marker, "w") as output:
                json.dump({"pid": os.getpid(), "expectedSha": expected, "privateDirectory": self.private.name}, output)
                output.flush()
                os.fsync(output.fileno())
            sync_directory(self.barrier.parent)
        except BaseException:
            # Incomplete marker is still a barrier, never automatic recovery.
            raise

    def children(self):
        # Linux subreaper adopts orphaned descendants, including setsid/FD-closing
        # workers that cannot be discovered through process-group membership alone.
        return [int(pid) for pid in Path(f"/proc/self/task/{os.getpid()}/children").read_text().split()]

    def enable_subreaper(self):
        if self.subreaper is None:
            if sys.platform != "linux" or self.children():
                raise ValueError("Dedicated Linux supervisor required")
            libc = ctypes.CDLL(None, use_errno=True)
            descriptor = os.pidfd_open(os.getpid())
            os.close(descriptor)
            old = ctypes.c_int()
            if libc.prctl(37, ctypes.byref(old), 0, 0, 0) or libc.prctl(36, 1, 0, 0, 0):
                raise OSError("Subreaper unavailable")
            self.subreaper = old.value

    def stop_orphans(self):
        deadline = time.monotonic() + 5
        while children := self.children():
            if time.monotonic() >= deadline or len(children) > 512:
                raise TimeoutError("Descendant completion uncertain")
            for pid in children:
                try:
                    os.kill(pid, signal.SIGKILL)
                    fd = os.pidfd_open(pid)
                except ProcessLookupError:
                    continue
                try:
                    poll = select.poll(); poll.register(fd, select.POLLIN)
                    if not poll.poll(max(0, int((deadline - time.monotonic()) * 1000))):
                        raise TimeoutError("Descendant completion uncertain")
                    os.waitpid(pid, 0)  # pidfd established exit; reap adopted child.
                finally:
                    os.close(fd)

    def stop(self, process):
        # Separate session prevents signalling the SSH shell. Descendants in this
        # group receive TERM, then KILL. Escaped/daemon work remains uncertain.
        for sig in (signal.SIGTERM, signal.SIGKILL):
            try:
                os.killpg(process.pid, sig)
            except ProcessLookupError:
                pass
            try:
                process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                continue
            # Even an exited CLI may have a descendant holding the output pipes/FD.
            if sig == signal.SIGKILL:
                break
        process.wait(timeout=5)  # Failure retains barrier; never clean an active input.
        self.stop_orphans()

    def run(self, args, cwd=None, timeout=1800):
        self.enable_subreaper()
        process = subprocess.Popen(args, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   pass_fds=(self.fd,), start_new_session=True)
        try:
            output, _ = process.communicate(timeout=timeout)
            if process.returncode:
                raise subprocess.CalledProcessError(process.returncode, args)
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                if not self.children():
                    return output
            # A successful leader with surviving descendants is not completion.
            raise ValueError("Command descendants remain")
        except BaseException:
            self.uncertain = True
            # Ignore repeat cancellation while terminating/reaping the process.
            handlers = {sig: signal.signal(sig, signal.SIG_IGN) for sig in
                        (signal.SIGTERM, signal.SIGHUP, signal.SIGINT)}
            try:
                self.stop(process)
            finally:
                for sig, handler in handlers.items():
                    signal.signal(sig, handler)
            raise

    def finish(self):
        if self.uncertain:
            return  # Snapshot/archive might still be used; retain privately for recovery.
        for path in (self.private, *self.private.rglob("*")):
            os.chmod(path, 0o700 if path.is_dir() else 0o600)
        shutil.rmtree(self.private)
        self.barrier.unlink()  # Cleanup precedes admission; any cleanup error stays blocked.
        sync_directory(self.barrier.parent)


@contextmanager
def supervised(fd, lock_path, expected):
    transaction = Transaction(fd, lock_path, expected)
    token = CURRENT.set(transaction)
    def cancel(signum, frame):
        transaction.uncertain = True
        raise Cancelled()
    handlers = {sig: signal.signal(sig, cancel) for sig in (signal.SIGTERM, signal.SIGINT)}
    if os.name == "posix":
        handlers[signal.SIGHUP] = signal.signal(signal.SIGHUP, cancel)
    try:
        yield transaction
    finally:
        # SIGKILL cannot run this block: durable barrier + inherited lock protect entry.
        try:
            transaction.finish()
        finally:
            CURRENT.reset(token)
            if transaction.subreaper is not None:
                if ctypes.CDLL(None).prctl(36, transaction.subreaper, 0, 0, 0):
                    raise OSError("Subreaper restore failed")
            for sig, handler in handlers.items():
                signal.signal(sig, handler)

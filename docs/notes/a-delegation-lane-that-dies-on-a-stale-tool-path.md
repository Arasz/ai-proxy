# A delegated lane that dies on a stale tool path

A lane in this project can die in a second with no output, and the reason is sometimes not
the reason the note about the persona's model pin describes. This is the other one.

## What it looks like

The delegation list shows the run as failed with a spawn error, and the log's only line is
the path of the runtime:

```
d-17 code-reviewer — failed (spawn /opt/homebrew/Cellar/node/26.12.0/bin/node ENOENT)
```

The lanes that ran before it in the same session worked. Nothing in the repository
changed, and the task, the brief and the working directory were the same ones the
successful lanes had.

## What it means

The harness resolved the path of the runtime once, when the session started, and kept the
answer. A package manager upgraded the runtime in between, the old directory went away, and
every later spawn asked for a file that was no longer there. The lanes that had already
started were unaffected, which is what makes it look like a brief problem rather than an
environment one.

The check is to look at the path in the error and see whether it exists:

```bash
ls -la /opt/homebrew/Cellar/node/
```

## The fix that works

Point the stale path at the new one, and the lanes run again:

```bash
ln -s 26.12.1 /opt/homebrew/Cellar/node/26.12.0
```

It is a symlink in someone else's package directory, so it is the last resort, and it goes
when the package manager prunes the old keg. The honest fix is to restart the session, so
that the harness resolves the path again.

## What else it looks like

A lane can also finish without writing the file it was told to write, and say that it did.
The write was blocked by the memory-first gate, which refuses a shell command whose text
looks like a search, and the lane's last command echoed a claim instead of checking it.
The review it had produced was in the delegation transcript, and the file was not on disk.

The rule that comes out of it: a lane that writes with a shell heredoc should read the
file back, and a lane that reports what it wrote should show the file. The `write` tool is
not blocked by the gate, and the file it writes is the evidence.

Both failures have the same shape. The lane ends, the session hears about it, and the
session believes it. The evidence is on the disk, and the session has to look.

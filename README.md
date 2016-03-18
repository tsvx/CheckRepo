# CheckRepo
Verification and update of local RPM repos.
*For those who have to update offline linux servers:)*

## Annotation

I maintain some offline linux servers and want to keep them up to date. For this reason I use rsync to get fresh repositories.
Sometimes rsync gives bad results. Even if it seems that everithing is OK. So I need a tool to check an offline mirror of a repo.

Besides, some small repos do not support rsync (mono-opt repos, for instance), only http.
So I need a tool to sync them over http(s).

Here is such a tool.

## Usage:

`CheckRepo [dir] [-c] [-r[r]] [-u [url]]`

1. Checks a repo in the given or in the current directory;
2. Checksum verification is performed only if the flag `-c` is specified.
3. Update files, if the flag `-u` is given. Update url, if given, is written to the file `.url`
in the dir to be checked. Or is read from this file if it is not given.
4. Show redundant files, if the flag `-r` is given and the repo is OK.
5. Remove redundant files, if the flag `-rr` is given and the repo is OK.

The update process tries to download `repomd.xml` (always) and each file referenced
that is absent or having a wrong size or checksum (if the latter is supposed to verify). The downloading supports resume.

## Workflow

Given a directory with an rpm repo, it assumes that there is a `repodata/repomd.xml` in that directory.
Then, it parses the `repomd.xml` for linked files (`<data>` elements) and checks for those files
existence, their size and checksum.

If it is all succeeded, it takes the `data`-file whose `type` attribute is `primary`,
unpacks this file if it is gzipped, extracts all the linked `package` elements and does almost the same
checks (existence, size, checksum + `type == rpm`).

After that, it outputs whenever the [updated] repository is consistent.
If something goes wrong, the app outputs it.

### To do
* Remove empty dirs in remove mode (`-r`).
* Show progress (files left to check; file check/download speed, estimate time).

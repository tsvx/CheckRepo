# CheckRepo
Verification and update of local RPM repos.
*For those who have to update offline linux servers:)*

## Usage:

`CheckRepo [dir] [-u [url]]`

1. Checks a repo in the given or in the current directory;
2. Update files, if flag `-u` is given. Update url, if given, is written to the file `.url`
in the dir to be checked. Or is read from this file if it is not given.

The update process tries to download `repomd.xml` (always) and each file referenced
that is absent or having a wrong size or checksum.

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
Flag `-r` to remove unwanted files which are no longer referred to this repo.

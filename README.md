# CheckRepo
Verification of local RPM repos.

## Usage:

* `CheckRepo` &ndash; checks a repo in the current directory;
* `CheckRepo dir` &ndash; checks a repo in the given directory;

## Workflow

Given a directory with an rpm repo, it assumes that there is a `repodata/repomd.xml` in that directory.
Then, it parses the `repomd.xml` for linked files (`<data>` elements) and checks for those files existence,
their size and checksum.

If it is all succeeded, it takes the `data`-file whose `type` attribute is `primary`,
unpacks this file if it is gzipped, extracts all the linked `package` elements and does almost the same
checks (existence, size and checksum).

After that, it outputs "All is OK!" :)
If something goes wrong, the app throws an exception or Trace a error.

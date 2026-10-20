//Backstop for regressions that deadlock, for example a Dispose that blocks on an async hook again (S7): the hung test fails
//after a minute instead of hanging the whole run until the CI job times out. Passing tests finish within milliseconds, and
//a failing test waits at most a few Eventually.DefaultTimeout periods (15 s) before it reports its own assertion.
[assembly: Timeout(60_000)]

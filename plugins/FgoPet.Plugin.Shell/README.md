# Shell

Contributes `shell.exec` through the native catalog. Command tools require host policy and exact bound approval; production policy currently denies them pending the frontend approval bridge. Arguments cannot create workspace authority.

The shared workspace guard verifies the working directory and authorization again before launch. `IBoundedProcessRunner` starts noninteractive PowerShell with bounded capture and a Windows Job that owns child process cleanup. Timeout, cancellation or unconfirmed completion produces an unknown execution outcome; it is never silently replayed. Working-directory restriction does not sandbox host-user permissions, filesystem access or networking.

Run Shell capability tests in `tests/FgoPet.Capability.Tests` and bounded process lifecycle tests in `tests/FgoPet.Windows.Tests`. Diagnostics contain only safe stage, correlation, exit and error metadata.

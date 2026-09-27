# Desktop composition

`DesktopKernelFactory` implements the Foundation lifecycle factory with PuddingHost DLL. The adapter is the only bridge to Host; no process launch, PID supervision or shutdown HTTP call. It injects `IDesktopServices`, leases the data directory, initializes and starts the host on loopback, then stops/disposes resources. Browser automation stays unavailable until its WinUI adapter is ported.

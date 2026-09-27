# Desktop composition

`DesktopKernelFactory` implements the Foundation lifecycle factory with PuddingHost DLL. The adapter is the only bridge to Host; no process launch, PID supervision or shutdown HTTP call. It injects `IDesktopServices`, leases the data directory, initializes and starts the host on loopback, then stops/disposes resources. Browser automation stays unavailable until its WinUI adapter is ported.

`InProcessChatClient` implements the BCL `IChatClient` port by invoking Core application services directly. Every operation runs on the thread pool with its own async DI scope and linked cancellation. Login validates the existing account/password hash; no JWT, cookie, HTTP request or controller invocation. Agent/main-session/Turn/projection services stay in Core. Core stop cancels and drains clients before disposing the host; restarted clients require login again. Portrait URLs map only to packaged wwwroot assets, with path containment checked.

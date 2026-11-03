using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Amberline.Tests")]
// DIAGNOSTICS: the headless match drives the production seat attach, detach and reconnect burst without ENet (Amberline.Tools match --reconnect-seat).
[assembly: InternalsVisibleTo("Amberline.Tools")]

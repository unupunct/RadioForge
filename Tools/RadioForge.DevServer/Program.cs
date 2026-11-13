// Local Icecast-compatible test server (used for screenshots/manual tests). Accepts any mount, source password "secret".
using RadioForge.Tests;
var srv = new FakeServer(FakeServer.Kind.Icecast) { AnyMount = true, Listeners = 0 };
File.WriteAllText(args.Length > 0 ? args[0] : "devserver.port", srv.Port.ToString());
Console.WriteLine($"Test Icecast on 127.0.0.1:{srv.Port} (password: secret). Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);

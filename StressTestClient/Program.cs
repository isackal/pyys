using Pyys;
using StressTestClient;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

X509Certificate2 cert = Certificates.GetCertificate(StoreName.My, StoreLocation.CurrentUser,
    x => x.Issuer == "CN=pyyserv"
    );
var host = new Host(cert);
host.Start();

int N = 1;
int fps = 24;  // How many messages to write per second
int seconds = 10;
int refreshRate = 24;  // How many times per second the buffered data is flushed.

var tasks = new Task[N];
var clients = new PyysClient[N];
var watch = new Stopwatch();
Console.WriteLine("start");
for (int i = 0; i < N; i++)
{
    Console.WriteLine($"{i+1} / {N}");
    clients[i] = await StressTest.CreateClient(refreshRate);
}
Console.WriteLine("Ready for testing");
await Task.Delay(2000);
Console.WriteLine("3");
await Task.Delay(1000);
Console.WriteLine("2");
await Task.Delay(1000);
Console.WriteLine("1");
await Task.Delay(1000);
Console.WriteLine("GO!");
watch.Start();
for (int i = 0; i<N; i++)
{
    tasks[i] = StressTest.RunTest(clients[i], fps, seconds);
}

await Task.WhenAll(tasks);
watch.Stop();
Console.WriteLine(watch.Elapsed.ToString());
Console.ReadKey();
Console.WriteLine($"{host.bytesReceived} | {48 * fps*seconds* N}");
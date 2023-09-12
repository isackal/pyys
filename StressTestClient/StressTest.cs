using Pyys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace StressTestClient;

public static class StressTest
{
    public static async Task Test()
    {
        var testClient = new PyysClient();
        var dataToSend = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Concat(Guid.NewGuid().ToByteArray())
            .ToArray();
        ulong x = 0;
        int FPS = 24;
        var dt = TimeSpan.FromMilliseconds(1000.0 / FPS);
        int seconds = 10;
        var iterations = FPS * seconds;
        testClient.ProcessMessage += async (buffer, size, cancellationToken) =>
        {
            x += ((ulong)size);
        };
        await testClient.ConnectAsync("127.0.0.1", "pyyserv", handShake: Guid.NewGuid().ToByteArray());
        for (int i=0; i<iterations; i++)
        {
            await testClient.WriteAsync(dataToSend, 0, dataToSend.Length, CancellationToken.None);
            await Task.Delay(dt);
        }
    }

    public static async Task<PyysClient> CreateClient(int rr=32)
    {
        var testClient = new PyysClient()
            .WithPinging(TimeSpan.FromSeconds(1))
            .WithRefreshRate(rr);
        ulong x = 0;
        testClient.ProcessMessage += async (buffer, size, cancellationToken) =>
        {
            x += ((ulong)size);
        };
        await testClient.ConnectAsync("127.0.0.1", "pyyserv", handShake: Guid.NewGuid().ToByteArray());
        return testClient;
    }

    public static async Task RunTest(PyysClient testClient, int FPS, int seconds)
    {
        var dataToSend = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Concat(Guid.NewGuid().ToByteArray())
            .ToArray();
        var dt = TimeSpan.FromMilliseconds(1000.0 / FPS);
        var iterations = FPS * seconds;
        for (int i = 0; i < iterations; i++)
        {
            await testClient.WriteAsync(dataToSend, 0, dataToSend.Length, CancellationToken.None);
            await Task.Delay(dt);
        }
    }
}

public class Host : PyysServer<Dictionary<string, object>>
{
    public int messageCount = 0;
    public ulong bytesReceived = 0;
    public Host(X509Certificate certificate) : base(certificate)
    {
    }

    public override void Log(object item)
    {
    }

    public override Dictionary<string, object> Accept(Guid connectionId)
        => new Dictionary<string, object>();

    public override async Task OnMessage(byte[] messageBuffer, int messageSize, Guid connectionId, Dictionary<string, object> context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref messageCount);
        Interlocked.Add(ref bytesReceived, ((ulong)messageSize));
        await WriteAsync(messageBuffer, 0, messageSize, cancellationToken);
    }
}

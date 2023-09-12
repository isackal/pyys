using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Threading;

namespace Pyys
{
    public delegate Task PyysMessageDelegate(byte[] message, int size, CancellationToken cancellationToken);
    public sealed class PyysClient
    {
        private Guid id = Guid.Empty;
        public Guid Id { get => id; }
        private bool isConnected = false;
        private SslStream sslStream = null;
        private TcpClient client = null;
        CancellationTokenSource cts = new CancellationTokenSource();
        private Connection connection = null;
        Task backgroundTask;
        int RefreshRate = 32;
        TimeSpan pingSchedule = TimeSpan.Zero;

        private void Log(object item)
        {
            Console.WriteLine(item);
        }

        public event PyysMessageDelegate ProcessMessage;

        // TODO
        public bool ValidateServerCertificate(
              object sender,
              X509Certificate certificate,
              X509Chain chain,
              SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            Log($"Certificate error: {sslPolicyErrors}");

            return false;
        }

        public PyysClient()
        {

        }

        public PyysClient WithRefreshRate(int refreshRate)
        {
            RefreshRate = refreshRate;
            return this;
        }

        public PyysClient WithPinging(TimeSpan pingInterval)
        {
            pingSchedule = pingInterval;
            return this;
        }

        public async Task WriteAsync(byte[] buffer, int position, int size, CancellationToken cancellationToken)
        {
            if (connection != null)
            {
                await connection.WriteAsync(buffer, position, size, cancellationToken);
            }
        }

        public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
        {
            return await connection.PingAsync(cancellationToken);
        }

        public async Task ConnectAsync(string hostName, string serverName, int port = 10001, byte[] handShake = null, CancellationToken cancellationToken = default)
        {
            // Create a TCP/IP client socket.
            // machineName is the host running the server application.
            client = new TcpClient();
            bool hasConnection = false;
            while (!hasConnection)
            {
                try
                {
                    await client.ConnectAsync(hostName, port);
                    hasConnection = true;
                }
                catch (Exception)
                {
                    Log("Retrying to connect");
                    await Task.Delay(2000);
                }
            }
            //client = new TcpClient(hostName, port);
            // Create an SSL stream that will close the client's stream.
            // The server name must match the name on the server certificate.
            try
            {
                sslStream = new SslStream(
                        client.GetStream(),
                        false,
                        new RemoteCertificateValidationCallback(ValidateServerCertificate),
                        null
                        );
                await sslStream.AuthenticateAsClientAsync(serverName);
                byte[] buffer = new byte[1 << 16];
                await Connection.ReadFixedAsync(sslStream, buffer, 0, 16, cts.Token);
                {
                    var idBuffer = new byte[16];
                    Buffer.BlockCopy(buffer, 0, idBuffer, 0, 16);
                    id = new Guid(idBuffer);
                }
                isConnected = true;
                connection = new Connection(Id, sslStream, null, cts.Token);


                byte[] handShakePayload = handShake ?? Array.Empty<byte>();
                await connection.WriteAsync(handShakePayload, 0, handShakePayload.Length, connection.StoppingToken);
                await connection.FlushAsync(connection.StoppingToken);

                connection.IsConnected = true;
                var flushTask = Task.Factory.StartNew(async () =>
                {
                    var dt = TimeSpan.FromMilliseconds(1000.0 / RefreshRate);
                    while (!connection.StoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(dt);
                        await connection.FlushAsync(connection.StoppingToken);
                    }
                }, TaskCreationOptions.LongRunning);
                var pingTask = pingSchedule != TimeSpan.Zero ? Task.Run(async () =>
                {
                    while (!connection.StoppingToken.IsCancellationRequested)
                    {
                        var ping = await connection.PingAsync(connection.StoppingToken);
                        Log($"Ping: {ping.TotalMilliseconds}");
                        await Task.Delay(pingSchedule);
                    }
                }, connection.StoppingToken) : Task.CompletedTask;
                backgroundTask = Task.Factory.StartNew(async () =>
                {
                    int numBytes = 0;
                    int bufferSize = buffer.Length;

                    while (!connection.StoppingToken.IsCancellationRequested && isConnected)
                    {
                        numBytes = await Connection.ReadMessageAsync(sslStream, buffer, 0, connection.StoppingToken);
                        if (numBytes > Connection.MaxSize)
                        {
                            switch (((ushort)numBytes))
                            {
                                case Connection.PingRequest:
                                    await connection.SendPingResponseAsync(cancellationToken);
                                    break;
                                case Connection.PingResponse:
                                    connection.ReceivePingResponse();
                                    break;
                                default:
                                    break;
                            }
                        }
                        else
                        {
                            await (ProcessMessage?.Invoke(buffer, numBytes, connection.StoppingToken) ?? Task.CompletedTask);
                        }
                    }
                    await flushTask;
                    await pingTask;
                }
                , TaskCreationOptions.LongRunning);
            }
            catch (Exception e)
            {
                Log($"Exception: {e.Message}");
                if (e.InnerException != null)
                {
                    Log($"Inner exception: {e.InnerException.Message}");
                }
                Log("Authentication failed - closing the connection.");
                client.Close();
                return;
            }
        }

        public void Dispose()
        {
            isConnected = false;
            client.Close();
            cts.Dispose();
            Log("Client disconnected");
        }
        public async Task StopAsync()
        {
            cts.Cancel();
            await backgroundTask;
            cts.Dispose();
            cts = new CancellationTokenSource();
        }
    }
}
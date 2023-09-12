using System;
using System.Collections;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using SslTcpServer;
using SslTcpServer.BufferExtensions;

namespace SslTcpServer
{
    public class SslTcpClient
    {
        private Guid id = Guid.Empty;
        private Thread thread = null!;
        public Guid Id { get => id; }
        private bool isConnected = false;
        private SslStream sslStream = null!;
        private TcpClient client = null!;
        public event Action<byte[]>? OnMessage;

        public virtual void Log(object item)
        {
            Console.WriteLine(item);
        }

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

        public SslTcpClient(string hostName, string serverName, int port=10001)
        {
            Connect(hostName, serverName, port);
        }

        public void Connect(string hostName, string serverName, int port = 10001)
        {
            // Create a TCP/IP client socket.
            // machineName is the host running the server application.
            client = new TcpClient(hostName, port);
            Log("Client connected.");
            // Create an SSL stream that will close the client's stream.
            // The server name must match the name on the server certificate.
            try
            {
                thread = new Thread(async () =>
                {
                    sslStream = new SslStream(
                        client.GetStream(),
                        false,
                        new RemoteCertificateValidationCallback(ValidateServerCertificate),
                        null
                        );
                    sslStream.AuthenticateAsClient(serverName);
                    Queue<byte[]> messageQueue = new Queue<byte[]>();
                    byte[] buffer = new byte[(1 << 16) -1];
                    messageQueue.EnqueueMessages(sslStream, buffer);
                    id = new Guid(messageQueue.Dequeue());
                    isConnected = true;
                    while (isConnected)
                    {
                        while (messageQueue.Count > 0)
                        {
                            OnMessage?.Invoke(messageQueue.Dequeue());
                        }
                        try
                        {
                            await messageQueue.EnqueueMessagesAsync(sslStream, buffer, default);
                        }
                        catch (Exception)
                        {
                            Log("Disconnecting...");
                            isConnected = false;
                        }
                    }

                });
                thread.Start();
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

        public void Send(params IEnumerable<byte>[] messages)
        {
            sslStream.WriteMessagesAsync(messages, default).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            isConnected = false;
            client.Close();
            thread.Join();
            Log("Client disconnected");
        }
    }
}
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using SslTcpServer.BufferExtensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace SslTcpServer
{
    public interface IUser
    {
        Guid Id { get; }
        Task SendAsync(IEnumerable<byte> message, CancellationToken cancellationToken = default);
        ICollection<object> Context { get; set; }
    }

    public static class UserExtensions
    {
        public static async Task SendAsync(this IUser user, IMessage message, CancellationToken cancellationToken = default)
        {
            var cmd = new Command
            {
                MessageId = BitConverter.ToUInt64(Encoding.UTF8.GetBytes(message.GetType().GetName()).Hash(), 0),
                Body = message.ToByteString()
            };
            await user.SendAsync(cmd.ToByteArray(), cancellationToken);
        }

        public static async Task SendAsync(this IEnumerable<IUser> users, IEnumerable<byte> message, CancellationToken cancellation = default)
        {
            await Task.WhenAll(users.Select(async u =>
            {
                try
                {
                    await u.SendAsync(message, cancellation);
                }
                catch (TaskCanceledException)
                {

                }
            }));
        }

        public static async Task SendAsync(this IEnumerable<IUser> users, IMessage message, CancellationToken cancellation = default)
        {
            await Task.WhenAll(users.Select(async u =>
            {
                try
                {
                    await u.SendAsync(message, cancellation);
                }
                catch (TaskCanceledException)
                {

                }
            }));
        }
    }

    public class CertificateProfiver
    {
        public X509Certificate GetCertificate(StoreName storeName, StoreLocation storeLocation, Func<IEnumerable<X509Certificate2>, X509Certificate2> certificateSelector)
        {
            using (var store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate cert = certificateSelector(store.Certificates.Cast<X509Certificate2>());
                if (cert == null)
                {
                    throw new DirectoryNotFoundException("The certificate was not found");
                }
                return cert;
            }
        }
    }

    public interface ISslTcpServer : IDisposable
    {
        string ServerName { get; }
        IUser[] Users { get; }
        event Func<IReadOnlyList<byte>, IUser, ISslTcpServer, CancellationToken, Task> OnMessage;
        event Func<IUser, ISslTcpServer, CancellationToken, Task> OnConnect;
        event Func<IUser, ISslTcpServer, CancellationToken, Task> OnDisconnect;
    }

    public interface IStreamProtocol
    {
        Task ReadAndExecuteAsync(Stream stream, IUser user, IDictionary<Guid, IUser> users, CancellationToken cancellationToken);
    }

    public sealed class SslServer
    {
        readonly X509Certificate certificate;
        readonly ConcurrentDictionary<Guid, IUser> connections = new Dictionary<Guid, IUser>();
        private CancellationTokenSource cts = new CancellationTokenSource();
        TcpListener listener = null!;
        Task serverTask = Task.CompletedTask;
        List<Task> clientTasks = new List<Task>();

        public event Func<IReadOnlyList<byte>, IUser, ISslTcpServer, CancellationToken, Task>? OnMessage;
        public event Func<IUser, ISslTcpServer, CancellationToken, Task>? OnConnect;
        public event Func<IUser, ISslTcpServer, CancellationToken, Task>? OnDisconnect;

        public int Port { get; set; } = 10001;

        public ISslTcpServer Interface { get => this; }

        public void Log(object item)
        {
            Console.WriteLine(item);
        }

        private class Connection : IUser
        {
            public Guid Id { get; } = Guid.NewGuid();
            public SslStream SslStream { get; set; } = null!;
            public bool IsConnected { get; set; }
            public string Name { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
            public ICollection<object> Context { get; set; } = new List<object>();

            public async Task SendAsync(IEnumerable<byte> message, CancellationToken cancellationToken = default)
            {
                if (SslStream == null)
                {
                    return;
                }
                await SslStream.WriteMessagesAsync(new[] { message }, cancellationToken);
            }
        }

        public SslServer UseCertificate(Func<CertificateProfiver, X509Certificate> certificateProvider)
        {
            var provider = new CertificateProfiver();
            serverCertificate = certificateProvider(provider);
            return this;
        }

        Func<IStreamProtocol> protocolFactory = () => throw new NotImplementedException("No protocol added");  // TODO: add default protocol
        public SslServer UseProtocol(Func<IStreamProtocol> protocol)
        {
            protocolFactory = protocol;
            return this;
        }

        public string ServerName { get => certificate.Issuer; }

        public IUser[] Users => connections.Values.ToArray();

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            listener = new TcpListener(IPAddress.Any, Port);
            try
            {
                listener.Start();
                while (!cts.IsCancellationRequested)
                {
                    Log("Waiting for a client to connect...");
                    TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                    var task = RunClientAsync(client, stoppingToken);
                    clientTasks.Add(task);
                    clientTasks.RemoveAll(x => x.IsCompleted);  // Clean up completed tasks
                }
                await Task.WhenAll(clientTasks);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task RunClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
                var connection = new Connection();
            try
            {
                using SslStream stream = new SslStream(
                    client.GetStream(), false);
                connections.Add(connection.Id, connection);
                await stream.AuthenticateAsServerAsync(certificate, false, true);
                await stream.WriteMessagesAsync(new[] { connection.Id.ToByteArray() }, cancellationToken);
                connection.IsConnected = true;
                connection.SslStream = stream;
                await OnClientConnectAsync(connection, cancellationToken);
                await (OnConnect?.Invoke(connection, this, cancellationToken) ?? Task.CompletedTask);
                byte[] buffer = new byte[1 << 16];
                var protocol = protocolFactory();
                while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
                {
                    try
                    {
                        await protocol.ReadAndExecuteAsync(stream, connection, connections, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR! :(");
                    }
                }
            }
            catch (Exception e)
            {
                Log($"Exception: {e.Message}");
                if (e.InnerException != null)
                {
                    Log($"Inner exception: {e.InnerException.Message}");
                }
            }
            finally
            {
                client.Close();
                client.Dispose();
                connections.Remove(connection.Id);
            }
            Log($"Terminated {connection.Id}");
        }

        protected virtual async Task OnClientConnectAsync(IUser user, CancellationToken cancellationToken)
        {
            Log($"Client {user.Id} connected.");
            await Task.CompletedTask;
        }

        protected virtual async Task OnClientDisconnectAsync(IUser user, CancellationToken cancellationToken)
        {
            Log($"Client {user.Id} disconnected.");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var c in connections.Values)
            {
                c.IsConnected = false;
            }
            cts.Dispose();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cts.Dispose();
            cts = new CancellationTokenSource();
            serverTask = ExecuteAsync(cts.Token);
            await Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            cts.Cancel();
            await serverTask;
        }
    }
}
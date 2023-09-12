using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Pyys
{
    [StructLayout(LayoutKind.Explicit)]
    internal struct ByteConverter
    {
        [FieldOffset(0)] public ushort Short;
        [FieldOffset(0)] public byte Byte1;
        [FieldOffset(1)] public byte Byte2;
    }

    public sealed class Connection : IDisposable
    {
        public Guid Id { get; }
        private SslStream sslStream;
        public object Context { get; }
        SemaphoreSlim sem = new SemaphoreSlim(1, 1);
        public bool IsConnected { get; set; } = false;
        byte[] writer = ArrayPool<byte>.Shared.Rent(1 << 16);
        int writeAt = 0;
        Channel<SemaphoreSlim> sems = Channel.CreateUnbounded<SemaphoreSlim>();
        internal const ushort MaxSize = 1 << 15;
        internal const ushort PingRequest = MaxSize + 1;
        internal const ushort PingResponse = MaxSize + 2;
        SemaphoreSlim pingRequestSem = new SemaphoreSlim(1, 1);
        SemaphoreSlim pingResponseSem = new SemaphoreSlim(0, 1);
        public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
        {
            await pingRequestSem.WaitAsync(cancellationToken);
            try
            {
                var stopWatch = new Stopwatch();
                stopWatch.Start();
                await sem.WaitAsync(cancellationToken);
                try
                {
                    var conv = new ByteConverter { Short = PingRequest };
                    writer[writeAt++] = conv.Byte1;
                    writer[writeAt++] = conv.Byte2;
                    await sslStream.WriteAsync(writer, 0, writeAt, cancellationToken);
                    await sslStream.FlushAsync(cancellationToken);
                    writeAt = 0;
                }
                finally
                {
                    sem.Release();
                }
                await pingResponseSem.WaitAsync(cancellationToken);
                stopWatch.Stop();
                return stopWatch.Elapsed;
            }
            finally
            {
                pingRequestSem.Release();
            }
        }

        internal void ReceivePingResponse()
        {
            try
            {
                pingResponseSem.Release();
            }
            catch (Exception)
            {

            }
        }

        public async Task WriteAsync(byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                var conv = new ByteConverter { Short = ((ushort)size) };
                writer[writeAt++] = conv.Byte1;
                writer[writeAt++] = conv.Byte2;
                Buffer.BlockCopy(buffer, offset, writer, writeAt, size);
                writeAt += size;
                if (writeAt > 1 << 15)
                {
                    await sslStream.WriteAsync(writer, 0, writeAt, cancellationToken);
                    await sslStream.FlushAsync(cancellationToken);
                    writeAt = 0;
                }
            }
            finally
            {
                sem.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                if (writeAt == 0)
                {
                    return;
                }
                await sslStream.WriteAsync(writer, 0, writeAt, cancellationToken);
                await sslStream.FlushAsync(cancellationToken);
                writeAt = 0;
            }
            finally
            {
                sem.Release();
            }
        }

        public Connection(Guid id, SslStream stream, object context, CancellationToken cancellationToken)
        {
            Id = id;
            sslStream = stream;
            Context = context;
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StoppingToken = cts.Token;
        }
        public CancellationTokenSource cts;

        public CancellationToken StoppingToken { get; }
        public void Kick() => cts.Cancel();

        public static async Task<int> ReadMessageAsync(SslStream sslStream, byte[] buffer, int pos, CancellationToken cancellationToken)
        {
            int i = pos;
            if ((await sslStream.ReadAsync(buffer, i, 2)) == 1)
            {
                await sslStream.ReadAsync(buffer, i + 1, 1);
            }
            int size = BitConverter.ToUInt16(buffer, i);
            if (size > MaxSize)
            {
                return size;
            }
            int j;
            while (size > 0)
            {
                j = await sslStream.ReadAsync(buffer, i, size, cancellationToken);
                i += j;
                size -= j;
            }
            return i - pos;
        }

        public static async Task<int> ReadFixedAsync(SslStream sslStream, byte[] buffer, int pos, int size, CancellationToken cancellationToken)
        {
            int i = pos;
            int s = size;
            int j;
            while (s > 0)
            {
                j = await sslStream.ReadAsync(buffer, i, s, cancellationToken);
                i += j;
                s -= j;
            }
            return size;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(writer);
            sslStream.Close();
            sslStream.Dispose();
            sem.Dispose();
            pingRequestSem.Dispose();
            pingResponseSem.Dispose();
            cts.Dispose();
        }

        internal async Task SendPingResponseAsync(CancellationToken cancellationToken)
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                var conv = new ByteConverter { Short = PingResponse };
                writer[writeAt++] = conv.Byte1;
                writer[writeAt++] = conv.Byte2;
                await sslStream.WriteAsync(writer, 0, writeAt, cancellationToken);
                await sslStream.FlushAsync(cancellationToken);
                writeAt = 0;
            }
            finally
            {
                sem.Release();
            }
        }
    }

    public abstract class PyysServer : PyysServer<object>
    {
        protected PyysServer(X509Certificate certificate) : base(certificate)
        {
        }
    }

    public abstract class PyysServer<TContext> : IDisposable
    {
        readonly X509Certificate certificate;

        protected PyysServer(X509Certificate certificate)
        {
            this.certificate = certificate;
        }

        readonly ConcurrentDictionary<Guid, Connection> connections = new ConcurrentDictionary<Guid, Connection>();
        KeyValuePair<Guid, Connection>[] finalConnections = Array.Empty<KeyValuePair<Guid, Connection>>();
        readonly ConcurrentStack<byte[]> binaryPool = new ConcurrentStack<byte[]>();
        readonly ConcurrentStack<Task[]> taskPool = new ConcurrentStack<Task[]>();
        private CancellationTokenSource cts = new CancellationTokenSource();
        TcpListener listener;
        readonly List<Task> clientTasks = new List<Task>();
        protected int workers = Environment.ProcessorCount;
        readonly int maxConcurrentConnections = 10000;
        Task backgroundTask = Task.CompletedTask;
        Task flushLoop = Task.CompletedTask;

        public int Port { get; set; } = 10001;

        public virtual void Log(object item)
        {
            Console.WriteLine(item);
        }

        private async Task WorkerProcess(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && await queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    await TryExecute(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {

            }
        }

        private byte[] RentBuffer()
        {
            if (binaryPool.TryPop(out var x))
            {
                return x;
            }
            else
            {
                return new byte[1 << 16];
            }
        }

        private void Return(byte[] array)
        {
            binaryPool.Push(array);
        }

        private void Return(Task[] tasks)
        {
            taskPool.Push(tasks);
        }

        private Task[] RentTasks()
        {
            if (taskPool.TryPop(out var x))
            {
                return x;
            }
            else
            {
                return new Task[maxConcurrentConnections];
            }
        }

        public string ServerName { get => certificate.Issuer; }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            listener = new TcpListener(IPAddress.Any, Port);
            Task[] workerTasks = new Task[workers];
            for (int i = 0; i < workers; i++)
            {
                workerTasks[i] = Task.Factory.StartNew(() => WorkerProcess(stoppingToken), TaskCreationOptions.LongRunning);
            }

            stoppingToken.Register(() => listener.Stop());
            try
            {
                listener.Start();
                while (!stoppingToken.IsCancellationRequested)
                {
                    Log("Waiting for a client to connect...");
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    clientTasks.RemoveAll(x => x.IsCompleted);  // Clean up completed tasks
                    var task = RunClientAsync(client, stoppingToken);
                    clientTasks.Add(task);
                }
                await Task.WhenAll(clientTasks);
                await Task.WhenAll(workerTasks);
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

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            var con = finalConnections;
            var tasks = RentTasks();
            Connection c;
            for (int i = 0; i < con.Length; i++)
            {
                c = con[i].Value;
                tasks[i] = c.FlushAsync(cancellationToken);
            }
            int j = con.Length - 1;
            while (j >= 0)
            {
                await tasks[j];
                tasks[j--] = null;  // Clean up
            }
            Return(tasks);
        }

        public abstract TContext Accept(Guid connectionId);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="data"></param>
        /// <param name="size"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>false if denying the connection</returns>
        protected virtual Task<bool> HandShake(Connection connection, byte[] data, int size, CancellationToken cancellationToken) => Task.FromResult(true);
        private struct MessageContext
        {
            public byte[] Data;
            public int Size;
            public Guid Id;
            public TContext Context;
            public Connection Connection;
        }

        readonly Channel<MessageContext> queue = Channel.CreateUnbounded<MessageContext>();

        public abstract Task OnMessage(byte[] messageBuffer, int messageSize, Guid connectionId, TContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Use when you do not care about ordering of when messages are sent and received.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="size"></param>
        public void Write(byte[] buffer, int offset, int size)
        {
            var con = finalConnections;
            foreach (var c in con)
            {
                _ = c.Value.WriteAsync(buffer, offset, size, CancellationToken.None);
            }
        }

        public async Task WriteAsync(byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            var con = finalConnections;
            var tasks = RentTasks();
            for (int i = 0; i < con.Length; i++)
            {
                tasks[i] = con[i].Value.WriteAsync(buffer, offset, size, cancellationToken);
            }
            int j = con.Length - 1;
            while (j >= 0)
            {
                await tasks[j];
                tasks[j--] = null;  // Clean up
            }
            Return(tasks);
        }

        public async Task WriteAsync(byte[] buffer, int offset, int size, Guid receiver, CancellationToken cancellationToken)
        {
            if (connections.TryGetValue(receiver, out var x))
            {
                await x.WriteAsync(buffer, offset, size, cancellationToken);
            }
        }

        public void Start()
        {
            backgroundTask = Task.Factory.StartNew(() => ExecuteAsync(cts.Token), TaskCreationOptions.LongRunning);
            flushLoop = Task.Factory.StartNew(() => FlushLoop(cts.Token), TaskCreationOptions.LongRunning);
        }

        public async Task StopAsync()
        {
            cts.Cancel();
            await backgroundTask;
            await flushLoop;
            await Task.WhenAll(clientTasks);
            cts.Dispose();
            cts = new CancellationTokenSource();
        }

        public int FlushRate { get; set; } = 24;
        private async Task FlushLoop(CancellationToken cancellationToken)
        {
            var dt = TimeSpan.FromMilliseconds(1000.0 / FlushRate);
            while (!cancellationToken.IsCancellationRequested)
            {
                await FlushAsync(cancellationToken);
                await Task.Delay(dt, cancellationToken);
            }
        }

        public async Task WriteAsync(byte[] buffer, int offset, int size, Func<TContext, bool> predicate, CancellationToken cancellationToken)
        {
            var con = finalConnections;
            var tasks = RentTasks();
            int j = -1;
            Connection c;
            for (int i = 0; i < con.Length; i++)
            {
                c = con[i].Value;
                if (predicate((TContext)c.Context))
                {
                    tasks[++j] = c.WriteAsync(buffer, offset, size, cancellationToken);
                }
            }
            while (j >= 0)
            {
                await tasks[j];
                tasks[j--] = null;  // Clean up
            }
            Return(tasks);
        }

        public async Task<bool> TryExecute(CancellationToken cancellationToken)
        {
            if (queue.Reader.TryRead(out var x))
            {
                if (x.Size > Connection.MaxSize)
                {
                    switch (((ushort)x.Size))
                    {
                        case Connection.PingRequest:
                            await x.Connection.SendPingResponseAsync(cancellationToken);
                            break;
                        case Connection.PingResponse:
                            x.Connection.ReceivePingResponse();
                            break;
                        default:
                            break;
                    }
                    Return(x.Data);
                    return true;
                }
                await OnMessage(x.Data, x.Size, x.Id, x.Context, cancellationToken);
                Return(x.Data);
                return true;
            }
            return false;
        }

        protected virtual Task OnConnectionAsync(Connection connection, CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RunClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            SslStream stream = new SslStream(
                    client.GetStream(), false);
            Guid id = Guid.NewGuid();
            var connection = new Connection(id, stream, Accept(id), cancellationToken);
            byte[] buffer = RentBuffer();  // Max 64 kb
            try
            {
                await stream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, true);
                await stream.WriteAsync(id.ToByteArray(), 0, 16, connection.StoppingToken);
                await stream.FlushAsync(cancellationToken);

                int numBytes = await Connection.ReadMessageAsync(stream, buffer, 0, cancellationToken);
                var success = await HandShake(connection, buffer, numBytes, connection.StoppingToken);
                if (!success)
                {
                    // Reject connection
                    return;
                }
                connection.IsConnected = true;
                connections.TryAdd(connection.Id, connection);
                finalConnections = connections.ToArray();
                await OnConnectionAsync(connection, connection.StoppingToken);

                while (!connection.StoppingToken.IsCancellationRequested && connection.IsConnected)
                {
                    try
                    {
                        numBytes = await Connection.ReadMessageAsync(stream, buffer, 0, connection.StoppingToken);
                        var msg = new MessageContext
                        {
                            Context = (TContext)connection.Context,
                            Data = RentBuffer(),
                            Id = id,
                            Size = numBytes,
                            Connection = connection
                        };
                        Buffer.BlockCopy(buffer, 0, msg.Data, 0, numBytes);
                        queue.Writer.TryWrite(msg);
                    }
                    catch (Exception ex)
                    {
                        connection.IsConnected = false;
                        Log("ERROR! :(");
                    }
                }
                connections.TryRemove(id, out _);
                finalConnections = connections.ToArray();
                await OnDisconnectAsync(connection, cancellationToken);
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
                connection.Dispose();
                Return(buffer);
            }
            Log($"Terminated {connection.Id}");
        }

        protected virtual async Task OnDisconnectAsync(Connection connection, CancellationToken cancellationToken)
        {
            Log($"Client {connection.Id} disconnected.");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var c in connections.ToArray())
            {
                c.Value.Dispose();
            }
            cts.Dispose();
        }
    }
}
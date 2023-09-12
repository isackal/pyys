using Google.Protobuf;
using Google.Protobuf.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace SslTcpServer
{
    public interface ICommandHandler {}
    public interface IServerCommandHandler : ICommandHandler { }
    public interface IClientCommandHandler : ICommandHandler { }

    public interface IServerCommandHandler<TMessage> : IServerCommandHandler
        where TMessage : IMessage<TMessage>
    {
        Task HandleAsync(TMessage request, IUser invoker, IUser[] users, CancellationToken cancellationToken);
    }

    public interface IClientCommandHandler<TMessage> : IClientCommandHandler
        where TMessage : IMessage<TMessage>
    {
        Task HandleAsync(TMessage request, Action<IMessage> send, CancellationToken cancellationToken);
    }

    public sealed class MessageHandlerCollection<TContext> : IDisposable
    {
        private class ContainerFunction
        {
            private MessageDescriptor descriptor = null!;
            private Func<IMessage, TContext, CancellationToken, Task> method = null!;


            public ContainerFunction(
                MessageDescriptor descriptor,
                Func<IMessage, TContext, CancellationToken, Task> handler
                )
            {
                this.descriptor = descriptor;
                this.method = handler;
            }

            public async Task InvokeAsync(ByteString bytes, TContext context, CancellationToken cancellationToken = default)
            {
                IMessage message = descriptor.Parser.ParseFrom(bytes);
                await method(message, context, cancellationToken);
            }

            public static ContainerFunction Create<T>(Func<T, TContext, CancellationToken, Task> action) where T : IMessage<T>
            {
                var descriptor = (MessageDescriptor)(typeof(T).GetProperty("Descriptor")?.GetValue(null))!;
                Func<IMessage, TContext, CancellationToken, Task> method = (m, c, x) => action((T)m, c, x);
                return new ContainerFunction(descriptor, method);
            }

            public static ContainerFunction Create(Type t, Func<IMessage, TContext, CancellationToken, Task> action)
            {
                var descriptor = (MessageDescriptor)(t.GetProperty("Descriptor")?.GetValue(null))!;
                return new ContainerFunction(descriptor, action);
            }
        }

        private Dictionary<ulong, ContainerFunction> handlers =
            new Dictionary<ulong, ContainerFunction>();
        private SHA256 sha256 = SHA256.Create();

        private ulong Hash(string name) => BitConverter.ToUInt64(sha256.ComputeHash(Encoding.UTF8.GetBytes(name)), 0);

        public void Add<T>(Func<T, TContext, CancellationToken, Task> handler) where T : IMessage<T>
        {
            var name = typeof(T).GetName();
            var id = Hash(name);
            handlers.Add(id, ContainerFunction.Create(handler));
        }

        public void Add(Type t, Func<IMessage, TContext, CancellationToken, Task> handler)
        {
            var name = t.GetName();
            var id = Hash(name);
            handlers.Add(id, ContainerFunction.Create(t, handler));
        }

        public void Dispose()
        {
            sha256.Dispose();
        }

        public async Task InvokeAsync(Command command, TContext context, CancellationToken cancellationToken = default)
        {
            var func = handlers.GetValueOrDefault(command.MessageId);
            if (func != null)
            {
                await func.InvokeAsync(command.Body, context, cancellationToken);
            }
        }

        public Command Serialize(IMessage message)
        {
            var name = message.GetType().GetName();
            var id = Hash(name);
            return new Command
            {
                MessageId = id,
                Body = message.ToByteString()
            };
        }
    }

    public abstract class ServerV3 : SslServer
    {
        public class Context
        {
            public IUser User { get; private set; }
            public Context(IUser user)
            {
                User = user;
            }
        }

        private MessageHandlerCollection<Context> handlers = new MessageHandlerCollection<Context>();

        public void AddHandler<T>(Func<IServerCommandHandler<T>> factory) where T : IMessage<T>
        {
            handlers.Add<T>(async (m, x, c) =>
            {
                var handler = factory();
                await handler.HandleAsync(m, x.User, Users, c);
            });
        }

        public void AddHandler<T>(IServerCommandHandler<T> handler) where T : IMessage<T>
        {
            handlers.Add<T>(async (m, x, c) =>
            {
                await handler.HandleAsync(m, x.User, Users, c);
            });
        }

        protected override async Task OnMessageAsync(byte[] message, IUser user, CancellationToken cancellationToken)
        {
            var command = Command.Parser.ParseFrom(message);
            await handlers.InvokeAsync(command, new Context(user), cancellationToken);
        }

        protected override abstract X509Certificate UseCertificate(CertificateProfiver certificateProvider);
    }
}

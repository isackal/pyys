using Google.Protobuf;
using SslTcpServer;
using System.Security.Cryptography.X509Certificates;

namespace TcpHost
{
    public class Server : SslServer
    {
        private string thumbPrint;
        private IServiceProvider serviceProvider;
        private Dictionary<Guid, ConnectionContext> contexts = new Dictionary<Guid, ConnectionContext>();
        private Sha256
        private Dictionary<ulong, >

        public sealed class RequestHandlerNode
        {
            private static readonly IRequestChainHandler emptyHandler = new EmptyRequestHandler();
            public IRequestChainHandler? Handler { get; set; }
            public IRequestChainHandler? Next { get; set; }
            public async Task HandleAsync(TRequest request, TResponse response, CancellationToken cancellationToken)
            {
                if (Handler != null)
                {
                    await Handler.HandleAsync(request, response, Next ?? emptyHandler, cancellationToken);
                }
            }
        }

        public Server(IConfiguration config, IServiceProvider serviceProvider)
        {
            thumbPrint = config["Certificate:Thumbprint"]!;
            this.serviceProvider = serviceProvider;
        }

        protected override X509Certificate UseCertificate(CertificateProfiver certificateProvider) => certificateProvider
            .GetCertificate(StoreName.My, StoreLocation.LocalMachine,
                certs => certs.Where(x => x.Thumbprint == thumbPrint).Single()
            );

        protected override Task OnClientConnectAsync(IUser user, CancellationToken cancellationToken)
        {
            var scope = serviceProvider.CreateScope();
            var context = new ConnectionContext(user, scope);
            contexts.Add(user.Id, context);
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectAsync(IUser user, CancellationToken cancellationToken)
        {
            var context = contexts[user.Id];
            context.Scope.Dispose();
            contexts.Remove(user.Id);
            return Task.CompletedTask;
        }

        protected override async Task OnMessageAsync(byte[] message, IUser user, CancellationToken cancellationToken)
        {
            TRequest request = Activator.CreateInstance<TRequest>();
            TResponse response = Activator.CreateInstance<TResponse>();
            request.MergeFrom(message);
            var pipeline = contexts.GetValueOrDefault(user.Id)?.PipelineStart;
            if (pipeline != null)
            {
                await pipeline.HandleAsync(request, response, cancellationToken);
            }
        }

        public class ConnectionContext
        {
            public IUser User { get; private set; } = null!;
            public IServiceScope Scope { get; private set; } = null!;
            public RequestHandlerNode? PipelineStart { get; private set; }
            public ConnectionContext(IUser user, IServiceScope scope)
            {
                User = user;
                Scope = scope;
                var pipeline = scope.ServiceProvider.GetServices<IRequestChainHandler>().Select(x => new RequestHandlerNode
                {
                    Handler = x
                }).ToArray();
                foreach (var x in pipeline.SkipLast(1).Zip(pipeline.Skip(1)))
                {
                    x.First.Next = x.Second.Handler;
                }
                PipelineStart = pipeline.FirstOrDefault();
            }
        }
    }
}

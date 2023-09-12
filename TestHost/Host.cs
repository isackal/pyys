using Pyys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace TestHost
{
    public class Host : PyysServer<Dictionary<string, object>>
    {
        public Host(X509Certificate certificate) : base(certificate)
        {
        }

        public override Dictionary<string, object> Accept(Guid connectionId)
            => new Dictionary<string, object>();

        public override async Task OnMessage(byte[] messageBuffer, int messageSize, Guid connectionId, Dictionary<string, object> context, CancellationToken cancellationToken)
        {
            string msg = Encoding.UTF8.GetString(messageBuffer, 0, messageSize);
            Console.WriteLine(msg);
            await WriteAsync(messageBuffer, 0, messageSize, cancellationToken);
        }
    }
}

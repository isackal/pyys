using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SslTcpServer
{
    public static class HashExtensions
    {
        private const int poolMax = 16;
        private static int at = 0;
        private static SHA256[] algs = Enumerable.Range(0, poolMax).Select(x => SHA256.Create())
            .ToArray();

        public static byte[] Hash(this IEnumerable<byte> bytes)
        {
            SHA256 alg;
            lock (algs)
            {
                alg = algs[at++];
                at = at % poolMax;
            }
            lock (alg)
            {
                return alg.ComputeHash(bytes.ToArray());
            }
        }
    }
}

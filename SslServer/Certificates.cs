using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Pyys
{
    public static class Certificates
    {
        public static X509Certificate2 GetCertificate(StoreName storeName, StoreLocation storeLocation, Func<X509Certificate2, bool> predicate)
        {
            using (var store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                foreach (var certificate in store.Certificates)
                {
                    if (predicate(certificate))
                    {
                        return certificate;
                    }
                }
                throw new KeyNotFoundException("Certificate not found");
            }
        }
    }
}

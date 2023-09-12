// See https://aka.ms/new-console-template for more information

using Pyys;
using System.Security.Cryptography.X509Certificates;
using TestHost;

X509Certificate2 cert = Certificates.GetCertificate(StoreName.My, StoreLocation.CurrentUser,
    x => x.Issuer == "CN=pyyserv"
    );
var host = new Host(cert);
host.Start();

Console.WriteLine("Hello, World!");
Console.WriteLine(host.ServerName);
Console.ReadKey();

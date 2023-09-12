namespace SslTcpServer
{
    public static class TypeExtensions
    {
        public static string GetName(this Type t) => t.Name.ToLower();
    }
}

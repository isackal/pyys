using Pyys;
using System.Text;

var testClient = new PyysClient()
    .WithPinging(TimeSpan.FromSeconds(5));
testClient.ProcessMessage += async (buffer,size,cancellationToken) =>
{
    var msg = Encoding.UTF8.GetString(buffer, 0, size);
    Console.WriteLine(msg);
};
await testClient.ConnectAsync("127.0.0.1", "pyyserv", handShake: Guid.NewGuid().ToByteArray());
while (true)
{
    Console.WriteLine("Msg: ");
    var x = Console.ReadLine();
    var data = Encoding.UTF8.GetBytes(x!);
    await testClient.WriteAsync(data, 0, data.Length, CancellationToken.None);
}
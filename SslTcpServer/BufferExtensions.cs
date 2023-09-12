using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SslTcpServer.BufferExtensions
{
    public static class BufferExtensions
    {
        public static int GetBigInt(this byte[] buffer, int pos, out int newPos)
        {
            int result = 0;
            int next = 1 << 7;
            int mask = ~next;
            int i = pos;
            do
            {
                result = (result << 7) + (mask & buffer[i]);
            } while ((next & buffer[i++]) != 0);
            newPos = i;
            return result;
        }

        public static IEnumerable<byte[]> Split(this byte[] buffer, int pos, int size)
        {
            int at = pos;
            int to = pos + size;
            List<byte[]> byteStrings = new List<byte[]>();
            while (at < to)
            {
                int messageSize = buffer.GetBigInt(at, out var newPos);
                at = newPos;
                byteStrings.Add(buffer.Take(new Range(at, at + messageSize)).ToArray());
                at += messageSize;
            }

            return byteStrings.ToArray();
        }

        public static byte[] ToDynamicIntegerBytes(this int number)
        {
            int remains = number;
            int next = 1 << 7;
            List<int> results = new List<int>();
            do
            {
                int mod = remains % next;
                remains = remains >> 7;
                results.Add(mod);

            } while (remains > 0);
            for (int i = 1; i < results.Count; i++)
            {
                results[i] |= next;
            }
            return results.Select(x => (byte)x).Reverse().ToArray();
        }

        public static byte[] FormatMessage(this IEnumerable<byte> message)
        {
            return message.Count().ToDynamicIntegerBytes().Concat(message).ToArray();
        }

        public static IEnumerable<byte[]> ReadMessages(this SslStream sslStream, byte[] buffer)
        {
            int bytes = sslStream.Read(buffer, 0, buffer.Length);
            var messages = buffer.Split(0, bytes);
            return messages;
        }

        public static async Task<IEnumerable<byte[]>> ReadMessagesAsync(this SslStream sslStream, byte[] buffer, CancellationToken cancellationToken)
        {
            int bytes = await sslStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            var messages = buffer.Split(0, bytes);
            return messages;
        }

        public static void WriteMessages(this SslStream sslStream, params IEnumerable<byte>[] messages)
        {
            try
            {
                foreach (var msg in messages)
                {
                    sslStream.Write(msg.FormatMessage());
                }
                sslStream.Flush();
            }
            catch (Exception)
            {
                sslStream.Close();
            }
        }

        public static async Task WriteMessagesAsync(this SslStream sslStream, IEnumerable<byte>[] messages, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var msg in messages)
                {
                    await sslStream.WriteAsync(msg.FormatMessage(), cancellationToken);
                }
                await sslStream.FlushAsync(cancellationToken);
            }
            catch (Exception)
            {
                sslStream.Close();
            }
        }

        public static void EnqueueMessages(this Queue<byte[]> messageQueue, SslStream sslStream, byte[] buffer)
        {
            var messages = sslStream.ReadMessages(buffer);
            foreach (var message in messages)
            {
                messageQueue.Enqueue(message);
            }
        }

        public static async Task EnqueueMessagesAsync(this Queue<byte[]> messageQueue, SslStream sslStream, byte[] buffer, CancellationToken cancellationToken)
        {
            var messages = await sslStream.ReadMessagesAsync(buffer, cancellationToken);
            foreach (var message in messages)
            {
                messageQueue.Enqueue(message);
            }
        }
    }
}

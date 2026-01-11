using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    public static class OpenSocketQuerier
    {
        public static async Task<string> QueryAsync(string appId, string openSocketId, string question, string host = "127.0.0.1", int port = 6376, int timeoutMs = 5000)
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = timeoutMs;
            await client.ConnectAsync(host, port).ConfigureAwait(false);

            using NetworkStream stream = client.GetStream();
            string packet = appId + "-" + openSocketId + "&*&" + question;
            byte[] buffer = Encoding.UTF8.GetBytes(packet);
            await stream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            byte[] respBuffer = new byte[4096];
            int read = await stream.ReadAsync(respBuffer, 0, respBuffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("No response from KnotLink.");
            }

            return Encoding.UTF8.GetString(respBuffer, 0, read);
        }
    }
}

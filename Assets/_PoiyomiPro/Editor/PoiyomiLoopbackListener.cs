using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Receives the login code on this computer. The website sends the browser here after login, so
    /// someone who only has the session ID, such as whoever sent a patron a login link, never gets the code.
    /// </summary>
    internal sealed class PoiyomiLoopbackListener : IDisposable
    {
        private const int READ_TIMEOUT_MS = 30000;
        private const int MAX_REQUEST_LINE = 4096;

        private readonly TcpListener listener;
        private readonly TaskCompletionSource<string> code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string sessionId;

        public int Port { get; }
        public Task<string> Code => code.Task;

        public PoiyomiLoopbackListener()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public void Listen(string expectedSessionId)
        {
            sessionId = expectedSessionId;
            _ = AcceptClients();
        }

        public void Dispose()
        {
            listener.Stop();
        }

        // Runs off the main thread so the browser isn't left waiting while Unity is in the background
        private async Task AcceptClients()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = HandleClient(client);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();

                    // Browsers can open a connection and send nothing on it, so nothing here may wait on one forever
                    var read = ReadRequestLine(stream);
                    if (await Task.WhenAny(read, Task.Delay(READ_TIMEOUT_MS)).ConfigureAwait(false) != read)
                        return;

                    var receivedCode = ParseCode(await read.ConfigureAwait(false));
                    var response = receivedCode != null
                        ? $"HTTP/1.1 302 Found\r\nLocation: {PoiyomiProConfig.WEB_BASE}/unity-success\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

                    if (receivedCode != null)
                    {
                        code.TrySetResult(receivedCode);
                    }
                }
            }
            catch (Exception e) when (e is System.IO.IOException || e is ObjectDisposedException || e is SocketException)
            {
                // The browser went away; the login times out if it never comes back
            }
        }

        private static async Task<string> ReadRequestLine(NetworkStream stream)
        {
            var buffer = new byte[MAX_REQUEST_LINE];
            var total = 0;

            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;

                var text = Encoding.ASCII.GetString(buffer, 0, total);
                var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (lineEnd >= 0)
                    return text.Substring(0, lineEnd);
            }

            return "";
        }

        /// <summary>
        /// The code from "GET /callback?sessionId=...&amp;code=... HTTP/1.1", or null for anything
        /// else, including a request for a different session.
        /// </summary>
        private string ParseCode(string requestLine)
        {
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET")
                return null;

            var query = parts[1].Split(new[] { '?' }, 2);
            if (query.Length != 2 || query[0] != "/callback")
                return null;

            string receivedSession = null;
            string receivedCode = null;

            foreach (var pair in query[1].Split('&'))
            {
                var field = pair.Split(new[] { '=' }, 2);
                if (field.Length != 2)
                    continue;

                if (field[0] == "sessionId")
                    receivedSession = Uri.UnescapeDataString(field[1]);
                else if (field[0] == "code")
                    receivedCode = Uri.UnescapeDataString(field[1]);
            }

            return receivedSession == sessionId && !string.IsNullOrEmpty(receivedCode) ? receivedCode : null;
        }
    }
}
